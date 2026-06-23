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
        public float   Dark;       // 0=bright/white area, 1=dark area
        public Vector3 TanNorm;    // tangent-space normal from normal map at this sample UV
        public Vector3 ElongDir;   // bullet direction projected onto hit surface (world space)
        public float   Elongation; // stretch factor: 1=round, >1=elongated along ElongDir
        public DotData(Vector3 pos, Vector3 norm, float r, float dark,
                       Vector3 tanNorm, Vector3 elongDir, float elongation)
        {
            Pos = pos; Norm = norm; R = r; Dark = dark;
            TanNorm = tanNorm; ElongDir = elongDir; Elongation = elongation;
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
        internal static readonly Dictionary<Color, Material> _matCache = new Dictionary<Color, Material>();
        static Shader    _bloodShader;
        static bool      _bloodShaderSearched;
        internal static Material  _decalSourceMat;
        internal static bool      _decalSourceSearched;
        internal static bool      _dbgDotLogged;
        internal static bool      _dbgDecalLogged;

        // _decalTex  = procedural gaussian soft circle (WHITE) — used with WFX clone (_Color tints it)
        // Blood PNGs are used ONLY for CDF ray-direction sampling and spray particle texture
        static Texture2D   _decalTex;
        static Texture2D   _firstBloodTex; // first valid PNG loaded — used as spray particle texture
        static Texture2D   _normalMapTex;  // blood normal map (PNG with "normal"/"norm" in filename)
        // Per-color pre-baked soft circles — fallback when shader has no _Color tint property
        static readonly Dictionary<Color, Texture2D> _coloredTexCache = new Dictionary<Color, Texture2D>();


        // CDF data built from ALL blood PNGs combined (equal-contribution, aspect-correct)
        static Vector2[] _splatterUVs;
        static float[]   _cumWeights;
        static float[]   _splatterDarks;   // per-sample darkness from source pixel luminance
        static Vector3[] _splatterNormals; // per-sample tangent-space normal from normal map

        // Fixed light direction in texture tangent space for normal-map shading
        static readonly Vector3 _tanLight = new Vector3(0.5f, 0.5f, 0.707f).normalized;

        // Spray: two persistent PSes (Unlit — scene lighting must not tint brief spray)
        static ParticleSystem _pelletPS;
        static ParticleSystem _fogPS;
        static Material       _fogMat;
        static Material       _pelletMat;
        // Dot mesh base material: Particles/Standard Lit + soft circle (scene-lit for persistent stains)
        static Material       _dotBaseMat;

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
            CfgRayCount  = Config.Bind("Blood", "Rays per shot",     2000,   "Splash ray count. Around 1500 was confirmed fine in testing; 2000 is the default.");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",   10f,    "Half-angle in degrees of the splash cone.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",   0.008f, "Base radius of each splash dot in metres. Scales linearly to 3x at 20 metres.");
            CfgRange     = Config.Bind("Blood", "Range metres",      50f,    "Maximum splash distance in metres.");

            // Soft-circle decal texture — all dot/stain rendering uses this (NOT the blood PNGs)
            _decalTex = MakeSoftCircle(96);

            // Load Alloy/Core material from AssetBundle shipped with the mod.
            // If bundle missing (dev env), falls back to scene scan on first sosig spawn.
            TryLoadFromBundle();

            // Dot base material: Sprites/Default — always compiled in any Unity build,
            // alpha-blends correctly, and reads mesh.colors (vertex colors) so per-dot
            // normal-map shading (darkMult, shadeMult) is actually visible.
            {
                Shader ds = Shader.Find("Sprites/Default");
                if (!ReferenceEquals(ds, null))
                {
                    _dotBaseMat = new Material(ds);
                    _dotBaseMat.mainTexture = _decalTex;
                    // Sprites/Default has Cull Off and premul-alpha blend hardcoded — no extra setup needed.
                }
            }

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

            _fogMat    = BuildSprayMaterial();
            _pelletMat = BuildSprayMaterial();
            BuildSprayPSes();

            new Harmony("h3vr.invent60.bloodsystem").PatchAll(typeof(BloodSystemPatches));
            Log.LogInfo("[BloodSystem] 3.0.0 loaded. FieldsOK=" + BloodSystemPatches.Ok);
        }

        // ── PNG loading ───────────────────────────────────────────────────────────

        static List<Texture2D> LoadAllPngs()
        {
            var result = new List<Texture2D>();
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var files = new List<string>(Directory.GetFiles(dir, "*.png"));
                files.AddRange(Directory.GetFiles(dir, "*.jpg"));
                files.AddRange(Directory.GetFiles(dir, "*.jpeg"));
                foreach (string f in files)
                {
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!t.LoadImage(File.ReadAllBytes(f))) continue;
                    t.filterMode = FilterMode.Trilinear;
                    string fname = Path.GetFileNameWithoutExtension(f).ToLower();
                    if (fname.Contains("normal") || fname.Contains("norm"))
                    {
                        _normalMapTex = t;
                        Log.LogInfo("[BloodSystem] NormalMap=" + Path.GetFileName(f));
                    }
                    else
                    {
                        result.Add(t);
                    }
                }
            }
            catch (Exception ex) { BloodSystemPlugin.Log.LogWarning("[BloodSystem] LoadAllPngs: " + ex.Message); }
            return result;
        }

        // Decode tangent-space normal from _normalMapTex at a [-1,1] UV. Returns (0,0,1) if no map.
        static Vector3 LookupNormal(Vector2 uv)
        {
            if (ReferenceEquals(_normalMapTex, null)) return new Vector3(0f, 0f, 1f);
            float u  = uv.x * 0.5f + 0.5f;
            float v  = uv.y * 0.5f + 0.5f;
            int   px = Mathf.Clamp(Mathf.RoundToInt(u * (_normalMapTex.width  - 1)), 0, _normalMapTex.width  - 1);
            int   py = Mathf.Clamp(Mathf.RoundToInt(v * (_normalMapTex.height - 1)), 0, _normalMapTex.height - 1);
            Color c  = _normalMapTex.GetPixel(px, py);
            return new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, Mathf.Abs(c.b * 2f - 1f)).normalized;
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

        // Pre-bake a blood-colored soft circle per color — used for shaders without _Color tint.
        static Texture2D GetColoredTex(Color col)
        {
            Texture2D t;
            if (_coloredTexCache.TryGetValue(col, out t) && !ReferenceEquals(t, null)) return t;
            int sz = 96;
            t = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
            float c = (sz - 1) * 0.5f;
            var pix = new Color[sz * sz];
            for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float d2 = dx*dx + dy*dy;
                float a = d2 > 1f ? 0f : Mathf.Clamp01(Mathf.Exp(-d2 * 3.5f));
                pix[y*sz+x] = new Color(col.r, col.g, col.b, a);
            }
            t.SetPixels(pix); t.Apply();
            t.filterMode = FilterMode.Trilinear;
            _coloredTexCache[col] = t;
            return t;
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
            var normList = new List<Vector3>();
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
                    normList.Add(LookupNormal(imgUVs[i]));
                    wList.Add(cumul);
                }
            }

            _splatterUVs     = uvList.ToArray();
            _splatterDarks   = darkList.ToArray();
            _splatterNormals = normList.ToArray();
            _cumWeights      = wList.ToArray();
        }

        static void BuildFallbackGrid(int side)
        {
            var uvList   = new List<Vector2>(side * side);
            var darkList = new List<float>(side * side);
            var normList = new List<Vector3>(side * side);
            var flatNorm = new Vector3(0f, 0f, 1f);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var uv = new Vector2(((float)x / (side - 1)) * 2f - 1f,
                                     ((float)y / (side - 1)) * 2f - 1f);
                uvList.Add(uv);
                darkList.Add(0.8f);
                normList.Add(LookupNormal(uv));
            }
            _splatterUVs     = uvList.ToArray();
            _splatterDarks   = darkList.ToArray();
            _splatterNormals = normList.ToArray();
            _cumWeights      = new float[0];
        }

        // O(log n) binary search on CDF → returns UV + darkness + tangent normal at that sample
        static void SampleSplatter(out Vector2 uv, out float dark, out Vector3 tanNorm)
        {
            if (_splatterUVs == null || _splatterUVs.Length == 0)
            {
                uv      = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f));
                dark    = 0.8f;
                tanNorm = new Vector3(0f, 0f, 1f);
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
            uv      = _splatterUVs[idx];
            dark    = (!ReferenceEquals(_splatterDarks,   null) && idx < _splatterDarks.Length)
                    ? _splatterDarks[idx]   : 0.8f;
            tanNorm = (!ReferenceEquals(_splatterNormals, null) && idx < _splatterNormals.Length)
                    ? _splatterNormals[idx] : new Vector3(0f, 0f, 1f);
        }

        // ── Alloy material cache (persists across sessions) ───────────────────────

        static string AlloyMatCachePath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                         "alloy_mat.cache");

        // Reconstruct Alloy/Core from saved keywords + blend state.
        // The glass-object transparent variant is always compiled in H3VR — Shader.Find picks it up.
        static void TryLoadFromBundle()  // name kept so Awake call doesn't change
        {
            try
            {
                string path = AlloyMatCachePath;
                if (!File.Exists(path)) return;
                var dict = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    dict[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                string sn;
                if (!dict.TryGetValue("shaderName", out sn)) return;
                Shader sh = Shader.Find(sn);
                if (ReferenceEquals(sh, null))
                { Log.LogWarning("[BloodSystem] Cache shader not found: " + sn); return; }

                var mat = new Material(sh);
                string v;
                if (dict.TryGetValue("renderQueue", out v)) mat.renderQueue = int.Parse(v);
                if (dict.TryGetValue("_SrcBlend", out v) && mat.HasProperty("_SrcBlend"))
                    mat.SetInt("_SrcBlend", int.Parse(v));
                if (dict.TryGetValue("_DstBlend", out v) && mat.HasProperty("_DstBlend"))
                    mat.SetInt("_DstBlend", int.Parse(v));
                if (dict.TryGetValue("_ZWrite",   out v) && mat.HasProperty("_ZWrite"))
                    mat.SetInt("_ZWrite", int.Parse(v));
                if (dict.TryGetValue("_Mode",     out v) && mat.HasProperty("_Mode"))
                    mat.SetFloat("_Mode", float.Parse(v));
                if (dict.TryGetValue("keywords",  out v) && !string.IsNullOrEmpty(v))
                    foreach (string kw in v.Split(','))
                        if (!string.IsNullOrEmpty(kw.Trim())) mat.EnableKeyword(kw.Trim());
                mat.SetInt("_Cull", 0);

                _decalSourceMat      = mat;
                _decalSourceSearched = true;
                Log.LogInfo("[BloodSystem] Cache loaded: " + sn + " rq=" + mat.renderQueue);
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] TryLoadFromCache: " + ex.Message); }
        }

        internal static void SaveAlloyCacheToFile(Material mat)
        {
            try
            {
                var lines = new List<string>
                {
                    "shaderName=" + mat.shader.name,
                    "renderQueue=" + mat.renderQueue,
                    "keywords="    + string.Join(",", mat.shaderKeywords),
                };
                foreach (string p in new[] { "_SrcBlend", "_DstBlend", "_ZWrite" })
                    if (mat.HasProperty(p)) lines.Add(p + "=" + mat.GetInt(p));
                foreach (string p in new[] { "_Mode", "_Cutoff" })
                    if (mat.HasProperty(p)) lines.Add(p + "=" + mat.GetFloat(p).ToString("F3"));
                File.WriteAllLines(AlloyMatCachePath, lines.ToArray());
                Log.LogInfo("[BloodSystem] Cache saved: " + mat.shader.name
                    + " kws=" + string.Join(",", mat.shaderKeywords));
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] SaveAlloyCache: " + ex.Message); }
        }

        // ── Shader / material for dot/stain meshes ────────────────────────────────

        internal static bool _alloyGrabPending;

        // Scan ALL renderers in scene for best Alloy transparent material.
        // Priority: Alloy/Core rq>2000 > other non-additive Alloy rq>2000 > Alloy/Core opaque.
        // Additive shaders excluded — additive blend on static mesh = glow, not blood.
        internal static IEnumerator TryGrabAlloyFromScene()
        {
            _alloyGrabPending = true;
            yield return null; // wait 1 frame for WFX decal to instantiate

            if (!ReferenceEquals(_decalSourceMat, null)) { _alloyGrabPending = false; yield break; }

            Renderer bestR = null; int bestScore = -1; int alloyCount = 0;
            try
            {
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (ReferenceEquals(r, null)) continue;
                    var mat = r.sharedMaterial;
                    if (ReferenceEquals(mat, null) || ReferenceEquals(mat.shader, null)) continue;
                    string sn = mat.shader.name;
                    if (sn.IndexOf("Alloy", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    // Exclude additive shaders: additive blend on mesh = glow, not blood.
                    if (sn.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    alloyCount++;

                    // Score: prefer Alloy/Core > other Alloy, prefer transparent (rq>2000) > opaque.
                    int score = 0;
                    if (sn == "Alloy/Core") score += 100;
                    if (mat.renderQueue > 2000) score += 50;
                    Log.LogInfo("[BloodSystem] Alloy candidate: shader=" + sn
                        + " rq=" + mat.renderQueue + " score=" + score + " obj=" + r.gameObject.name);
                    if (score > bestScore) { bestScore = score; bestR = r; }
                }
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] TryGrabAlloyFromScene: " + ex.Message); }

            Log.LogInfo("[BloodSystem] AlloyGrab: candidates=" + alloyCount + " bestScore=" + bestScore);

            if (!ReferenceEquals(bestR, null))
            {
                _decalSourceMat = new Material(bestR.sharedMaterial);
                _decalSourceMat.SetInt("_Cull", 0);
                _decalSourceSearched = true;
                _matCache.Clear();
                Log.LogInfo("[BloodSystem] Alloy mat GRABBED: " + _decalSourceMat.shader.name
                    + " rq=" + _decalSourceMat.renderQueue);
                SaveAlloyCacheToFile(_decalSourceMat);
            }
            else
            {
                Log.LogWarning("[BloodSystem] No Alloy renderer found — shoot a wall, then a sosig.");
            }
            _alloyGrabPending = false;
        }

        // Returns cached Alloy material. Returns null (no dots) until Alloy is grabbed via wall hit.
        internal static Material GetBloodMat(Color col)
        {
            Material m;
            if (_matCache.TryGetValue(col, out m) && !ReferenceEquals(m, null)) return m;

            if (ReferenceEquals(_decalSourceMat, null))
                return null; // invisible until Alloy grabbed

            m = new Material(_decalSourceMat);
            if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;
            ApplyBloodProps(m, col);
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
            if (m.HasProperty("_Specularity"))    m.SetFloat("_Specularity",    0.15f);  // flashlight won't overblow
            if (m.HasProperty("_Roughness"))      m.SetFloat("_Roughness",      0.4f);   // wet but not mirror
            // Kill emission: Alloy uses _EmissionColor (half3) + _EMISSION keyword
            if (m.HasProperty("_EmissionColor"))  m.SetColor("_EmissionColor",  Color.black);
            m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            // Unity Standard shader properties (different names from Alloy)
            if (m.HasProperty("_Metallic"))       m.SetFloat("_Metallic",       0f);    // non-metallic
            if (m.HasProperty("_Smoothness"))     m.SetFloat("_Smoothness",     0.25f); // low — stacked quads don't blow out
            // Legacy fallback shaders (Transparent/Specular etc.) use these
            if (m.HasProperty("_SpecColor"))      m.SetColor("_SpecColor",      new Color(0.1f, 0.1f, 0.1f));
            if (m.HasProperty("_Shininess"))      m.SetFloat("_Shininess",      0.3f);
        }

        // ── Spray materials ───────────────────────────────────────────────────────

        // Spray: Sprites/Default — always in any Unity build, alpha-blends, reads vertex (particle) color.
        static Material BuildSprayMaterial()
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Additive");
            if (ReferenceEquals(sh, null)) return null;
            var mat = new Material(sh);
            // Sprites/Default has its own premul-alpha blend — no SetParticleFadeMode needed.
            if (!ReferenceEquals(_firstBloodTex, null)) mat.mainTexture = _firstBloodTex;
            return mat;
        }

        // Particles/Standard Lit defaults to Opaque — force Fade so alpha from colorOverLifetime works.
        static void SetParticleFadeMode(Material mat)
        {
            mat.SetFloat("_Mode", 2f);           // 2 = Fade
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.SetInt("_SrcBlend", 5);           // SrcAlpha
            mat.SetInt("_DstBlend", 10);          // OneMinusSrcAlpha
            mat.SetInt("_ZWrite",   0);
            mat.renderQueue = 3000;
        }

        void BuildSprayPSes()
        {
            var fadeGrad = new Gradient();
            fadeGrad.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0f),
                                         new GradientColorKey(Color.white, 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1.0f, 0f),
                                         new GradientAlphaKey(0f,   1f) });

            // Pellet PS: small blood-texture blobs
            {
                var go = new GameObject("BSPellet");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _pelletPS = go.AddComponent<ParticleSystem>();
                var mn = _pelletPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(0.5f, 2.5f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.02f, 0.08f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 2000;
                mn.gravityModifier = 0f;
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
                sh.angle     = 15f;  // 30° total cone
                sh.radius    = 0.02f;
                var col = _pelletPS.colorOverLifetime;
                col.enabled = true;
                col.color   = new ParticleSystem.MinMaxGradient(fadeGrad);
                var psr = _pelletPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_pelletMat, null)) psr.material = _pelletMat;
                go.SetActive(true);
            }

            // Fog PS: large blood-texture blobs
            {
                var go = new GameObject("BSFog");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _fogPS = go.AddComponent<ParticleSystem>();
                var mn = _fogPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(0.5f, 2.5f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.07f, 0.22f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 1000;
                mn.gravityModifier = 0f;
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
                sh.angle     = 15f;  // 30° total cone
                sh.radius    = 0.03f;
                var col = _fogPS.colorOverLifetime;
                col.enabled = true;
                col.color   = new ParticleSystem.MinMaxGradient(fadeGrad);
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
                    Vector2 uv; float dark; Vector3 tanNorm;
                    SampleSplatter(out uv, out dark, out tanNorm);

                    Vector3 dir = (fwd + right * uv.x * tanHalf + up * uv.y * tanHalf).normalized;

                    RaycastHit h;
                    if (!Physics.Raycast(exitPt + dir * 0.15f, dir, out h, range)) continue;
                    if (IsSourceSosig(h.collider, srcSosig)) continue;
                    if (h.collider.GetComponentInParent<SosigWeapon>() != null) continue;

                    // Size: base at close range, scales to 3x at CfgRange (half-range = 2x)
                    float dotR = CfgDotSize.Value * Mathf.Clamp(1f + h.distance / 10f, 1f, 3f);
                    int   bin  = Mathf.FloorToInt(h.distance / projSpeed / BIN_S);

                    // Elongation: blood hitting at a glancing angle streaks in the travel direction.
                    // sinAngle = |dot(rayDir, surfaceNormal)| → 1=perpendicular (round), ~0=grazing (very long).
                    float sinAngle  = Mathf.Abs(Vector3.Dot(dir, h.normal));
                    float elong     = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 8f);
                    Vector3 elongVec = dir - Vector3.Dot(dir, h.normal) * h.normal; // project onto surface
                    if (elongVec.sqrMagnitude > 0.001f) elongVec.Normalize();
                    else elongVec = right; // perfectly perpendicular: fallback (round anyway)

                    Rigidbody hitRb = h.collider.attachedRigidbody;
                    Transform par   = hitRb != null ? hitRb.transform : null;
                    var dd = new DotData(h.point, h.normal, dotR, dark, tanNorm, elongVec, elong);

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
                int _dbgHits = 0;
                foreach (var _kv in staticBins) _dbgHits += _kv.Value.Count;
                foreach (var _kv in dynBins) foreach (var _kv2 in _kv.Value) _dbgHits += _kv2.Value.Count;
                Log.LogInfo("[BloodSystem] Projection: " + _dbgHits + " hits, " + (staticBins.Count + dynBins.Count) + " bins");
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
                mn.startColor = new ParticleSystem.MinMaxGradient(new Color(col.r * 0.85f, col.g * 0.85f, col.b * 0.85f, 1f));
                _pelletPS.Emit(250);
            }
            if (!ReferenceEquals(_fogPS, null))
            {
                _fogPS.transform.position = pos;
                _fogPS.transform.rotation = rot;
                var mn = _fogPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(new Color(col.r * 0.85f, col.g * 0.85f, col.b * 0.85f, 1f));
                _fogPS.Emit(100);
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
            mesh.triangles = new[] { 0, 2, 3, 0, 1, 2 };
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
            Log.LogInfo("[BloodSystem] BuildDotMesh " + dots.Count + " mat=" + (!ReferenceEquals(mat, null) ? mat.shader.name : "NULL"));
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

                    // Orient quad: elongation direction = surface-projected bullet path.
                    // Perpendicular direction = cross(norm, elongDir).
                    // For near-perpendicular shots elongation≈1 so the roll is irrelevant.
                    Vector3 elongDir = d.ElongDir;
                    Vector3 perpDir  = Vector3.Cross(norm, elongDir);
                    if (perpDir.sqrMagnitude < 0.001f)
                    {
                        Vector3 qup2 = Mathf.Abs(Vector3.Dot(norm, Vector3.up)) > 0.9f
                                     ? Vector3.forward : Vector3.up;
                        perpDir = Vector3.Cross(norm, qup2);
                    }
                    perpDir.Normalize();

                    Vector3 qr = elongDir * (r * d.Elongation); // stretched in bullet direction
                    Vector3 qu = perpDir  * r;                   // width unchanged
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
                    float darkMult = Mathf.Lerp(0.4f, 1.0f, d.Dark);

                    // Per-dot normal-map shading: tangent-space normal from blood normal map
                    // gives each dot a unique brightness based on its position in the splatter pattern.
                    // Together all dots read as a 3D surface with ridges and depth.
                    float tanShade  = Mathf.Clamp01(d.TanNorm.x * _tanLight.x
                                                  + d.TanNorm.y * _tanLight.y
                                                  + d.TanNorm.z * _tanLight.z);
                    float shadeMult = Mathf.Lerp(0.5f, 1.0f, tanShade);
                    float totalMult = darkMult * shadeMult;
                    Color vc = new Color(col.r * totalMult, col.g * totalMult, col.b * totalMult, 1f);

                    int v = i * 4;
                    verts[v]=c0; verts[v+1]=c1; verts[v+2]=c2; verts[v+3]=c3;
                    uvs[v]  =new Vector2(0,0); uvs[v+1]=new Vector2(1,0);
                    uvs[v+2]=new Vector2(1,1); uvs[v+3]=new Vector2(0,1);
                    cols[v]=cols[v+1]=cols[v+2]=cols[v+3]=vc;
                    int t = i * 6;
                    tris[t]=v; tris[t+1]=v+2; tris[t+2]=v+3;
                    tris[t+3]=v; tris[t+4]=v+1; tris[t+5]=v+2;
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

                if (!BloodSystemPlugin._dbgDotLogged && dots.Count > 0)
                {
                    BloodSystemPlugin._dbgDotLogged = true;
                    var d0 = dots[0];
                    Vector3 wp = parent != null ? parent.TransformPoint(verts[0]) : verts[0];
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] DBG dot[0] worldPos=" + wp
                        + " r=" + d0.R + " norm=" + d0.Norm
                        + " matShader=" + mat.shader.name
                        + " matColor=" + mat.GetColor("_Color")
                        + " hasTex=" + (!ReferenceEquals(mat.mainTexture, null)));
                }
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
        public SosigLink  PrevHitLink;
        public Sosig      LastSosig;
        public Vector3    LastSosigLinkPos;
        public Vector3    LastBulletDir;
        public float      LastBulletSpeed = 400f;
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
        static bool _bloodFiredOnce;
        static readonly FieldInfo FLastColliderHit =
            typeof(BallisticProjectile).GetField("m_lastColliderHit",
                BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FVelocity =
            typeof(BallisticProjectile).GetField("m_velocity",
                BindingFlags.NonPublic | BindingFlags.Instance);

        internal static bool Ok => !ReferenceEquals(FLastColliderHit, null)
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

                // Auto-grab Alloy mat from scene glass objects on first sosig spawn — no wall needed.
                if (ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)
                    && !BloodSystemPlugin._alloyGrabPending)
                    BloodSystemPlugin._instance.StartCoroutine(
                        BloodSystemPlugin.TryGrabAlloyFromScene());
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

            tracker.PrevCollider = FLastColliderHit.GetValue(__instance) as Collider;

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
                    Vector3 exitPt    = segCenter + dir * 0.1f;
                    if (!linkDestroyed)
                    {
                        // Link still alive — raycast to find the exact exit surface point
                        RaycastHit xh;
                        if (Physics.Raycast(segCenter, dir, out xh, 2f)
                            && xh.collider.GetComponentInParent<SosigLink>() != null)
                            exitPt = xh.point + dir * 0.02f;
                    }

                    if (!_bloodFiredOnce)
                    {
                        _bloodFiredOnce = true;
                        BloodSystemPlugin.Log.LogInfo("[BloodSystem] First blood: " + (linkDestroyed ? "kill" : "exit") + " exitPt=" + exitPt);
                    }
                    if (!BloodSystemPlugin._dbgDecalLogged)
                    {
                        BloodSystemPlugin._dbgDecalLogged = true;
                        try
                        {
                            var sb = new System.Text.StringBuilder("[BloodSystem] DBG decal types in scene: ");
                            var seen = new System.Collections.Generic.HashSet<string>();
                            foreach (var mono in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                            {
                                if (mono == null) continue;
                                string tn = mono.GetType().Name;
                                if ((tn.IndexOf("Decal", System.StringComparison.OrdinalIgnoreCase) >= 0
                                  || tn.IndexOf("Hole",  System.StringComparison.OrdinalIgnoreCase) >= 0
                                  || tn.IndexOf("WFX",   System.StringComparison.OrdinalIgnoreCase) >= 0)
                                 && seen.Add(tn))
                                    sb.Append(tn).Append(' ');
                            }
                            BloodSystemPlugin.Log.LogInfo(sb.ToString());
                        }
                        catch { }
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

            // When bullet hits a static wall and Alloy not grabbed yet, scan next frame for WFX decal.
            if (ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)
                && !BloodSystemPlugin._alloyGrabPending
                && !ReferenceEquals(currentCollider, null) && currentCollider != null
                && currentCollider.attachedRigidbody == null
                && currentCollider.GetComponentInParent<SosigLink>() == null)
            {
                BloodSystemPlugin._instance.StartCoroutine(BloodSystemPlugin.TryGrabAlloyFromScene());
            }
        }

        // Grabs WFX decal material the moment the first bullet hole decal activates.
        // Clears _matCache so any Sprites/Default mats cached before this get replaced.
        [HarmonyPatch]
        static class WfxDecalMaterialGrab
        {
            static bool _grabbed;
            static bool Prepare() => AccessTools.TypeByName("WFX_BulletHoleDecal") != null;
            static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("WFX_BulletHoleDecal");
                if (t == null) return null;
                var m = AccessTools.Method(t, "Start");
                if (m == null) m = AccessTools.Method(t, "Awake");
                return m;
            }
            static void Postfix(Component __instance)
            {
                if (_grabbed) return;
                if (!ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)) { _grabbed = true; return; }
                try
                {
                    var r = __instance.GetComponent<Renderer>();
                    if (ReferenceEquals(r, null) || ReferenceEquals(r.sharedMaterial, null)) return;
                    BloodSystemPlugin._decalSourceMat = new Material(r.sharedMaterial);
                    BloodSystemPlugin._decalSourceMat.SetInt("_Cull", 0);
                    BloodSystemPlugin._decalSourceSearched = true;
                    BloodSystemPlugin._matCache.Clear();
                    _grabbed = true;
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] WFX mat grabbed on decal Start: "
                        + r.sharedMaterial.shader.name);
                }
                catch (Exception ex)
                {
                    BloodSystemPlugin.Log.LogWarning("[BloodSystem] WFxDecalGrab: " + ex.Message);
                }
            }
        }
    }
}
