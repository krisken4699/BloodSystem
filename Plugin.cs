using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FistVR;
using HarmonyLib;
using UnityEngine;

namespace BloodSystem
{
    struct DotData
    {
        public Vector3 Pos, Norm;
        public float   R;
        public DotData(Vector3 pos, Vector3 norm, float r) { Pos=pos; Norm=norm; R=r; }
    }

    [BepInPlugin("h3vr.invent60.bloodsystem", "Blood System", "1.0.0")]
    public class BloodSystemPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource    Log;
        internal static BloodSystemPlugin  _instance;

        internal static ConfigEntry<bool>  CfgEnabled;
        internal static ConfigEntry<float> CfgLifetime;
        internal static ConfigEntry<int>   CfgRayCount;
        internal static ConfigEntry<float> CfgConeAngle;
        internal static ConfigEntry<float> CfgDotSize;
        internal static ConfigEntry<float> CfgDotGrow;
        internal static ConfigEntry<float> CfgRange;

        internal static readonly Color _mustard = new Color(0.9f, 0.8f, 0f, 1f);

        // Materials.
        internal static Material _dotMat;      // particles: spray + drip (Sprites/Default + splatter PNG)
        static Material          _splatDotMat; // projection dots PS (Sprites/Default + white soft circle)
        // Per-color mesh decal materials (used only for drip floor stains).
        static readonly Dictionary<Color, Material> _meshMatCache = new Dictionary<Color, Material>();

        // Spray PSes: pellets (fast/small) + fog (slow/large/splatter texture).
        static ParticleSystem _pelletPS;
        static ParticleSystem _fogPS;

        // Weighted splatter distribution pre-computed from PNG.
        // _splatterUVs[i] = (u,v) in [-1,1]x[-1,1], _cumWeights[i] = cumulative weight up to i.
        static Vector2[] _splatterUVs;
        static float[]   _cumWeights;

        void Awake()
        {
            Log       = Logger;
            _instance = this;

            CfgEnabled   = Config.Bind("Blood", "Enabled",         true,   "Toggle all blood effects.");
            CfgLifetime  = Config.Bind("Blood", "Lifetime seconds", 30f,    "How long blood decals last.");
            CfgRayCount  = Config.Bind("Blood", "Rays per shot",    2000,   "Number of splatter rays. Higher = more resolution.");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",  60f,    "Blood spread half-angle in degrees.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",  0.015f, "Base dot radius in metres.");
            CfgDotGrow   = Config.Bind("Blood", "Dot grow per metre", 0.0005f, "Extra radius per metre of ray distance.");
            CfgRange     = Config.Bind("Blood", "Range metres",     50f,    "Max splatter distance.");

            // Load splatter PNG. Used for particle texture AND analytical projection CDF.
            Texture2D splatTex = LoadFirstPng();

            if (!ReferenceEquals(splatTex, null))
            {
                BuildSampleData(splatTex);
                Log.LogInfo("[BloodSystem] PNG loaded, pool=" + (_splatterUVs != null ? _splatterUVs.Length.ToString() : "0"));
            }
            else
            {
                Log.LogWarning("[BloodSystem] No PNG in plugin folder — generating uniform grid fallback.");
                BuildFallbackGrid(320); // 320x320 = ~102K points uniform
            }

            // Particle material: Sprites/Default + splatter PNG (spray + drip).
            Shader spriteShader = Shader.Find("Sprites/Default");
            if (ReferenceEquals(spriteShader, null)) spriteShader = Shader.Find("Particles/Alpha Blended");
            if (!ReferenceEquals(spriteShader, null))
            {
                _dotMat = new Material(spriteShader);
                if (!ReferenceEquals(splatTex, null)) _dotMat.mainTexture = splatTex;

                // Projection dot material: same shader, white gaussian circle.
                // Particle color field carries blood tint: Sprites/Default = tex × vertexColor.
                _splatDotMat = new Material(spriteShader);
                _splatDotMat.mainTexture = MakeColoredSoftCircle(64, Color.white);
            }
            Log.LogInfo("[BloodSystem] spriteShader=" + (spriteShader != null ? spriteShader.name : "NULL"));

            BuildSprayPSes();
            new Harmony("h3vr.invent60.bloodsystem").PatchAll(typeof(BloodSystemPatches));
            Log.LogInfo("[BloodSystem] 1.0.0 ready.");
        }

