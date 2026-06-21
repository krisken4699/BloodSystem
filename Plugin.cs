using System;
using System.Collections;
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
        public float   Dark; // 0=bright/white area, 1=dark area — used to darken dot vertex color
        public DotData(Vector3 pos, Vector3 norm, float r, float dark)
        {
            Pos = pos; Norm = norm; R = r; Dark = dark;
        }
    }

    [BepInPlugin("h3vr.invent60.bloodsystem", "Blood System", "3.0.0")]
    public class BloodSystemPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource   Log;
        internal static BloodSystemPlugin _instance;

        internal static ConfigEntry<bool>  CfgEnabled;
        internal static ConfigEntry<float> CfgLifetime;
        internal static ConfigEntry<int>   CfgRayCount;
        internal static ConfigEntry<float> CfgConeAngle;
        internal static ConfigEntry<float> CfgDotSize;
        internal static ConfigEntry<float> CfgRange;

        internal static readonly Color _mustardFallback = new Color(0.9f, 0.8f, 0f, 1f);

        // Decal material cache: one Material per blood Color — uses _decalTex (soft circle)
        static readonly Dictionary<Color, Material> _matCache = new Dictionary<Color, Material>();
        static Shader    _bloodShader;
        static bool      _bloodShaderSearched;
        static Material  _decalSourceMat;
        static bool      _decalSourceSearched;

        // _decalTex  = procedural gaussian soft circle — used for dot mesh quads, drip stain quads, pellet particles
        // Blood PNGs are used ONLY for CDF ray-direction sampling and fog particle texture
        static Texture2D   _decalTex;
        static Texture2D   _firstBloodTex; // first valid PNG loaded — used as fog particle texture

        // CDF data built from ALL blood PNGs combined (equal-contribution, aspect-correct)
        static Vector2[] _splatterUVs;
        static float[]   _cumWeights;
        static float[]   _splatterDarks; // per-sample darkness from source pixel luminance

        // Spray: two persistent PSes
        static ParticleSystem _pelletPS;   // small round drops — uses soft-circle tex
        static ParticleSystem _fogPS;      // large puffs — uses first blood PNG
        static Material       _fogMat;
        static Material       _pelletMat;

        // NGA SosigIntegrityConfigs color (one-time check)
        static bool  _ngaChecked;
        static bool  _ngaKetchup;
        static Color _ngaColor;

        // Reflected Sosig.Mustard field
        static FieldInfo _fiMustard;
        static bool      _mustardFieldSearched;

        void Awake()
        {
            Log       = Logger;
            _instance = this;

            CfgEnabled   = Config.Bind("Blood", "Enabled",          true,   "Toggle all blood effects.");
            CfgLifetime  = Config.Bind("Blood", "Lifetime seconds",  30f,    "How long splash and drip stains last before despawning.");
            CfgRayCount  = Config.Bind("Blood", "Rays per shot",     200,    "Splash ray count. Around 1500 was confirmed fine in testing; 200 is a safe default.");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",   10f,    "Half-angle in degrees of the splash cone.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",   0.008f, "Base radius of each splash dot in metres. Scales linearly to 3x at 20 metres.");
            CfgRange     = Config.Bind("Blood", "Range metres",      15f,    "Maximum splash distance in metres.");

            // Soft-circle decal texture — all dot/stain rendering uses this (NOT the blood PNGs)
            _decalTex = MakeSoftCircle(64);

            // Load ALL blood PNGs and build combined CDF
            var allTextures = LoadAllPngs();
            if (allTextures.Count > 0)
            {
                _firstBloodTex = allTextures[0];
                BuildSampleDataFromAll(allTextures);
                Log.LogInfo("[BloodSystem] " + allTextures.Count + " PNG(s) loaded. CDF points="
                    + (_splatterUVs != null ? _splatterUVs.Length.ToString() : "0"));
            }
            else
            {
                BuildFallbackGrid(200);
                Log.LogWarning("[BloodSystem] No PNG found in plugin folder — using uniform fallback grid.");
            }

            _fogMat    = BuildFogMaterial();
            _pelletMat = BuildPelletMaterial();
            BuildSprayPSes();

            new Harmony("h3vr.invent60.bloodsystem").PatchAll(typeof(BloodSystemPatches));
            Log.LogInfo("[BloodSystem] 3.0.0 loaded.");
        }

        // ── PNG loading ───────────────────────────────────────────────────────────

        static List<Texture2D> LoadAllPngs()
        {
            var result = new List<Texture2D>();
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (string f in Directory.GetFiles(dir, "*.png"))
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (t.LoadImage(File.ReadAllBytes(f)))
                    {
                        t.filterMode = FilterMode.Trilinear;
                        result.Add(t);
                    }
                }
            }
            catch (Exception ex) { BloodSystemPlugin.Log.LogWarning("[BloodSystem] LoadAllPngs: " + ex.Message); }
            return result;
        }

        // Procedural gaussian soft-circle 64×64: white center, alpha falls to 0 at edge.
        // Used for dot quads, drip stain quads, and pellet spray particles.
        static Texture2D MakeSoftCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            var pix = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c;
                float dy = (y - c) / c;
                float d2 = dx * dx + dy * dy;
                float a  = d2 > 1f ? 0f : Mathf.Clamp01(Mathf.Exp(-d2 * 3.5f));
                pix[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        // ── CDF build from multiple blood PNGs ────────────────────────────────────
        //
        // Each image contributes equally regardless of resolution or overall brightness.
        // UV mapping is aspect-correct: longest dimension maps to [-1,1], shorter is proportional.
        // Dark+opaque pixels → high sample weight and darker dot color.

        static void BuildSampleDataFromAll(List<Texture2D> textures)
        {
            var uvList   = new List<Vector2>();
            var darkList = new List<float>();
            var wList    = new List<float>();
            float cumul  = 0f;

            foreach (var tex in textures)
            {
                int w = tex.width, h = tex.height;
                Color[] pixels = tex.GetPixels();
                float ar = (float)w / h; // aspect ratio

                // Collect per-image UV points and raw weights
                var imgUVs   = new List<Vector2>(w * h / 4);
                var imgDarks = new List<float>(w * h / 4);
                var imgWts   = new List<float>(w * h / 4);
                float imgTotal = 0f;

                for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                {
                    Color c   = pixels[py * w + px];
                    float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                    float wt  = c.a * (1f - lum);
                    if (wt < 0.02f) continue;

                    // Aspect-correct UV: fit longest axis to [-1,1], scale other axis proportionally
                    float u, v;
                    if (ar >= 1f) // landscape or square: full width, compressed height
                    {
                        u = ((float)px / (w - 1)) * 2f - 1f;
                        v = (((float)py / (h - 1)) * 2f - 1f) / ar;
                    }
                    else // portrait: compressed width, full height
                    {
                        u = (((float)px / (w - 1)) * 2f - 1f) * ar;
                        v = ((float)py / (h - 1)) * 2f - 1f;
                    }

                    imgUVs.Add(new Vector2(u, v));
                    imgDarks.Add(1f - lum);   // 1=black pixel area, 0=white pixel area
                    imgWts.Add(wt);
                    imgTotal += wt;
                }

                if (imgTotal < 0.001f || imgUVs.Count == 0) continue;

                // Normalize this image's contribution to 1.0 total weight so all images contribute equally
                float norm = 1f / imgTotal;
                for (int i = 0; i < imgUVs.Count; i++)
                {
                    cumul += imgWts[i] * norm;
                    uvList.Add(imgUVs[i]);
                    darkList.Add(imgDarks[i]);
                    wList.Add(cumul);
                }
            }

            _splatterUVs   = uvList.ToArray();
            _splatterDarks = darkList.ToArray();
            _cumWeights    = wList.ToArray();
        }

        static void BuildFallbackGrid(int side)
        {
            var uvList   = new List<Vector2>(side * side);
            var darkList = new List<float>(side * side);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                uvList.Add(new Vector2(((float)x / (side - 1)) * 2f - 1f,
                                       ((float)y / (side - 1)) * 2f - 1f));
                darkList.Add(0.8f);
            }
            _splatterUVs   = uvList.ToArray();
            _splatterDarks = darkList.ToArray();
            _cumWeights    = new float[0];
        }

        // O(log n) binary search on CDF → returns UV + darkness at that sample
        static void SampleSplatter(out Vector2 uv, out float dark)
        {
            if (_splatterUVs == null || _splatterUVs.Length == 0)
            {
                uv   = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f));
                dark = 0.8f;
                return;
            }
            int idx;
            if (_cumWeights == null || _cumWeights.Length == 0)
            {
                idx = UnityEngine.Random.Range(0, _splatterUVs.Length);
            }
            else
            {
                float r = UnityEngine.Random.Range(0f, _cumWeights[_cumWeights.Length - 1]);
                int lo = 0, hi = _cumWeights.Length - 1;
                while (lo < hi) { int mid = (lo + hi) >> 1; if (_cumWeights[mid] < r) lo = mid + 1; else hi = mid; }
                idx = lo;
            }
            uv   = _splatterUVs[idx];
            dark = (!ReferenceEquals(_splatterDarks, null) && idx < _splatterDarks.Length)
                 ? _splatterDarks[idx] : 0.8f;
        }

        // ── Shader / material for dot/stain meshes ────────────────────────────────

        // Alloy shader found at runtime (if any) — cached after first search
        static Shader _alloyShader;
        static bool   _alloyShaderSearched;

        static Shader FindAlloyShader()
        {
            if (_alloyShaderSearched) return _alloyShader;
            _alloyShaderSearched = true;
            string[] candidates = { "Alloy/Core", "Alloy Mods/Masked Core", "Alloy/Unlit",
                                    "Alloy/Triplanar", "Alloy Mods/Triplanar" };
            foreach (string name in candidates)
            {
                Shader s = Shader.Find(name);
                if (!ReferenceEquals(s, null))
                {
                    _alloyShader = s;
                    Log.LogInfo("[BloodSystem] alloyShader=" + name);
                    return s;
                }
            }
            Log.LogInfo("[BloodSystem] No Alloy shader found via Shader.Find.");
            return null;
        }

        static Shader FindBloodShader()
        {
            if (_bloodShaderSearched) return _bloodShader;
            _bloodShaderSearched = true;
            // Alloy first (correct PBR), then simpler fallbacks
            Shader alloy = FindAlloyShader();
            if (!ReferenceEquals(alloy, null)) { _bloodShader = alloy; return alloy; }

            // Standard is always in Unity — its _ALPHABLEND_ON variant is guaranteed compiled
            string[] fallbacks = { "Standard", "Transparent/Specular", "Unlit/Transparent",
                                   "Transparent/Diffuse",  "Sprites/Default" };
            foreach (string name in fallbacks)
            {
                Shader s = Shader.Find(name);
                if (!ReferenceEquals(s, null))
                {
                    _bloodShader = s;
                    Log.LogInfo("[BloodSystem] decalShaderFallback=" + name);
                    return s;
                }
            }
            Log.LogWarning("[BloodSystem] No decal shader found — dots will be invisible.");
            return null;
        }

        // Scan all scene renderers for a transparent Alloy material (glass, lenses, etc.)
        // and clone it. That material already has the correct transparent shader variant compiled.
        static void TryGrabDecalSourceMat()
        {
            if (_decalSourceSearched) return;
            _decalSourceSearched = true;

            // 1 — WFX_BulletHoleDecal (any shader, proven to work as a decal)
            try
            {
                Type bhType = AccessTools.TypeByName("WFX_BulletHoleDecal");
                if (!ReferenceEquals(bhType, null))
                {
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(bhType))
                    {
                        var comp = obj as Component;
                        if (ReferenceEquals(comp, null)) continue;
                        var r = comp.GetComponent<Renderer>();
                        if (ReferenceEquals(r, null) || ReferenceEquals(r.sharedMaterial, null)) continue;
                        _decalSourceMat = new Material(r.sharedMaterial);
                        _decalSourceMat.SetInt("_Cull", 0);
                        Log.LogInfo("[BloodSystem] WFXShader=" + _decalSourceMat.shader.name
                                    + " queue=" + _decalSourceMat.renderQueue);
                        return;
                    }
                }
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] TryGrabDecal WFX: " + ex.Message); }

            // 2 — Transparent/Specular: Unity built-in legacy shader, always in every Unity build.
            // Alpha from mainTex.a (soft circle) + Blinn-Phong specular (wet/shiny look).
            // Responds to scene lighting → dark in shadows, lit under flashlight. No glow.
            // Preferred over Alloy because both Alloy blend variants (_ALPHABLEND_ON and
            // _ALPHAPREMULTIPLY_ON) are NOT compiled in H3VR — confirmed squares in both cases.
            // Only Alloy _ALPHATEST_ON (cutout) works but gives hard edges.
            try
            {
                Shader ts = Shader.Find("Transparent/Specular");
                if (!ReferenceEquals(ts, null))
                {
                    _decalSourceMat = new Material(ts);
                    _decalSourceMat.renderQueue = 3000;
                    _decalSourceMat.SetInt("_Cull", 0);
                    Log.LogInfo("[BloodSystem] TransparentSpecularSrc");
                }
                else Log.LogWarning("[BloodSystem] Transparent/Specular not in build.");
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] TryGrabDecal TranspSpec: " + ex.Message); }

            // 3 — Alloy _ALPHATEST_ON fallback: hard circle edges but full Alloy PBR reflectiveness.
            // Only used if Transparent/Specular not found.
            if (ReferenceEquals(_decalSourceMat, null))
            {
                try
                {
                    foreach (var rend in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        foreach (var mat in rend.sharedMaterials)
                        {
                            if (ReferenceEquals(mat, null) || ReferenceEquals(mat.shader, null)) continue;
                            string sn = mat.shader.name;
                            if (!sn.StartsWith("Alloy")) continue;
                            if (sn.Contains("Particle") || sn.Contains("Add") || sn.Contains("Unlit")) continue;
                            if (!mat.HasProperty("_Roughness")) continue;

                            var alloyMat = new Material(mat.shader);
                            alloyMat.EnableKeyword("_ALPHATEST_ON");
                            alloyMat.DisableKeyword("_ALPHABLEND_ON");
                            alloyMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                            alloyMat.SetFloat("_Mode",   1f);
                            alloyMat.SetFloat("_Cutoff", 0.05f);
                            alloyMat.SetInt("_SrcBlend", 1);
                            alloyMat.SetInt("_DstBlend", 0);
                            alloyMat.SetFloat("_ZWrite", 1f);
                            alloyMat.renderQueue = 2450;
                            alloyMat.SetInt("_Cull", 0);
                            _decalSourceMat = alloyMat;
                            Log.LogInfo("[BloodSystem] AlloyCutoutFallback=" + sn);
                            goto doneAlloyScan;
                        }
                    }
                    doneAlloyScan:;
                }
                catch (Exception ex) { Log.LogWarning("[BloodSystem] TryGrabDecal Alloy: " + ex.Message); }
            }
        }

        // Returns a cached material using _decalTex (soft gaussian circle).
        // Priority: (1) cloned transparent scene material — Alloy or WFX, already working
        //           (2) runtime Alloy + keyword activation for transparency
        //           (3) Transparent/Specular / Unlit / Diffuse fallbacks
        internal static Material GetBloodMat(Color col)
        {
            Material m;
            if (_matCache.TryGetValue(col, out m) && !ReferenceEquals(m, null)) return m;

            // --- Path 1: clone an existing transparent material from scene ---
            if (ReferenceEquals(_decalSourceMat, null)) { _decalSourceSearched = false; TryGrabDecalSourceMat(); }
            if (!ReferenceEquals(_decalSourceMat, null))
            {
                m = new Material(_decalSourceMat);
                if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;
                ApplyBloodProps(m, col);
                _matCache[col] = m;
                return m;
            }

            // --- Path 2: build from shader ---
            Shader sh = FindBloodShader();
            if (ReferenceEquals(sh, null)) return null;

            m = new Material(sh);
            if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;

            if (sh.name.StartsWith("Alloy"))
            {
                m.EnableKeyword("_ALPHATEST_ON");
                m.DisableKeyword("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.SetFloat("_Mode",   1f);
                m.SetFloat("_Cutoff", 0.05f);
                m.SetInt("_SrcBlend", 1);
                m.SetInt("_DstBlend", 0);
                m.SetFloat("_ZWrite", 1f);
                m.renderQueue = 2450;
            }
            else
            {
                m.renderQueue = 3000;
            }

            ApplyBloodProps(m, col);
            m.SetInt("_Cull", 0);

            _matCache[col] = m;
            return m;
        }

        // Blood = wet dielectric fluid. NOT metallic. NOT emissive.
        // Alloy property names from Josh015/Alloy source: _Metal, _Roughness, _Specularity, _EmissionColor.
        // NOT _Metallic, NOT _GlowColor, NOT _Emission, NOT _SpecColor, NOT _Shininess (those are Standard).
        static void ApplyBloodProps(Material m, Color col)
        {
            if (m.HasProperty("_Color"))          m.SetColor("_Color",          col);

            // Alloy Core PBR properties (exact names from shader source)
            if (m.HasProperty("_Metal"))          m.SetFloat("_Metal",          0f);
            if (m.HasProperty("_Specularity"))    m.SetFloat("_Specularity",    0.2f);   // flashlight won't overblow
            if (m.HasProperty("_Roughness"))      m.SetFloat("_Roughness",      0.15f);  // wet but not chrome
            // Kill emission: Alloy uses _EmissionColor (half3) + _EMISSION keyword
            if (m.HasProperty("_EmissionColor"))  m.SetColor("_EmissionColor",  Color.black);
            m.DisableKeyword("_EMISSION");

            // Unity Standard shader properties (different names from Alloy)
            if (m.HasProperty("_Metallic"))       m.SetFloat("_Metallic",       0f);    // non-metallic
            if (m.HasProperty("_Smoothness"))     m.SetFloat("_Smoothness",     0.9f);  // very smooth = reflective
            // Legacy fallback shaders (Transparent/Specular etc.) use these
            if (m.HasProperty("_SpecColor"))      m.SetColor("_SpecColor",      new Color(0.3f, 0.3f, 0.3f));
            if (m.HasProperty("_Shininess"))      m.SetFloat("_Shininess",      0.7f);
        }

        // ── Spray materials ───────────────────────────────────────────────────────

        static Material BuildFogMaterial()
        {
            Shader sh = Shader.Find("Particles/Standard Unlit");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Additive");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Sprites/Default");
            if (ReferenceEquals(sh, null)) return null;
            var mat = new Material(sh);
            // Fog uses the blood splatter PNG so puffs have the splatter shape
            if (!ReferenceEquals(_firstBloodTex, null)) mat.mainTexture = _firstBloodTex;
            return mat;
        }

        static Material BuildPelletMaterial()
        {
            Shader sh = Shader.Find("Particles/Standard Unlit");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Additive");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Sprites/Default");
            if (ReferenceEquals(sh, null)) return null;
            var mat = new Material(sh);
            // Pellets use soft circle so individual drops are round
            if (!ReferenceEquals(_decalTex, null)) mat.mainTexture = _decalTex;
            return mat;
        }

        void BuildSprayPSes()
        {
            // Pellet PS: tight fast round drops
            {
                var go = new GameObject("BSPellet");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _pelletPS = go.AddComponent<ParticleSystem>();
                var mn = _pelletPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(4f, 20f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.003f, 0.012f);
                mn.maxParticles    = 3000;
                mn.gravityModifier = 1.8f;
                mn.loop            = false;
                mn.playOnAwake     = false;
                mn.duration        = 0.5f;
                mn.startColor      = new ParticleSystem.MinMaxGradient(_mustardFallback);
                var em = _pelletPS.emission;
                em.enabled      = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                var sh = _pelletPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle     = 28f;
                sh.radius    = 0.02f;
                var psr = _pelletPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_pelletMat, null)) psr.material = _pelletMat;
                go.SetActive(true);
            }

            // Fog PS: large slow dense puffs
            {
                var go = new GameObject("BSFog");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _fogPS = go.AddComponent<ParticleSystem>();
                var mn = _fogPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(1f, 5f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.10f, 0.35f); // larger than before for density
                mn.startRotation   = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
                mn.maxParticles    = 1000;
                mn.gravityModifier = new ParticleSystem.MinMaxCurve(0.5f, 1.0f);
                mn.loop            = false;
                mn.playOnAwake     = false;
                mn.duration        = 0.5f;
                mn.startColor      = new ParticleSystem.MinMaxGradient(_mustardFallback);
                var em = _fogPS.emission;
                em.enabled      = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                var sh = _fogPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle     = 18f;
                sh.radius    = 0.03f;
                var col = _fogPS.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] { new GradientColorKey(Color.white, 0f),
                                            new GradientColorKey(Color.white, 1f) },
                    new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f),
                                            new GradientAlphaKey(0f, 1f) });
                col.color = new ParticleSystem.MinMaxGradient(grad);
                var rot = _fogPS.rotationOverLifetime;
                rot.enabled = true;
                rot.z       = new ParticleSystem.MinMaxCurve(-1f, 1f);
                var psr = _fogPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_fogMat, null)) psr.material = _fogMat;
                go.SetActive(true);
            }
        }

        // ── Spawn: splash projection ──────────────────────────────────────────────

        internal static void SpawnProjection(Vector3 exitPt, Vector3 projDir,
                                              Sosig srcSosig, float bulletSpeed)
        {
            if (!CfgEnabled.Value) return;
            try
            {
                Color   col       = GetSosigBloodColor(srcSosig);
                Vector3 fwd       = projDir.normalized;
                float   tanHalf   = Mathf.Tan(CfgConeAngle.Value * Mathf.Deg2Rad);
                float   range     = CfgRange.Value;
                float   projSpeed = Mathf.Max(1f, bulletSpeed * 2f);

                Vector3 worldUp = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.99f
                                ? Vector3.forward : Vector3.up;
                Vector3 right = Vector3.Cross(worldUp, fwd).normalized;
                Vector3 up    = Vector3.Cross(fwd, right);

                // Random roll per shot so each splatter looks different
                float randRad = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float cosR = Mathf.Cos(randRad), sinR = Mathf.Sin(randRad);
                Vector3 r2 = right * cosR + up * sinR;
                Vector3 u2 = up    * cosR - right * sinR;
                right = r2; up = u2;

                int N = Mathf.Max(1, CfgRayCount.Value);

                const float BIN_S = 0.025f;
                var staticBins = new Dictionary<int, List<DotData>>();
                var dynBins    = new Dictionary<int, Dictionary<Transform, List<DotData>>>();

                for (int i = 0; i < N; i++)
                {
                    Vector2 uv; float dark;
                    SampleSplatter(out uv, out dark);

                    Vector3 dir = (fwd + right * uv.x * tanHalf + up * uv.y * tanHalf).normalized;

                    RaycastHit h;
                    if (!Physics.Raycast(exitPt + dir * 0.15f, dir, out h, range)) continue;
                    if (IsSourceSosig(h.collider, srcSosig)) continue;
                    if (h.collider.GetComponentInParent<SosigWeapon>() != null) continue;

                    // Size: base at close range, scales to 3x at CfgRange (half-range = 2x)
                    float dotR = CfgDotSize.Value * Mathf.Clamp(1f + h.distance / 10f, 1f, 3f);
                    int   bin  = Mathf.FloorToInt(h.distance / projSpeed / BIN_S);

                    Rigidbody hitRb = h.collider.attachedRigidbody;
                    Transform par   = hitRb != null ? hitRb.transform : null;
                    var dd = new DotData(h.point, h.normal, dotR, dark);

                    if (par == null)
                    {
                        if (!staticBins.ContainsKey(bin)) staticBins[bin] = new List<DotData>();
                        staticBins[bin].Add(dd);
                    }
                    else
                    {
                        if (!dynBins.ContainsKey(bin))      dynBins[bin] = new Dictionary<Transform, List<DotData>>();
                        if (!dynBins[bin].ContainsKey(par)) dynBins[bin][par] = new List<DotData>();
                        dynBins[bin][par].Add(dd);
                    }
                }

                if (staticBins.Count == 0 && dynBins.Count == 0) return;
                _instance.StartCoroutine(DoDelayedSpawn(staticBins, dynBins, col, BIN_S));
            }
            catch (Exception ex) { Log.LogError("[BloodSystem] SpawnProjection: " + ex); }
        }

        static IEnumerator DoDelayedSpawn(
            Dictionary<int, List<DotData>> staticBins,
            Dictionary<int, Dictionary<Transform, List<DotData>>> dynBins,
            Color col, float binSize)
        {
            var allKeys = new List<int>(staticBins.Keys);
            foreach (int k in dynBins.Keys) if (!allKeys.Contains(k)) allKeys.Add(k);
            allKeys.Sort();

            float elapsed = 0f;
            foreach (int b in allKeys)
            {
                float t    = b * binSize;
                float wait = t - elapsed;
                if (wait > 0.001f) { yield return new WaitForSeconds(wait); elapsed = t; }

                List<DotData> slist;
                if (staticBins.TryGetValue(b, out slist) && slist.Count > 0)
                    BuildDotMesh(slist, null, col);

                Dictionary<Transform, List<DotData>> dmap;
                if (dynBins.TryGetValue(b, out dmap))
                    foreach (var kv in dmap)
                        if (!ReferenceEquals(kv.Key, null) && kv.Key != null)
                            BuildDotMesh(kv.Value, kv.Key, col);
            }
        }

        // ── Spawn: spray ──────────────────────────────────────────────────────────

        internal static void SpawnBloodSpray(Vector3 pos, Vector3 fwd, Color col)
        {
            if (!CfgEnabled.Value) return;
            Quaternion rot = Quaternion.LookRotation(fwd);

            if (!ReferenceEquals(_pelletPS, null))
            {
                _pelletPS.transform.position = pos;
                _pelletPS.transform.rotation = rot;
                var mn = _pelletPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                _pelletPS.Emit(400);
            }
            if (!ReferenceEquals(_fogPS, null))
            {
                _fogPS.transform.position = pos;
                _fogPS.transform.rotation = rot;
                var mn = _fogPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(new Color(col.r, col.g, col.b, 0.75f));
                _fogPS.Emit(80); // increased from 30 for denser fog cloud
            }
        }

        // ── Spawn: drip stain quad (static surfaces only) ─────────────────────────

        internal static void SpawnDripStain(Vector3 pos, Vector3 normal, Color col, float scale = 1f)
        {
            Material mat = GetBloodMat(col);
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.015f, 0.04f) * scale;
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
            mesh.triangles = new[] { 0, 3, 2, 0, 2, 1 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            var go = new GameObject("DS");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
        }

        // ── Mesh building ─────────────────────────────────────────────────────────

        static void BuildDotMesh(List<DotData> dots, Transform parent, Color col)
        {
            if (dots.Count == 0) return;
            Material mat = GetBloodMat(col);
            if (ReferenceEquals(mat, null)) return;

            const int MAX = 16383; // 4 verts × 16383 = 65532 < 65535 index limit
            int total = dots.Count;
            for (int start = 0; start < total; start += MAX)
            {
                int count = Mathf.Min(MAX, total - start);
                var verts = new Vector3[count * 4];
                var uvs   = new Vector2[count * 4];
                var cols  = new Color  [count * 4];
                var tris  = new int    [count * 6];

                for (int i = 0; i < count; i++)
                {
                    DotData d    = dots[start + i];
                    Vector3 norm = d.Norm;
                    float   r    = d.R;
                    Vector3 qup  = Mathf.Abs(Vector3.Dot(norm, Vector3.up)) > 0.9f
                                 ? Vector3.forward : Vector3.up;
                    Quaternion rot = Quaternion.LookRotation(-norm, qup)
                                   * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                    Vector3 qr = rot * Vector3.right * r;
                    Vector3 qu = rot * Vector3.up    * r;
                    Vector3 bp = d.Pos + norm * 0.003f;

                    Vector3 c0 = bp-qr-qu, c1 = bp+qr-qu, c2 = bp+qr+qu, c3 = bp-qr+qu;
                    if (parent != null)
                    {
                        c0 = parent.InverseTransformPoint(c0);
                        c1 = parent.InverseTransformPoint(c1);
                        c2 = parent.InverseTransformPoint(c2);
                        c3 = parent.InverseTransformPoint(c3);
                    }

                    // Per-dot darkness: dark pixels in source image → darker dot color
                    // darkMult: range 0.4 (light pixels) to 1.0 (dark/dense pixels)
                    float darkMult = Mathf.Lerp(0.4f, 1.0f, d.Dark);
                    Color vc = new Color(col.r * darkMult, col.g * darkMult, col.b * darkMult, 1f);

                    int v = i * 4;
                    verts[v]=c0; verts[v+1]=c1; verts[v+2]=c2; verts[v+3]=c3;
                    uvs[v]  =new Vector2(0,0); uvs[v+1]=new Vector2(1,0);
                    uvs[v+2]=new Vector2(1,1); uvs[v+3]=new Vector2(0,1);
                    cols[v]=cols[v+1]=cols[v+2]=cols[v+3]=vc;
                    int t = i * 6;
                    tris[t]=v; tris[t+1]=v+3; tris[t+2]=v+2;
                    tris[t+3]=v; tris[t+4]=v+2; tris[t+5]=v+1;
                }

                var mesh = new Mesh();
                mesh.vertices  = verts;
                mesh.uv        = uvs;
                mesh.colors    = cols;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                mesh.RecalculateNormals();

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

        // ── Blood color resolution ────────────────────────────────────────────────

        internal static Color GetSosigBloodColor(Sosig s)
        {
            if (!_ngaChecked)
            {
                _ngaChecked = true;
                try
                {
                    BepInEx.PluginInfo nga;
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("NGA.SosigIntegrityConfigs", out nga)
                        && !ReferenceEquals(nga.Instance, null))
                    {
                        var plugin = nga.Instance as BaseUnityPlugin;
                        if (!ReferenceEquals(plugin, null))
                        {
                            ConfigEntry<bool> kEntry;
                            if (plugin.Config.TryGetEntry("Sosig Body.Colour", "Ketchup", out kEntry)
                                && kEntry.Value)
                            {
                                _ngaKetchup = true;
                                ConfigEntry<string> cEntry;
                                if (plugin.Config.TryGetEntry("Sosig Body.Colour", "Mustard Colour", out cEntry))
                                {
                                    Color parsed;
                                    _ngaColor = ColorUtility.TryParseHtmlString(cEntry.Value, out parsed)
                                              ? parsed : _mustardFallback;
                                }
                                else { _ngaColor = _mustardFallback; }
                            }
                            Log.LogInfo("[BloodSystem] NGA ketchup=" + _ngaKetchup + " col=" + _ngaColor);
                        }
                    }
                    else { Log.LogInfo("[BloodSystem] NGA SosigIntegrityConfigs not present."); }
                }
                catch (Exception ex) { Log.LogWarning("[BloodSystem] NGA check: " + ex.Message); }
            }
            if (_ngaKetchup) return _ngaColor;

            if (!_mustardFieldSearched)
            {
                _mustardFieldSearched = true;
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fiMustard = typeof(Sosig).GetField("Mustard", bf);
                if (ReferenceEquals(_fiMustard, null))
                    foreach (FieldInfo fi in typeof(Sosig).GetFields(bf))
                        if (fi.FieldType.Name == "Color" && fi.Name.ToLower().Contains("mustard"))
                        { _fiMustard = fi; break; }
                Log.LogInfo("[BloodSystem] Mustard field=" +
                    (!ReferenceEquals(_fiMustard, null) ? _fiMustard.Name : "not found"));
            }

            if (!ReferenceEquals(s, null) && !ReferenceEquals(_fiMustard, null))
                try { return (Color)_fiMustard.GetValue(s); } catch { }

            return _mustardFallback;
        }

        // ── Gib tagging (deferred one frame so gibs have time to scatter) ───────────

        internal static IEnumerator TagGibsDeferred(Vector3 pos, Sosig src)
        {
            yield return null;
            foreach (var nc in Physics.OverlapSphere(pos, 8f))
            {
                if (nc.GetComponentInParent<SosigLink>() != null) continue;
                Rigidbody nrb = nc.attachedRigidbody;
                if (nrb == null) continue;
                var tag = nrb.GetComponent<SosigGibTag>();
                if (tag == null) tag = nrb.gameObject.AddComponent<SosigGibTag>();
                tag.SourceSosig = src;
            }
        }

        // ── Source-sosig filter ───────────────────────────────────────────────────

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
    }

    // ── Helper MonoBehaviours ─────────────────────────────────────────────────────

    public class SosigGibTag : MonoBehaviour
    {
        public Sosig SourceSosig;
    }

    // Per-bullet inter-frame state: Pre patch writes, Post patch reads.
    public class SplatterTracker : MonoBehaviour
    {
        public Collider   PrevCollider;
        public SosigLink  PrevHitLink;       // SosigLink stored in PostMove WHILE ALIVE; survive Destroy() until next frame
        public Sosig      LastSosig;         // S from PrevHitLink, copied while link was alive
        public Vector3    LastSosigLinkPos;
        public Vector3    LastBulletDir;
        public float      LastBulletSpeed = 400f;
        public Vector3    LastHitPoint;
    }

    // Attached to every ParticleSystem found in a sosig's hierarchy.
    // Polls near-expired particles → stains static environment surfaces.
    public class VanillaDripStainer : MonoBehaviour
    {
        ParticleSystem            _ps;
        ParticleSystem.Particle[] _buf;
        Sosig                     _sosig;
        int                       _skip;

        void Start()
        {
            _ps    = GetComponent<ParticleSystem>();
            _sosig = GetComponentInParent<Sosig>();
            if (_ps != null)
                _buf = new ParticleSystem.Particle[Mathf.Max(_ps.main.maxParticles, 64)];
        }

        void Update()
        {
            if (_ps == null || _buf == null) return;
            if (++_skip < 3) return; _skip = 0;

            int n = _ps.GetParticles(_buf);
            if (n == 0) return;

            bool local = _ps.main.simulationSpace == ParticleSystemSimulationSpace.Local;
            bool dirty = false;

            for (int i = 0; i < n; i++)
            {
                if (_buf[i].remainingLifetime > 0.2f) continue;

                Vector3 pos = local
                    ? _ps.transform.TransformPoint(_buf[i].position)
                    : (Vector3)_buf[i].position;

                RaycastHit h;
                if (Physics.Raycast(pos + Vector3.up * 0.08f, Vector3.down, out h, 0.4f))
                {
                    if (h.collider.attachedRigidbody == null
                        && h.collider.GetComponentInParent<SosigLink>() == null)
                    {
                        Color col = BloodSystemPlugin.GetSosigBloodColor(_sosig);
                        BloodSystemPlugin.SpawnDripStain(h.point, h.normal, col);
                    }
                    _buf[i].remainingLifetime = 0f;
                    dirty = true;
                }
            }
            if (dirty) _ps.SetParticles(_buf, n);
        }
    }

    // Added to each Sosig to catch drip ParticleSystems spawned after Sosig.Start.
    // Vanilla blood drip PSes are often Instantiated when the sosig first takes a wound.
    public class SosigDripWatcher : MonoBehaviour
    {
        int _tick;

        void Update()
        {
            if (++_tick < 150) return; // scan every ~3s at 50fps
            _tick = 0;
            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
                if (ps.GetComponent<VanillaDripStainer>() == null)
                    ps.gameObject.AddComponent<VanillaDripStainer>();
        }
    }

    // ── Harmony patches ───────────────────────────────────────────────────────────
    // RULE: zero IEnumerator methods in this class — causes TypeLoadException on PatchAll.

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

        // ── Sosig.Start: attach drip polling components ───────────────────────────

        [HarmonyPatch(typeof(Sosig), "Start")]
        [HarmonyPostfix]
        static void OnSosigStart(Sosig __instance)
        {
            try
            {
                // Attach to PSes already present at spawn
                foreach (var ps in __instance.GetComponentsInChildren<ParticleSystem>(true))
                    if (ps.GetComponent<VanillaDripStainer>() == null)
                        ps.gameObject.AddComponent<VanillaDripStainer>();

                // Periodic watcher: catches drip PSes that get spawned later (on first wound)
                if (__instance.GetComponent<SosigDripWatcher>() == null)
                    __instance.gameObject.AddComponent<SosigDripWatcher>();
            }
            catch (Exception ex)
            {
                BloodSystemPlugin.Log.LogWarning("[BloodSystem] SosigStart: " + ex.Message);
            }
        }

        // ── Bullet pre-move: snapshot state BEFORE this step's movement/collision ─

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPrefix]
        static void PreMove(BallisticProjectile __instance)
        {
            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker == null) tracker = __instance.gameObject.AddComponent<SplatterTracker>();

            // Snapshot state before this frame's movement overwrites it
            tracker.PrevCollider = FLastColliderHit.GetValue(__instance) as Collider;
            var lastHit          = (RaycastHit)FHit.GetValue(__instance);
            tracker.LastHitPoint = lastHit.point;

            var vel = (Vector3)FVelocity.GetValue(__instance);
            if (vel.magnitude > 0.01f)
            {
                tracker.LastBulletDir   = vel.normalized;
                tracker.LastBulletSpeed = vel.magnitude;
            }
        }

        // ── Bullet post-move: detect exit wound and trigger effects ───────────────
        //
        // PrevHitLink approach: when bullet hits a SosigLink, we store the reference in PostMove
        // while the link is GUARANTEED ALIVE (Destroy() is deferred to end-of-frame in Unity).
        // Next frame, if that link is Unity-destroyed (killing blow) OR bullet moved to different
        // collider (pass-through), we fire blood. This correctly handles lethal shots where
        // the link gets Destroy()'d before the next PreMove runs.

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPostfix]
        static void PostMove(BallisticProjectile __instance)
        {
            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker == null) return;

            var currentCollider = FLastColliderHit.GetValue(__instance) as Collider;

            // -- Check if we were tracking a SosigLink from last frame --
            SosigLink prevLink = tracker.PrevHitLink;
            if (!ReferenceEquals(prevLink, null))
            {
                bool linkDestroyed = prevLink == null;  // Unity lifecycle null — segment exploded
                bool exitedLink    = !ReferenceEquals(currentCollider, tracker.PrevCollider);

                if (linkDestroyed || exitedLink)
                {
                    Vector3 dir = tracker.LastBulletDir.magnitude > 0.01f
                                ? tracker.LastBulletDir : __instance.transform.forward;
                    Sosig   src = tracker.LastSosig;
                    Color   col = BloodSystemPlugin.GetSosigBloodColor(src);
                    float   spd = tracker.LastBulletSpeed > 1f ? tracker.LastBulletSpeed : 400f;

                    Vector3 segCenter = tracker.LastSosigLinkPos;
                    Vector3 exitPt    = tracker.LastHitPoint + dir * 0.35f;
                    if (!linkDestroyed)
                    {
                        // Link still alive — raycast to find the exact exit surface point
                        RaycastHit xh;
                        if (Physics.Raycast(segCenter, dir, out xh, 2f)
                            && xh.collider.GetComponentInParent<SosigLink>() != null)
                            exitPt = xh.point + dir * 0.02f;
                    }

                    BloodSystemPlugin.SpawnProjection(exitPt, dir, src, spd);
                    BloodSystemPlugin.SpawnBloodSpray(exitPt, dir, col);

                    // Segment exploded — tag nearby gibs next frame (after they've scattered)
                    if (linkDestroyed && !ReferenceEquals(src, null))
                        BloodSystemPlugin._instance.StartCoroutine(
                            BloodSystemPlugin.TagGibsDeferred(segCenter, src));

                    tracker.PrevHitLink = null; // consumed
                }
            }

            // -- Update PrevHitLink and related state for next frame --
            tracker.PrevCollider = currentCollider;

            bool curCsAlive = !ReferenceEquals(currentCollider, null);
            bool curUAlive  = curCsAlive && currentCollider != null;
            if (curUAlive)
            {
                SosigLink curLink = currentCollider.GetComponentInParent<SosigLink>();
                if (!ReferenceEquals(curLink, null) && curLink != null)
                {
                    // Store while confirmed alive — this C# ref survives Destroy() until next PostMove
                    tracker.PrevHitLink      = curLink;
                    tracker.LastSosig        = curLink.S;
                    tracker.LastSosigLinkPos = curLink.transform.position;
                }
            }
        }
    }
}
