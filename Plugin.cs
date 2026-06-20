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
        internal static Material        _dotMat;   // particles: spray + drip
        // Per-color mesh decal materials: blood color baked into texture, no vertex color dependency.
        static readonly Dictionary<Color, Material> _meshMatCache = new Dictionary<Color, Material>();

        // Spray PS.
        static ParticleSystem _sprayPS;

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
            CfgRayCount  = Config.Bind("Blood", "Point stride",     2,      "Sample stride through image points (1=full, 2=half, 4=quarter resolution).");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",  60f,    "Blood spread half-angle in degrees.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",  0.015f, "Base dot radius in metres.");
            CfgDotGrow   = Config.Bind("Blood", "Dot grow per metre", 0.0005f, "Extra radius per metre of ray distance.");
            CfgRange     = Config.Bind("Blood", "Range metres",     50f,    "Max splatter distance.");

            // Load splatter PNG. Used for both particle material AND distribution pre-compute.
            Texture2D splatTex = LoadFirstPng();

            // Pre-compute weighted CDF from splatter PNG.
            // Darker + more opaque pixels have higher weight = more rays go there.
            if (!ReferenceEquals(splatTex, null))
                BuildSampleData(splatTex);
            else
                Log.LogWarning("[BloodSystem] No PNG found — using uniform cone sampling.");

            // Particle material: Sprites/Default + splatter PNG (spray + drip).
            Shader spriteShader = Shader.Find("Sprites/Default");
            if (ReferenceEquals(spriteShader, null)) spriteShader = Shader.Find("Transparent/Diffuse");
            if (!ReferenceEquals(spriteShader, null))
            {
                _dotMat = new Material(spriteShader);
                if (!ReferenceEquals(splatTex, null)) _dotMat.mainTexture = splatTex;
            }

            BuildSprayPS();
            new Harmony("h3vr.invent60.bloodsystem").PatchAll(typeof(BloodSystemPatches));
            Log.LogInfo("[BloodSystem] 1.0.0 loaded. splatPool=" + (_splatterUVs != null ? _splatterUVs.Length.ToString() : "0"));
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

        void BuildSprayPS()
        {
            var go = new GameObject("BloodSprayPS");
            DontDestroyOnLoad(go);
            go.SetActive(false);
            _sprayPS = go.AddComponent<ParticleSystem>();
            var main = _sprayPS.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(3f, 16f);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.008f, 0.025f);
            main.maxParticles    = 2000;
            main.gravityModifier = 1.2f;
            main.loop            = false;
            main.playOnAwake     = false;
            main.startColor      = new ParticleSystem.MinMaxGradient(_mustard);
            var sh = _sprayPS.shape;
            sh.enabled   = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle     = 18f;
            sh.radius    = 0.01f;
            var psr = _sprayPS.GetComponent<ParticleSystemRenderer>();
            if (psr != null && !ReferenceEquals(_dotMat, null)) psr.material = _dotMat;
            go.SetActive(true);
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

        // Returns a cached decal Material with blood color baked into the soft-circle texture.
        // Tries Alloy/Decal first (H3VR's PBR stack), falls back to standard transparent shaders.
        static Material GetMeshMat(Color col)
        {
            Material m;
            if (!_meshMatCache.TryGetValue(col, out m) || ReferenceEquals(m, null))
            {
                Shader sh = Shader.Find("Alloy/Decal");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Unlit/Transparent");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Alpha Blended");
                if (ReferenceEquals(sh, null)) sh = Shader.Find("Transparent/Diffuse");
                if (ReferenceEquals(sh, null)) { _meshMatCache[col] = null; return null; }
                m = new Material(sh);
                m.mainTexture = MakeColoredSoftCircle(64, col);
                if (m.HasProperty("_TintColor"))
                    m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 1f));
                Log.LogInfo("[BloodSystem] decalShader=" + sh.name);
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

        // Analytical projection: one center raycast finds wall plane, then all image CDF points
        // project onto that plane analytically. No per-dot raycasts — allows half-image-resolution
        // dot counts without per-frame raycast budget concerns.
        internal static void SpawnProjection(Vector3 exitPt, Vector3 projDir, Sosig srcSosig)
        {
            if (!CfgEnabled.Value) return;
            if (_splatterUVs == null || _splatterUVs.Length == 0) return;

            Color   col  = GetSosigBloodColor(srcSosig);
            Vector3 fwd  = projDir.normalized;

            // Single center ray to locate wall.
            RaycastHit wallHit;
            if (!Physics.Raycast(exitPt, fwd, out wallHit, CfgRange.Value)) return;
            if (IsSourceSosig(wallHit.collider, srcSosig)) return;
            if (wallHit.collider.GetComponentInParent<SosigWeapon>() != null) return;

            // Build wall tangent frame.
            Vector3 wNorm  = wallHit.normal;
            Vector3 qup    = Mathf.Abs(Vector3.Dot(wNorm, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 wRight = Vector3.Cross(qup, wNorm).normalized;
            Vector3 wUp    = Vector3.Cross(wNorm, wRight);

            // Stamp radius: cone spread at hit distance.
            float stampR = wallHit.distance * Mathf.Tan(CfgConeAngle.Value * Mathf.Deg2Rad);
            float dotR   = CfgDotSize.Value;
            // Offset slightly off the wall to avoid z-fight.
            Vector3 center = wallHit.point + wNorm * 0.003f;

            Rigidbody hitRb  = wallHit.collider.attachedRigidbody;
            bool      onSosig = hitRb != null && hitRb.GetComponentInParent<SosigLink>() != null;
            Transform par    = onSosig ? hitRb.transform : null;

            // Walk CDF array at configured stride (2 = half-image resolution).
            int stride = Mathf.Max(1, CfgRayCount.Value);
            var dots = new List<DotData>(_splatterUVs.Length / stride + 1);
            for (int i = 0; i < _splatterUVs.Length; i += stride)
            {
                Vector2 uv  = _splatterUVs[i];
                Vector3 pos = center + wRight * (uv.x * stampR) + wUp * (uv.y * stampR);
                dots.Add(new DotData(pos, wNorm, dotR));
            }

            BuildDotMesh(dots, par, col);
            Log.LogInfo("[BloodSystem] proj " + dots.Count + " dots stampR=" + stampR.ToString("F2") + "m");
        }

        internal static void SpawnBloodSpray(Vector3 pos, Vector3 fwd, Color col)
        {
            if (ReferenceEquals(_sprayPS, null)) return;
            _sprayPS.gameObject.transform.position = pos;
            _sprayPS.gameObject.transform.rotation = Quaternion.LookRotation(fwd);
            var main = _sprayPS.main;
            main.startColor = new ParticleSystem.MinMaxGradient(col);
            _sprayPS.Emit(500);
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