        // ── Startup helpers ───────────────────────────────────────────────────────

        static Texture2D LoadFirstPng()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (string f in Directory.GetFiles(dir, "*.png"))
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (t.LoadImage(File.ReadAllBytes(f))) return t;
                }
            }
            catch { }
            return null;
        }

        // Pre-compute weighted CDF from splatter image.
        // Weight = alpha * (1 - luminance): dark + opaque = heavy weight.
        // GetPixels() in Unity returns rows bottom-to-top, y=0 is bottom of image.
        static void BuildSampleData(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            Color[] pixels = tex.GetPixels();

            var uvList  = new List<Vector2>();
            var wList   = new List<float>();
            float cumul = 0f;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color c = pixels[y * w + x];
                float lum    = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                float weight = c.a * (1f - lum);
                if (weight < 0.02f) continue;

                float u =  ((float)x / (w - 1)) * 2f - 1f;  // [-1,1], left→right
                float v =  ((float)y / (h - 1)) * 2f - 1f;  // [-1,1], bottom→top
                cumul += weight;
                uvList.Add(new Vector2(u, v));
                wList.Add(cumul);
            }

            _splatterUVs = uvList.ToArray();
            _cumWeights  = wList.ToArray();
        }

        // O(log n) sample from the weighted distribution via binary search on CDF.
        static Vector2 SampleSplatter()
        {
            if (_splatterUVs == null || _splatterUVs.Length == 0)
                return new Vector2(UnityEngine.Random.Range(-1f, 1f),
                                   UnityEngine.Random.Range(-1f, 1f));

            float r  = UnityEngine.Random.Range(0f, _cumWeights[_cumWeights.Length - 1]);
            int lo = 0, hi = _cumWeights.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_cumWeights[mid] < r) lo = mid + 1; else hi = mid;
            }
            return _splatterUVs[lo];
        }

        // Soft gaussian circle with blood color baked into RGB. Alpha = gaussian falloff.
        // Baking color avoids any dependency on vertex colors or _TintColor shader properties.
        static Texture2D MakeColoredSoftCircle(int size, Color col)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c   = (size - 1) * 0.5f;
            Color[] pix = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float a  = Mathf.Clamp01(Mathf.Exp(-(dx*dx + dy*dy) * 2.5f));
                pix[y * size + x] = new Color(col.r, col.g, col.b, a);
            }
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        // Uniform grid fallback when no PNG is present in the plugin folder.
        static void BuildFallbackGrid(int side)
        {
            var uvList = new List<Vector2>(side * side);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                float u = ((float)x / (side - 1)) * 2f - 1f;
                float v = ((float)y / (side - 1)) * 2f - 1f;
                uvList.Add(new Vector2(u, v));
            }
            _splatterUVs = uvList.ToArray();
            _cumWeights  = new float[0]; // unused in analytical projection
        }

        void BuildSprayPSes()
        {
            // ── Pellet PS: fast, small, tight cone — the visible spray droplets ──────
            {
                var go = new GameObject("BloodPelletPS");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _pelletPS = go.AddComponent<ParticleSystem>();
                var main = _pelletPS.main;
                main.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.9f);
                main.startSpeed      = new ParticleSystem.MinMaxCurve(4f, 22f);
                main.startSize       = new ParticleSystem.MinMaxCurve(0.006f, 0.022f);
                main.maxParticles    = 3000;
                main.gravityModifier = 1.5f;
                main.loop            = false;
                main.playOnAwake     = false;
                main.startColor      = new ParticleSystem.MinMaxGradient(_mustard);
                var sh = _pelletPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle     = 35f;   // wider first ring
                sh.radius    = 0.04f;
                var psr = _pelletPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_dotMat, null)) psr.material = _dotMat;
                go.SetActive(true);
            }

            // ── Fog PS: wide billowing cloud, fades out by ~0.6s ─────────────────────
            {
                var go = new GameObject("BloodFogPS");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _fogPS = go.AddComponent<ParticleSystem>();
                var main = _fogPS.main;
                main.startLifetime   = new ParticleSystem.MinMaxCurve(0.5f, 0.8f);   // short — all gone by ~0.8s
                main.startSpeed      = new ParticleSystem.MinMaxCurve(1.5f, 5.0f);   // faster outward burst
                main.startSize       = new ParticleSystem.MinMaxCurve(0.08f, 0.42f); // large + varied
                main.startRotation   = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
                main.maxParticles    = 500;
                main.gravityModifier = new ParticleSystem.MinMaxCurve(0.1f, 0.45f);  // low — drifts outward, falls gently
                main.loop            = false;
                main.playOnAwake     = false;
                main.startColor      = new ParticleSystem.MinMaxGradient(_mustard);
                var sh = _fogPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle     = 23f;
                sh.radius    = 0.05f;
                // Alpha fades 1→0 over lifetime so particles dissolve rather than pop out.
                var col = _fogPS.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
                );
                col.color = new ParticleSystem.MinMaxGradient(grad);
                // Slow spin for organic tumbling.
                var rotOvLt = _fogPS.rotationOverLifetime;
                rotOvLt.enabled = true;
                rotOvLt.z       = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f);
                var psr = _fogPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_dotMat, null)) psr.material = _dotMat;
                // Fog particles leave floor/surface stains when they land.
                go.AddComponent<BloodDripStainer>().BloodColor = _mustard;
                go.SetActive(true);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        internal static bool IsSourceSosig(Collider col, Sosig src)
        {
            if (ReferenceEquals(src, null)) return false;
            SosigLink lk = col.GetComponentInParent<SosigLink>();
            if (lk != null && !ReferenceEquals(lk.S, null) && ReferenceEquals(lk.S, src)) return true;
            if (col.transform.IsChildOf(src.transform)) return true;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null)
            {
                var tag = rb.GetComponent<SosigGibTag>();
                if (tag != null && ReferenceEquals(tag.SourceSosig, src)) return true;
            }
            return false;
        }

        internal static Color GetSosigBloodColor(Sosig s)
        {
            if (ReferenceEquals(s, null) || s == null) return _mustard;
            try
            {
                FieldInfo fi;
                fi = typeof(Sosig).GetField("m_bloodColor", BindingFlags.NonPublic | BindingFlags.Instance);
                if (!ReferenceEquals(fi, null)) return (Color)fi.GetValue(s);
                fi = typeof(Sosig).GetField("BloodColor", BindingFlags.Public | BindingFlags.Instance);
                if (!ReferenceEquals(fi, null)) return (Color)fi.GetValue(s);
            }
            catch { }
            return _mustard;
        }

        // ── Mesh building ─────────────────────────────────────────────────────────

        static Material GetMeshMat(Color col)
        {
            Material m;
            if (!_meshMatCache.TryGetValue(col, out m) || ReferenceEquals(m, null))
            {
                // Try every transparent shader. Sprites/Default is guaranteed present
                // (it's what _dotMat uses). Last resort: clone _dotMat's shader directly.
                Shader sh = Shader.Find("Alloy/Decal");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Unlit/Transparent");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Alpha Blended");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Transparent/Diffuse");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Sprites/Default");
                if (ReferenceEquals(sh, null) && !ReferenceEquals(_dotMat, null)) sh = _dotMat.shader;
                if (ReferenceEquals(sh, null))
                {
                    Log.LogError("[BloodSystem] GetMeshMat: no usable shader — decals disabled.");
                    _meshMatCache[col] = null; return null;
                }
                m = new Material(sh);
                m.mainTexture = MakeColoredSoftCircle(64, col);
                if (m.HasProperty("_TintColor"))
                    m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 1f));
                Log.LogInfo("[BloodSystem] decalShader=" + sh.name + " col=" + col);
                _meshMatCache[col] = m;
            }
            return m;
        }

        // Batches all hit-point dots into one merged mesh per parent.
        static void BuildDotMesh(List<DotData> dots, Transform parent, Color col)
        {
            if (dots.Count == 0) return;
            Material mat = GetMeshMat(col);
            if (ReferenceEquals(mat, null)) return;
            const int MAX = 16383; // 4 verts × 16383 = 65532 < 65535
            int total = dots.Count;
            for (int start = 0; start < total; start += MAX)
            {
                int count = Mathf.Min(MAX, total - start);
                var verts = new Vector3[count * 4];
                var uvs   = new Vector2[count * 4];
                var tris  = new int    [count * 6];

                for (int i = 0; i < count; i++)
                {
                    DotData d    = dots[start + i];
                    Vector3 norm = d.Norm; float r = d.R;
                    Vector3 qup  = Mathf.Abs(Vector3.Dot(norm, Vector3.up)) > 0.9f
                        ? Vector3.forward : Vector3.up;
                    Quaternion rot = Quaternion.LookRotation(-norm, qup)
                                   * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                    Vector3 qr = rot * Vector3.right * r;
                    Vector3 qu = rot * Vector3.up    * r;
                    Vector3 bp = d.Pos + norm * 0.003f;

                    Vector3 c0 = bp - qr - qu, c1 = bp + qr - qu,
                            c2 = bp + qr + qu, c3 = bp - qr + qu;
                    if (parent != null)
                    {
                        c0 = parent.InverseTransformPoint(c0);
                        c1 = parent.InverseTransformPoint(c1);
                        c2 = parent.InverseTransformPoint(c2);
                        c3 = parent.InverseTransformPoint(c3);
                    }

                    int v = i * 4;
                    verts[v]=c0; verts[v+1]=c1; verts[v+2]=c2; verts[v+3]=c3;
                    uvs[v]=new Vector2(0,0); uvs[v+1]=new Vector2(1,0);
                    uvs[v+2]=new Vector2(1,1); uvs[v+3]=new Vector2(0,1);
                    int t = i * 6;
                    tris[t]=v; tris[t+1]=v+1; tris[t+2]=v+2;
                    tris[t+3]=v; tris[t+4]=v+2; tris[t+5]=v+3;
                }

                var mesh = new Mesh();
                mesh.vertices  = verts;
                mesh.uv        = uvs;
                mesh.triangles = tris;
                mesh.RecalculateBounds();

                var go = new GameObject("BD");
                if (parent != null) go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().mesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.material          = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows    = false;
                UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            }
        }

        // ── Spawn functions ───────────────────────────────────────────────────────

        // Fires N CDF-weighted rays from exit wound. Hits collected into a ParticleSystem
        // via SetParticles — uses the same particle renderer as spray (confirmed working).
        // Only fires on exit wound (dot < 0 check in PostMove).
        internal static void SpawnProjection(Vector3 exitPt, Vector3 projDir, Sosig srcSosig)
        {
            if (!CfgEnabled.Value) return;
            if (ReferenceEquals(_splatDotMat, null) && ReferenceEquals(_dotMat, null)) return;
            try
            {
                if (ReferenceEquals(_splatterUVs, null) || _splatterUVs.Length == 0)
                    BuildFallbackGrid(200);

                Color   col     = GetSosigBloodColor(srcSosig);
                Vector3 fwd     = projDir.normalized;
                float   tanHalf = Mathf.Tan(CfgConeAngle.Value * Mathf.Deg2Rad);
                float   range   = CfgRange.Value;

                Vector3 worldUp = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
                Vector3 right   = Vector3.Cross(worldUp, fwd).normalized;
                Vector3 up      = Vector3.Cross(fwd, right);

                int N      = Mathf.Max(1, CfgRayCount.Value);
                int stride = Mathf.Max(1, _splatterUVs.Length / N);

                var particles = new List<ParticleSystem.Particle>(N);
                for (int i = 0; i < _splatterUVs.Length; i += stride)
                {
                    Vector2 uv  = _splatterUVs[i];
                    Vector3 dir = (fwd + right * uv.x * tanHalf + up * uv.y * tanHalf).normalized;

                    RaycastHit h;
                    if (!Physics.Raycast(exitPt, dir, out h, range)) continue;
                    if (IsSourceSosig(h.collider, srcSosig)) continue;
                    if (h.collider.GetComponentInParent<SosigWeapon>() != null) continue;

                    float dotR = CfgDotSize.Value + h.distance * CfgDotGrow.Value;
                    var p = new ParticleSystem.Particle();
                    p.position         = h.point + h.normal * 0.005f;
                    p.velocity         = Vector3.zero;
                    p.startLifetime    = CfgLifetime.Value;
                    p.remainingLifetime = CfgLifetime.Value;
                    p.startSize        = dotR * 2f;
                    p.startColor       = col;
                    particles.Add(p);
                }

                Log.LogInfo("[BloodSystem] proj N=" + N + " hits=" + particles.Count);
                if (particles.Count == 0) return;

                var arr = particles.ToArray();
                var go  = new GameObject("BloodSplat");
                var ps  = go.AddComponent<ParticleSystem>();
                var mn  = ps.main;
                mn.loop              = false;
                mn.playOnAwake       = false;
                mn.maxParticles      = arr.Length + 1;
                mn.startLifetime     = new ParticleSystem.MinMaxCurve(CfgLifetime.Value);
                mn.startSpeed        = new ParticleSystem.MinMaxCurve(0f);
                mn.gravityModifier   = 0f;
                mn.simulationSpace   = ParticleSystemSimulationSpace.World;
                var em = ps.emission; em.enabled = false;
                var psr = ps.GetComponent<ParticleSystemRenderer>();
                psr.material = !ReferenceEquals(_splatDotMat, null) ? _splatDotMat : _dotMat;
                ps.Play();
                ps.SetParticles(arr, arr.Length);
                UnityEngine.Object.Destroy(go, CfgLifetime.Value + 2f);
            }
            catch (System.Exception ex)
            {
                Log.LogError("[BloodSystem] SpawnProjection exception: " + ex);
            }
        }

        internal static void SpawnBloodSpray(Vector3 pos, Vector3 fwd, Color col)
        {
            if (!CfgEnabled.Value) return;
            Quaternion rot = Quaternion.LookRotation(fwd);

            // Pellets: fast, tight inner ring of individual blood drops.
            if (!ReferenceEquals(_pelletPS, null))
            {
                _pelletPS.gameObject.transform.position = pos;
                _pelletPS.gameObject.transform.rotation = rot;
                var m = _pelletPS.main;
                m.startColor = new ParticleSystem.MinMaxGradient(col);
                _pelletPS.Emit(700);
            }

            // Fog: slow billowing outer cloud. Semi-transparent, splatter texture per particle.
            if (!ReferenceEquals(_fogPS, null))
            {
                _fogPS.gameObject.transform.position = pos;
                _fogPS.gameObject.transform.rotation = rot;
                var m = _fogPS.main;
                Color fogCol = new Color(col.r, col.g, col.b, 0.6f);
                m.startColor = new ParticleSystem.MinMaxGradient(fogCol);
                var stainer = _fogPS.GetComponent<BloodDripStainer>();
                if (stainer != null) stainer.BloodColor = col;
                _fogPS.Emit(50);
            }
        }

        internal static void SpawnDrip(Vector3 pos, Color col)
        {
            if (!CfgEnabled.Value) return;
            var go   = new GameObject("BloodDrip");
            go.transform.position = pos;
            var ps   = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration        = 0.5f;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.5f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(0.3f, 2f);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.005f, 0.012f);
            main.gravityModifier = 3f;
            main.maxParticles    = 60;
            main.loop            = false;
            main.playOnAwake     = false;
            main.startColor      = new ParticleSystem.MinMaxGradient(col);
            var em = ps.emission;
            em.enabled      = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(80f);
            var sh = ps.shape;
            sh.enabled   = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle     = 25f;
            sh.radius    = 0.01f;
            var psr = go.GetComponent<ParticleSystemRenderer>();
            if (psr != null && !ReferenceEquals(_dotMat, null)) psr.material = _dotMat;
            var stainer = go.AddComponent<BloodDripStainer>();
            stainer.BloodColor = col;
            ps.Play();
            UnityEngine.Object.Destroy(go, 2f);
        }

        internal static void SpawnDripStain(Vector3 pos, Vector3 normal, Color col, float scale = 1f)
        {
            Material mat = GetMeshMat(col);
            if (ReferenceEquals(mat, null)) return;
            float r = UnityEngine.Random.Range(0.02f, 0.055f) * scale;
            Vector3 qup = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f
                ? Vector3.forward : Vector3.up;
            Quaternion q = Quaternion.LookRotation(-normal, qup)
                         * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            Vector3 qr = q * Vector3.right * r;
            Vector3 qu = q * Vector3.up    * r;
            Vector3 bp = pos + normal * 0.003f;

            var mesh = new Mesh();
            mesh.vertices  = new[] { bp-qr-qu, bp+qr-qu, bp+qr+qu, bp-qr+qu };
            mesh.uv        = new[] { new Vector2(0,0), new Vector2(1,0),
                                     new Vector2(1,1), new Vector2(0,1) };
            mesh.colors    = new[] { col, col, col, col };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            var go = new GameObject("DX");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
        }

        internal static void SpawnExplosionSplatter(Vector3 center, Sosig srcSosig)
        {
            if (!CfgEnabled.Value) return;
            Color col = GetSosigBloodColor(srcSosig);
            const int N = 60; const float range = 3f;
            for (int i = 0; i < N; i++)
            {
                float theta = Mathf.Acos(1f - 2f * (i + 0.5f) / N);
                float phi   = Mathf.PI * (1f + Mathf.Sqrt(5f)) * i;
                Vector3 dir = new Vector3(
                    Mathf.Sin(theta) * Mathf.Cos(phi),
                    Mathf.Sin(theta) * Mathf.Sin(phi),
                    Mathf.Cos(theta));
                RaycastHit h;
                if (!Physics.Raycast(center, dir, out h, range)) continue;
                if (h.distance < 0.3f) continue;
                if (IsSourceSosig(h.collider, srcSosig)) continue;
                if (h.collider.attachedRigidbody != null) continue;
                SpawnDripStain(h.point, h.normal, col, 1.8f);
            }
        }
    }

    public class BloodDripStainer : MonoBehaviour
    {
        public Color BloodColor = new Color(0.9f, 0.8f, 0f, 1f);

        ParticleSystem            _ps;
        ParticleSystem.Particle[] _buf;
        int _skip;

        void Start()
        {
            _ps = GetComponent<ParticleSystem>();
            if (_ps != null) _buf = new ParticleSystem.Particle[_ps.main.maxParticles];
        }

        void Update()
        {
            if (_ps == null || _buf == null) return;
            if (++_skip < 3) return;
            _skip = 0;
            int  n     = _ps.GetParticles(_buf);
            if (n == 0) return;
            bool local = _ps.main.simulationSpace == ParticleSystemSimulationSpace.Local;
            bool dirty = false;
            for (int i = 0; i < n; i++)
            {
                if (_buf[i].remainingLifetime > 0.15f) continue;
                Vector3 pos = local
                    ? _ps.transform.TransformPoint(_buf[i].position)
                    : _buf[i].position;
                RaycastHit h;
                if (Physics.Raycast(pos + Vector3.up * 0.05f, Vector3.down, out h, 0.25f)
                    && h.collider.GetComponentInParent<SosigLink>() == null)
                {
                    BloodSystemPlugin.SpawnDripStain(h.point, h.normal, BloodColor);
                    _buf[i].remainingLifetime = 0f;
                    dirty = true;
                }
            }
            if (dirty) _ps.SetParticles(_buf, n);
        }
    }

    public class SosigGibTag : MonoBehaviour
    {
        public Sosig SourceSosig;
    }

    public class SplatterTracker : MonoBehaviour
    {
        public Collider PrevCollider;
        public Sosig    LastSosig;
        public bool     PrevWasAliveSosig;
        public Vector3  LastSosigLinkPos;
        public Vector3  LastBulletDir;
    }

    static class BloodSystemPatches
    {
        static readonly FieldInfo FLastColliderHit =
            typeof(BallisticProjectile).GetField("m_lastColliderHit",
                BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FHit =
            typeof(BallisticProjectile).GetField("m_hit",
                BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FVelocity =
            typeof(BallisticProjectile).GetField("m_velocity",
                BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Ok => !ReferenceEquals(FLastColliderHit, null)
                       && !ReferenceEquals(FHit,             null)
                       && !ReferenceEquals(FVelocity,        null);

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPrefix]
        static void PreMove(BallisticProjectile __instance)
        {
            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker is null)
                tracker = __instance.gameObject.AddComponent<SplatterTracker>();

            var col = FLastColliderHit.GetValue(__instance) as Collider;
            tracker.PrevCollider = col;

            var vel = (Vector3)FVelocity.GetValue(__instance);
            if (vel.magnitude > 0.01f) tracker.LastBulletDir = vel.normalized;

            tracker.PrevWasAliveSosig = false;
            if (!ReferenceEquals(col, null) && col != null)
            {
                var link = col.GetComponentInParent<SosigLink>();
                if (link != null && link.S != null)
                {
                    tracker.PrevWasAliveSosig = true;
                    tracker.LastSosigLinkPos  = link.transform.position;
                    tracker.LastSosig         = link.S;
                }
            }
        }

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPostfix]
        static void PostMove(BallisticProjectile __instance)
        {
            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker is null) return;

            var currentCollider = FLastColliderHit.GetValue(__instance) as Collider;

            // Segment explosion.
            if (tracker.PrevWasAliveSosig
                && !ReferenceEquals(tracker.PrevCollider, null)
                && tracker.PrevCollider == null)
            {
                tracker.PrevWasAliveSosig = false;
                Vector3 bPos    = tracker.LastSosigLinkPos;
                Vector3 bDir    = tracker.LastBulletDir.magnitude > 0.01f
                                ? tracker.LastBulletDir : Vector3.forward;
                Sosig   expSosig = tracker.LastSosig;
                Color   expCol   = BloodSystemPlugin.GetSosigBloodColor(expSosig);

                if (expSosig != null)
                    foreach (var nc in Physics.OverlapSphere(bPos, 10f))
                    {
                        if (nc.GetComponentInParent<SosigLink>() != null) continue;
                        Rigidbody nrb = nc.attachedRigidbody;
                        if (nrb == null) continue;
                        var t = nrb.GetComponent<SosigGibTag>();
                        if (t == null) t = nrb.gameObject.AddComponent<SosigGibTag>();
                        t.SourceSosig = expSosig;
                    }

                BloodSystemPlugin.SpawnExplosionSplatter(bPos, expSosig);
                BloodSystemPlugin.SpawnBloodSpray(bPos, bDir, expCol);
            }

            if (ReferenceEquals(currentCollider, tracker.PrevCollider)) return;

            var  hit        = (RaycastHit)FHit.GetValue(__instance);
            bool csExists   = !ReferenceEquals(currentCollider, null);
            bool unityAlive = csExists && currentCollider != null;
            if (!unityAlive) return;

            SosigLink shotLink = currentCollider.GetComponentInParent<SosigLink>();
            if (shotLink == null) return;

            float   dot = Vector3.Dot(__instance.transform.position - hit.point, hit.normal);
            Vector3 dir = tracker.LastBulletDir.magnitude > 0.01f
                        ? tracker.LastBulletDir : __instance.transform.forward;
            Sosig srcSosig = shotLink.S;
            Color bloodCol = BloodSystemPlugin.GetSosigBloodColor(srcSosig);

            if (dot < 0f)
            {
                Vector3 segCenter = shotLink.transform.position;
                Vector3 exitPt    = hit.point + dir * 0.35f;
                {
                    RaycastHit xh;
                    if (Physics.Raycast(segCenter, dir, out xh, 2f)
                        && xh.collider.GetComponentInParent<SosigLink>() != null)
                        exitPt = xh.point + dir * 0.02f;
                }
                Vector3 projDir = (exitPt - segCenter).normalized;
                if (projDir.sqrMagnitude < 0.01f) projDir = dir;

                BloodSystemPlugin.SpawnProjection(exitPt, projDir, srcSosig);
                BloodSystemPlugin.SpawnBloodSpray(exitPt, projDir, bloodCol);
                BloodSystemPlugin.SpawnDrip(exitPt, bloodCol);
            }
        }
    }
}
