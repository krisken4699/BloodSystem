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
        public float   Dist;       // hit distance — used for range-edge alpha fade
        public DotData(Vector3 pos, Vector3 norm, float r, float dark,
                       Vector3 tanNorm, Vector3 elongDir, float elongation, float dist)
        {
            Pos = pos; Norm = norm; R = r; Dark = dark;
            TanNorm = tanNorm; ElongDir = elongDir; Elongation = elongation; Dist = dist;
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
        internal static ConfigEntry<string> CfgProjectionMode;
        internal static ConfigEntry<float>  CfgSpeedRatio;
        internal static ConfigEntry<float>  CfgSpeedBias;
        internal static ConfigEntry<float>  CfgDotScaleMax;
        internal static ConfigEntry<float>  CfgDotScaleRange;
        internal static ConfigEntry<int>    CfgGibRayCount;

        internal static readonly Color _mustardFallback = new Color(0.9f, 0.8f, 0f, 1f);

        // Decal material cache: one Material per blood Color — uses _decalTex (soft circle)
        internal static readonly Dictionary<Color, Material> _matCache     = new Dictionary<Color, Material>();
        // Drip stain material cache: same as _matCache but texture = _hardCircleTex
        internal static readonly Dictionary<Color, Material> _dripMatCache = new Dictionary<Color, Material>();

        // Shot group tracking: all GOs from one shot grouped by ID.
        // Evict the oldest shot group when CfgMaxShots is exceeded.
        static int _nextShotId;
        static readonly Dictionary<int, List<GameObject>> _shotGroups = new Dictionary<int, List<GameObject>>();
        static readonly Queue<int>        _shotQueue = new Queue<int>();
        // Drip queue: per-particle stains from VanillaDripStainer (no shot context), evicted individually.
        static readonly Queue<GameObject> _dripQueue = new Queue<GameObject>();
        internal static ConfigEntry<int>  CfgMaxShots;
        internal static ConfigEntry<int>  CfgMaxDrips;

        struct FadingStainState { public Mesh M; public Color BaseCol; public float SpawnTime; }
        static readonly List<FadingStainState> _fadingStains   = new List<FadingStainState>();
        static readonly Color[]                _fadeColorBuf   = new Color[4];
        static Shader    _bloodShader;
        static bool      _bloodShaderSearched;
        internal static Material  _decalSourceMat;
        internal static bool      _decalSourceSearched;
        internal static bool      _dbgDotLogged;
        internal static bool      _dbgDecalLogged;

        // _decalTex  = procedural gaussian soft circle (WHITE) — used for splash/spray dots
        // _hardCircleTex = hard-edge circle — used for drip stains (crisp edge, no feather)
        // Blood PNGs are used ONLY for CDF ray-direction sampling and spray particle texture
        static Texture2D   _decalTex;
        static Texture2D   _hardCircleTex;
        static Texture2D         _firstBloodTex; // first valid PNG loaded — used as spray particle texture
        static List<Texture2D>   _allTextures;   // all blood PNGs — picked randomly per impact decal
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

        // Spray: two persistent PSes (Sprites/Default — confirmed alpha-blend in Unity 5)
        static ParticleSystem _pelletPS; // mid-fog layer (10-30°)
        static ParticleSystem _fogPS;   // outer-fog layer (40-80°)
        static ParticleSystem _jetPS;   // inner drops     (0-10°)
        static Material       _fogMat;
        static Material       _pelletMat;
        // Dot mesh base material: Particles/Standard Lit + soft circle (scene-lit for persistent stains)
        static Material       _dotBaseMat;
        // Shared unit-quad mesh (kept for potential future use)
        static Mesh           _dotQuadMesh;
        // Flying-dot particle system: one PS = one draw call for all in-flight blood dots
        static ParticleSystem _flyingDotPS;
        // Pre-allocated particle buffers — avoids per-shot GC allocation
        static ParticleSystem.Particle[] _flyBuf      = new ParticleSystem.Particle[4000];
        static ParticleSystem.Particle[] _flyMergeBuf = new ParticleSystem.Particle[8000];

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
            CfgRayCount  = Config.Bind("Blood", "Max rays per shot",  3000,   "Maximum splash ray count. Capped to the actual number of image pixels if fewer.");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",   10f,    "Half-angle in degrees of the splash cone.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",   0.008f, "Base radius of each splash dot in metres. Scales linearly to 3x at 20 metres.");
            CfgRange          = Config.Bind("Blood", "Range metres",            50f,       "Maximum splash distance in metres.");
            CfgProjectionMode = Config.Bind("Blood", "Projection Mode",         "Animated",
                "How splash dots appear. Animated: dots fly from wound to wall in real-time (best visuals, most FPS cost). " +
                "Delayed: dots appear all at once after a timed delay with no animation (moderate). " +
                "Immediate: dots appear instantly with no delay and no animation (cheapest, best for low-end systems).");
            CfgSpeedRatio     = Config.Bind("Blood", "Projection Speed Ratio",  2f,
                "Multiplies bullet exit speed to calculate how fast splash dots travel toward the wall. " +
                "Higher = faster animation, less time spread between near and far dots. Default 2.");
            CfgSpeedBias      = Config.Bind("Blood", "Projection Speed Bias",   10f,
                "Flat metres-per-second added to projection speed after the ratio multiply. " +
                "Prevents dots from moving too slowly for low-velocity bullets. Default 10.");
            CfgDotScaleMax    = Config.Bind("Blood", "Dot Max Scale",           5f,
                "Maximum size multiplier applied to splash dots at Dot Scale Range distance. " +
                "5 means dots at full range are 5x the base radius. Default 5.");
            CfgDotScaleRange  = Config.Bind("Blood", "Dot Scale Range metres",  50f,
                "Distance in metres at which splash dots reach their maximum size (Dot Max Scale). " +
                "Dots near the wound start at Dot Base Radius and grow linearly to this range. Default 50.");
            CfgGibRayCount    = Config.Bind("Blood", "Gib Ray Count",           200,
                "Number of rays fired in random 360-degree directions when a segment explodes. " +
                "Lower values improve FPS in gib-heavy fights. Capped by image pixel count. Default 200.");
            CfgMaxShots = Config.Bind("Blood", "Max shot groups", 20,
                "Maximum number of shots whose splash and drip decals stay visible. When exceeded, the oldest shot's decals are all deleted together.");
            CfgMaxDrips = Config.Bind("Blood", "Max drip stains", 400,
                "Maximum drip stains from particle detection (VanillaDripStainer). Oldest deleted when exceeded.");

            // Soft-circle for splash dots, hard-circle for drip stains
            _decalTex      = MakeSoftCircle(96);
            _hardCircleTex = MakeHardCircle(64);

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

            // Unit quad (kept for potential future use)
            {
                _dotQuadMesh = new Mesh();
                _dotQuadMesh.vertices  = new[] { new Vector3(-0.5f,-0.5f,0f), new Vector3(0.5f,-0.5f,0f),
                                                  new Vector3(0.5f, 0.5f,0f), new Vector3(-0.5f,0.5f,0f) };
                _dotQuadMesh.uv        = new[] { new Vector2(0f,0f), new Vector2(1f,0f),
                                                  new Vector2(1f,1f), new Vector2(0f,1f) };
                _dotQuadMesh.triangles = new[] { 0, 2, 3, 0, 1, 2 };
                _dotQuadMesh.RecalculateBounds();
            }

            // Flying-dot PS: one draw call for all in-flight blood; particles auto-die as stains appear
            {
                var fgo = new GameObject("FlyingDotPS");
                DontDestroyOnLoad(fgo);
                _flyingDotPS = fgo.AddComponent<ParticleSystem>();
                var mn = _flyingDotPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(0f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.02f);
                mn.loop            = false;
                mn.playOnAwake     = false;
                mn.maxParticles    = 8000;
                mn.simulationSpace = ParticleSystemSimulationSpace.World;
                mn.gravityModifier = 0f; // velocity already encodes direction; no gravity so dots fly straight
                var em = _flyingDotPS.emission;
                em.enabled      = false; // manual SetParticles only
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                var psr = _flyingDotPS.GetComponent<ParticleSystemRenderer>();
                psr.renderMode  = ParticleSystemRenderMode.Billboard;
            }

            // Load ALL blood PNGs and build combined CDF
            var allTextures = LoadAllPngs();
            if (allTextures.Count > 0)
            {
                _firstBloodTex = allTextures[0];
                _allTextures   = allTextures;
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
            // Give flying-dot PS the same material as spray pellets (blood texture, alpha-blend)
            if (!ReferenceEquals(_flyingDotPS, null) && !ReferenceEquals(_pelletMat, null))
                _flyingDotPS.GetComponent<ParticleSystemRenderer>().material = _pelletMat;
            BuildSprayPSes();

            new Harmony("h3vr.invent60.bloodsystem").PatchAll(typeof(BloodSystemPatches));
            Log.LogInfo("[BloodSystem] 3.0.0 loaded. FieldsOK=" + BloodSystemPatches.Ok);
        }

        // ── Centralized stain fade ────────────────────────────────────────────────

        void Update()
        {
            if (_fadingStains.Count == 0) return;
            float now      = Time.time;
            float lifetime = CfgLifetime.Value;
            float fadeAt   = lifetime * 0.45f; // start fading at 45% of lifetime (faster than halfway)
            for (int i = _fadingStains.Count - 1; i >= 0; i--)
            {
                var s = _fadingStains[i];
                if (s.M == null) { _fadingStains.RemoveAt(i); continue; }
                float age = now - s.SpawnTime;
                if (age >= lifetime) { _fadingStains.RemoveAt(i); continue; }
                if (age <= fadeAt) continue;
                float t     = (age - fadeAt) / (lifetime - fadeAt);
                float alpha = s.BaseCol.a * Mathf.Clamp01(1f - t);
                Color c     = new Color(s.BaseCol.r, s.BaseCol.g, s.BaseCol.b, alpha);
                _fadeColorBuf[0] = _fadeColorBuf[1] = _fadeColorBuf[2] = _fadeColorBuf[3] = c;
                s.M.colors = _fadeColorBuf;
            }
        }

        // ── Drip stain material cache (hard circle, cached per color) ─────────────

        internal static Material GetDripMat(Color col)
        {
            Material m;
            if (_dripMatCache.TryGetValue(col, out m) && !ReferenceEquals(m, null)) return m;
            Material src = GetBloodMat(col);
            if (ReferenceEquals(src, null))
            {
                if (ReferenceEquals(_pelletMat, null)) return null;
                m = new Material(_pelletMat);
                m.color = col;
            }
            else
            {
                m = new Material(src);
            }
            if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex; // soft circle, same as splash dots
            _dripMatCache[col] = m;
            return m;
        }

        // ── Shot group helpers ────────────────────────────────────────────────────

        internal static List<GameObject> StartShotGroup()
        {
            int id = _nextShotId++;
            var list = new List<GameObject>();
            _shotGroups[id] = list;
            _shotQueue.Enqueue(id);
            EvictOldShots();
            return list;
        }

        static void EvictOldShots()
        {
            while (_shotQueue.Count > CfgMaxShots.Value)
            {
                int old = _shotQueue.Dequeue();
                List<GameObject> objs;
                if (_shotGroups.TryGetValue(old, out objs))
                {
                    foreach (var o in objs)
                        if (!ReferenceEquals(o, null)) UnityEngine.Object.Destroy(o);
                    _shotGroups.Remove(old);
                }
            }
        }

        // Registers a GO to the shot group, or into the drip queue if no group (VanillaDripStainer path).
        internal static void TrackGO(GameObject go, List<GameObject> shotList)
        {
            if (shotList != null)
            {
                shotList.Add(go);
            }
            else
            {
                _dripQueue.Enqueue(go);
                while (_dripQueue.Count > CfgMaxDrips.Value)
                {
                    var old = _dripQueue.Dequeue();
                    if (!ReferenceEquals(old, null)) UnityEngine.Object.Destroy(old);
                }
            }
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

        // Hard-edge circle: fully opaque inside radius, fully transparent outside. No feathering.
        // Used for drip stains to give a crisp blood-drop look.
        static Texture2D MakeHardCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            var pix = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float a  = (dx*dx + dy*dy <= 1f) ? 1f : 0f;
                pix[y*size+x] = new Color(1f, 1f, 1f, a);
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
                var imgNorms = new List<Vector3>(w * h / 4);
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

                    // Derive tangent-space normal from alpha gradient: treats blood coverage
                    // as a height field so drop edges face outward and centers face up.
                    float aL = px > 0   ? pixels[py*w + (px-1)].a : c.a;
                    float aR = px < w-1 ? pixels[py*w + (px+1)].a : c.a;
                    float aD = py > 0   ? pixels[(py-1)*w + px].a  : c.a;
                    float aU = py < h-1 ? pixels[(py+1)*w + px].a  : c.a;
                    float ndx = (aR - aL) * 3f;
                    float ndy = (aU - aD) * 3f;

                    imgUVs.Add(new Vector2(u, v));
                    imgDarks.Add(1f - lum);   // 1=black pixel area, 0=white pixel area
                    imgNorms.Add(new Vector3(-ndx, -ndy, 1f).normalized);
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
                    normList.Add(imgNorms[i]);
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

        // Reconstruct Alloy/Core transparent material.
        // Primary path: hardcoded keywords confirmed from real H3VR session scan.
        // PlayerTorsoGeo + Quickbelt_MagSlot_Constant use this variant in every scene, so it is
        // always compiled into the game's shader cache — no glass, no wall-shoot required.
        // Fallback: saved cache file (covers edge case where shader name changed between game versions).
        static void TryLoadFromBundle()  // name kept so Awake call doesn't change
        {
            Shader shHard = Shader.Find("Alloy/Core");
            if (!ReferenceEquals(shHard, null))
            {
                var mat = new Material(shHard);
                mat.renderQueue = 3000;
                mat.EnableKeyword("EFFECT_BUMP");
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.EnableKeyword("_RIM_ON");
                if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", 1);
                if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", 10);
                if (mat.HasProperty("_ZWrite"))   mat.SetInt("_ZWrite",   0);
                if (mat.HasProperty("_Mode"))     mat.SetFloat("_Mode",   3f);
                mat.SetInt("_Cull", 0);
                _decalSourceMat      = mat;
                _decalSourceSearched = true;
                Log.LogInfo("[BloodSystem] Alloy/Core hardcoded transparent: rq=3000 EFFECT_BUMP _ALPHAPREMULTIPLY_ON _RIM_ON");
                return;
            }
            // Cache file fallback
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
                _dripMatCache.Clear();
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

            // Alloy Core PBR — non-metallic, matte blood.
            // _Specularity=0 + _RIM_ON disabled: both cause the full quad rect to glow white
            // at glancing angles because they don't alpha-weight their contribution on transparent corners.
            if (m.HasProperty("_Metal"))          m.SetFloat("_Metal",          0f);
            if (m.HasProperty("_Specularity"))    m.SetFloat("_Specularity",    0f);
            if (m.HasProperty("_Roughness"))      m.SetFloat("_Roughness",      0.8f);
            m.DisableKeyword("_RIM_ON");
            // Kill emission
            if (m.HasProperty("_EmissionColor"))  m.SetColor("_EmissionColor",  Color.black);
            m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            // Switch from premultiplied (SrcBlend=One) to standard alpha so transparent corners
            // can't additively leak rim/env-probe color through them at glancing angles.
            if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", 5);  // SrcAlpha
            if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", 10); // OneMinusSrcAlpha
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.EnableKeyword("_ALPHABLEND_ON");

            // Unity Standard / legacy fallback
            if (m.HasProperty("_Metallic"))       m.SetFloat("_Metallic",       0f);
            if (m.HasProperty("_Smoothness"))     m.SetFloat("_Smoothness",     0.1f);
            if (m.HasProperty("_SpecColor"))      m.SetColor("_SpecColor",      Color.black);
            if (m.HasProperty("_Shininess"))      m.SetFloat("_Shininess",      0.05f);

            // Bump map: normal noise.jpg as PBR normal map. EFFECT_BUMP keyword enables it in Alloy.
            // No-op if H3VR stripped this shader variant; vertex-color shading still works as fallback.
            if (!ReferenceEquals(_normalMapTex, null) && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", _normalMapTex);
                m.EnableKeyword("EFFECT_BUMP");
            }
        }

        // ── Spray materials ───────────────────────────────────────────────────────

        // Spray: Sprites/Default — confirmed alpha-blends + reads particle color in Unity 5.
        static Material BuildSprayMaterial()
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (ReferenceEquals(sh, null)) sh = Shader.Find("Particles/Additive");
            if (ReferenceEquals(sh, null)) return null;
            var mat = new Material(sh);
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

            // Outer ring (80-90°): ConeShell = only the surface, not the interior
            // Thin particles, slow, bloom outward — stays as a ring skirt
            {
                var go = new GameObject("BSFog");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _fogPS = go.AddComponent<ParticleSystem>();
                var mn = _fogPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.25f, 0.4f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.004f, 0.012f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 2000;
                mn.gravityModifier = 0.05f; // outer fog barely falls — less dense, less gravity
                mn.loop            = false;
                mn.playOnAwake     = false;
                mn.duration        = 0.5f;
                mn.startColor      = new ParticleSystem.MinMaxGradient(_mustardFallback);
                var em = _fogPS.emission;
                em.enabled      = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                var sh = _fogPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.ConeShell; // surface only = ring
                sh.angle     = 85f;
                sh.radius    = 0.02f;
                var sol = _fogPS.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 2.5f)));
                var col = _fogPS.colorOverLifetime;
                col.enabled = true;
                col.color   = new ParticleSystem.MinMaxGradient(fadeGrad);
                var psr = _fogPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_fogMat, null)) psr.material = _fogMat;
                go.SetActive(true);
            }

            // Mid fog (10-30°): medium blobs rush out of the outer ring, grow moderately
            {
                var go = new GameObject("BSPellet");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _pelletPS = go.AddComponent<ParticleSystem>();
                var mn = _pelletPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(1.5f, 4.0f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 2000;
                mn.gravityModifier = 0.35f; // mid fog — moderate gravity
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
                sh.angle     = 20f;
                sh.radius    = 0.02f;
                var sol = _pelletPS.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(1f, 1.6f)));
                var col = _pelletPS.colorOverLifetime;
                col.enabled = true;
                col.color   = new ParticleSystem.MinMaxGradient(fadeGrad);
                var psr = _pelletPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_pelletMat, null)) psr.material = _pelletMat;
                go.SetActive(true);
            }

            // Inner drops (0-10°): individual drops, no scaling, go furthest, visible line
            {
                var go = new GameObject("BSJet");
                DontDestroyOnLoad(go);
                go.SetActive(false);
                _jetPS = go.AddComponent<ParticleSystem>();
                var mn = _jetPS.main;
                mn.startLifetime   = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                mn.startSpeed      = new ParticleSystem.MinMaxCurve(8.0f, 18.0f);
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.012f, 0.03f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 1000;
                mn.gravityModifier = 1.2f; // inner dense drops — pulled down hard
                mn.loop            = false;
                mn.playOnAwake     = false;
                mn.duration        = 0.5f;
                mn.startColor      = new ParticleSystem.MinMaxGradient(_mustardFallback);
                var em = _jetPS.emission;
                em.enabled      = true;
                em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
                var sh = _jetPS.shape;
                sh.enabled   = true;
                sh.shapeType = ParticleSystemShapeType.Cone;
                sh.angle     = 5f;
                sh.radius    = 0.005f;
                // no sizeOverLifetime — drops stay same size
                var col = _jetPS.colorOverLifetime;
                col.enabled = true;
                col.color   = new ParticleSystem.MinMaxGradient(fadeGrad);
                var psr = _jetPS.GetComponent<ParticleSystemRenderer>();
                if (psr != null && !ReferenceEquals(_pelletMat, null)) psr.material = _pelletMat;
                go.SetActive(true);
            }

            // Spray PSes — attach stainer in spray mode: 80% small dot / 15% nothing / 5% streak.
            foreach (var ps in new ParticleSystem[] { _fogPS, _pelletPS, _jetPS })
            {
                if (ReferenceEquals(ps, null)) continue;
                var stainer = ps.gameObject.AddComponent<VanillaDripStainer>();
                stainer.SetUseParticleColor();
                stainer.SetSprayMode();
            }
        }

        // ── Spawn: splash projection ──────────────────────────────────────────────

        internal static void SpawnProjection(Vector3 exitPt, Vector3 projDir,
                                              Sosig srcSosig, float bulletSpeed,
                                              bool gib = false, List<GameObject> shotList = null)
        {
            if (!CfgEnabled.Value) return;
            try
            {
                Color   col   = GetSosigBloodColor(srcSosig);
                Vector3 fwd   = projDir.normalized;
                float   range = CfgRange.Value;

                // Lift exitPt above embedded floor
                {
                    var snapHits = Physics.RaycastAll(exitPt + Vector3.up * 0.5f, Vector3.down, 0.6f);
                    System.Array.Sort(snapHits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var sh in snapHits)
                    {
                        if (sh.collider.attachedRigidbody != null) continue;
                        if (sh.collider.GetComponentInParent<SosigLink>() != null) continue;
                        if (sh.normal.y < 0.5f) continue;
                        exitPt = sh.point + sh.normal * 0.05f;
                        break;
                    }
                }

                if (gib)
                {
                    // Gib: several decals scattered in random directions
                    int gibDecals = Mathf.Clamp(CfgGibRayCount.Value / 15, 3, 8);
                    for (int i = 0; i < gibDecals; i++)
                    {
                        Vector3 dir = UnityEngine.Random.onUnitSphere;
                        RaycastHit h;
                        if (!Physics.Raycast(exitPt, dir, out h, range)) continue;
                        if (IsSourceSosig(h.collider, srcSosig)) continue;
                        if (h.collider.GetComponentInParent<SosigWeapon>() != null) continue;
                        if (h.collider.attachedRigidbody != null) continue;
                        SpawnImpactDecal(h.point, h.normal, dir, h.distance, col, shotList, sizeScale: 0.7f);
                    }
                }
                else
                {
                    // Normal shot: one decal on the primary surface behind the sosig
                    RaycastHit h;
                    if (!Physics.Raycast(exitPt - fwd * 0.15f, fwd, out h, range)) return;
                    if (IsSourceSosig(h.collider, srcSosig)) return;
                    if (h.collider.GetComponentInParent<SosigWeapon>() != null) return;
                    if (h.collider.attachedRigidbody != null) return;
                    SpawnImpactDecal(h.point, h.normal, fwd, h.distance, col, shotList);
                }
            }
            catch (Exception ex) { Log.LogError("[BloodSystem] SpawnProjection: " + ex); }
        }

        static void SpawnImpactDecal(Vector3 hitPt, Vector3 hitNormal, Vector3 hitDir,
                                     float dist, Color col, List<GameObject> shotList,
                                     float sizeScale = 1f)
        {
            if (_allTextures == null || _allTextures.Count == 0) return;
            Material baseMat = GetBloodMat(col);
            if (ReferenceEquals(baseMat, null)) return;

            Texture2D bloodTex = _allTextures[UnityEngine.Random.Range(0, _allTextures.Count)];

            // Size: bigger up close (≤1m → 18cm radius), smaller at range (10m → 5cm)
            float baseR = Mathf.Lerp(0.18f, 0.05f, Mathf.Clamp01(dist / 10f)) * sizeScale;

            // Elongation: grazing angle → stretched along bullet direction
            Vector3 N    = hitNormal.normalized;
            Vector3 vDir = hitDir.normalized;
            float sinAngle = Mathf.Abs(Vector3.Dot(vDir, N));
            float elong    = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 4f);

            // Orient quad: elongDir = bullet projected onto surface, perpDir = cross(N, elongDir)
            Vector3 elongDir = vDir - Vector3.Dot(vDir, N) * N;
            if (elongDir.sqrMagnitude > 0.001f) elongDir.Normalize();
            else elongDir = Mathf.Abs(Vector3.Dot(N, Vector3.right)) > 0.9f ? Vector3.forward : Vector3.right;
            Vector3 perpDir = Vector3.Cross(N, elongDir);
            if (perpDir.sqrMagnitude < 0.001f) perpDir = Vector3.Cross(N, Vector3.forward);
            perpDir.Normalize();

            Vector3 qr = elongDir * (baseR * elong);
            Vector3 qu = perpDir  * baseR;
            Vector3 bp = hitPt + N * 0.004f;

            var mesh = new Mesh();
            mesh.vertices  = new Vector3[] { bp-qr-qu, bp+qr-qu, bp+qr+qu, bp-qr+qu };
            mesh.uv        = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            mesh.colors    = new Color[]   { col, col, col, col };
            mesh.triangles = new int[]     { 0,2,3, 0,1,2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            var mat = new Material(baseMat);
            mat.mainTexture = bloodTex;
            if (!ReferenceEquals(_normalMapTex, null) && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", _normalMapTex);
                mat.EnableKeyword("EFFECT_BUMP");
            }

            var go = new GameObject("BD");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            TrackGO(go, shotList);
            _fadingStains.Add(new FadingStainState { M = mesh, BaseCol = col, SpawnTime = Time.time });
        }

        static IEnumerator DoDelayedSpawn(
            Dictionary<int, List<DotData>> staticBins,
            Dictionary<int, Dictionary<Transform, List<DotData>>> dynBins,
            Color col, float binSize, List<GameObject> shotList)
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
                    BuildDotMesh(slist, null, col, shotList);

                Dictionary<Transform, List<DotData>> dmap;
                if (dynBins.TryGetValue(b, out dmap))
                    foreach (var kv in dmap)
                        if (!ReferenceEquals(kv.Key, null) && kv.Key != null)
                            BuildDotMesh(kv.Value, kv.Key, col, shotList);
            }
        }

        // ── Spawn: spray ──────────────────────────────────────────────────────────

        // explode=true fires a 360° sphere burst. speedScale > 1 → longer lifetime + faster particles.
        // burstFraction (0-1) scales down emit counts for wound bursts vs full gib bursts.
        internal static void SpawnBloodSpray(Vector3 pos, Vector3 fwd, Color col, bool explode = false, float speedScale = 1f, float burstFraction = 1f)
        {
            if (!CfgEnabled.Value) return;
            Quaternion rot = Quaternion.LookRotation(fwd);
            float sc = explode ? Mathf.Clamp(speedScale, 0.2f, 2.5f) : 1f;
            float bf = Mathf.Clamp01(burstFraction);

            // ── Outer fog: slow mist, blooms wide ────────────────────────────────
            if (!ReferenceEquals(_fogPS, null))
            {
                _fogPS.transform.position = pos;
                _fogPS.transform.rotation = rot;
                var mn = _fogPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.2f * sc, 0.4f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(0.5f * sc, 2.0f * sc);
                    var sh = _fogPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.05f;
                    _fogPS.Emit(Mathf.RoundToInt(500 * bf));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.4f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
                    var sh = _fogPS.shape;
                    sh.shapeType = ParticleSystemShapeType.ConeShell; sh.angle = 85f; sh.radius = 0.02f;
                    _fogPS.Emit(500);
                }
            }

            // ── Mid fog: medium blobs, moderate speed ─────────────────────────────
            if (!ReferenceEquals(_pelletPS, null))
            {
                _pelletPS.transform.position = pos;
                _pelletPS.transform.rotation = rot;
                var mn = _pelletPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.2f * sc, 0.4f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(1.5f * sc, 5.0f * sc);
                    var sh = _pelletPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.05f;
                    _pelletPS.Emit(Mathf.RoundToInt(600 * bf));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(1.5f, 4.0f);
                    var sh = _pelletPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 20f; sh.radius = 0.02f;
                    _pelletPS.Emit(400);
                }
            }

            // ── Inner drops: fast, dense core ─────────────────────────────────────
            if (!ReferenceEquals(_jetPS, null))
            {
                _jetPS.transform.position = pos;
                _jetPS.transform.rotation = rot;
                var mn = _jetPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.2f * sc, 0.4f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(3.0f * sc, 9.0f * sc);
                    var sh = _jetPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.03f;
                    _jetPS.Emit(Mathf.RoundToInt(300 * bf));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(8.0f, 18.0f);
                    var sh = _jetPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 5f; sh.radius = 0.005f;
                    _jetPS.Emit(200);
                }
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
            mesh.RecalculateTangents();

            var go = new GameObject("DS");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
        }

        // ── Custom blood drop effect ──────────────────────────────────────────────
        // Launches small blood drops from a wound point. Each drop simulates gravity,
        // then on surface contact spawns a hard-edge circle stain that grows to 1.5x.

        static bool _dbgDropLogged;

        // Spawns visible blood-drop particles at the wound and a coroutine that predicts landing
        // and places stain decals at the right time — no fragile particle polling needed.
        internal static void SpawnBloodDrops(Vector3 pos, Vector3 outward, Color col, int count, List<GameObject> shotList = null)
        {
            if (!CfgEnabled.Value) return;
            if (ReferenceEquals(_instance, null)) return;
            Vector3 out2     = outward.sqrMagnitude > 0.001f ? outward.normalized : Vector3.up;
            Vector3 spawnPos = pos + out2 * 0.08f;

            if (!_dbgDropLogged)
            {
                _dbgDropLogged = true;
                Log.LogInfo("[BloodSystem] SpawnBloodDrops count=" + count + " pos=" + spawnPos);
            }

            // Visual particles — just for the drop animation, stains are handled by DoDropStains
            var go   = new GameObject("BDrp");
            go.transform.position = spawnPos;
            go.transform.rotation = Quaternion.LookRotation(out2);
            var ps   = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(0.3f, 1.5f);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.008f, 0.025f);
            main.gravityModifier = 1f;
            main.loop            = false;
            main.playOnAwake     = false;
            main.maxParticles    = 64;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor      = new ParticleSystem.MinMaxGradient(col);
            var sh = ps.shape;
            sh.enabled   = true;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle     = 55f;
            sh.radius    = 0.02f;
            var em = ps.emission;
            em.enabled      = true;
            em.rateOverTime = new ParticleSystem.MinMaxCurve(0f);
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null && !ReferenceEquals(_pelletMat, null))
            { var dmat = new Material(_pelletMat); dmat.color = col; psr.material = dmat; }
            ps.Play();
            ps.Emit(count);
            UnityEngine.Object.Destroy(go, 6f);

            // Predict landing and schedule stains via coroutine — no particle polling
            _instance.StartCoroutine(DoDropStains(spawnPos, col, count, shotList));
        }

        static IEnumerator DoDropStains(Vector3 pos, Color col, int count, List<GameObject> shotList)
        {
            // RaycastAll so sosig body between wound and floor doesn't block
            var origin = pos + Vector3.up * 0.1f;
            var hits   = Physics.RaycastAll(origin, Vector3.down, 6f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            RaycastHit floor = default;
            bool found = false;
            foreach (var h in hits)
            {
                if (h.collider.attachedRigidbody != null) continue;
                if (h.collider.GetComponentInParent<SosigLink>() != null) continue;
                if (h.normal.y < 0.5f) continue;
                if (h.collider.GetComponentInParent<Canvas>() != null) continue;
                if (h.collider.gameObject.layer == 5) continue;
                floor = h;
                found = true;
                break;
            }
            if (!found) yield break;

            float delay = Mathf.Clamp(Mathf.Sqrt(2f * Mathf.Max(0.01f, floor.distance) / 9.81f), 0.05f, 3.5f);
            yield return new WaitForSeconds(delay);

            float fallSpeed = Mathf.Sqrt(2f * 9.81f * Mathf.Max(0.01f, floor.distance));
            Vector3 fallVel = Vector3.down * fallSpeed;
            int stainN = Mathf.Min(count, 8);
            for (int i = 0; i < stainN; i++)
            {
                Vector2 off = UnityEngine.Random.insideUnitCircle * 0.15f;
                SpawnDripStainStreak(floor.point + new Vector3(off.x, 0f, off.y), fallVel, floor.normal, col, shotList);
            }
        }

        // Drips stains onto the floor below a wound over several seconds after a confirmed penetration.
        // Fires once per bullet wound, independent of vanilla particle systems.
        internal static IEnumerator DrippingWound(Vector3 woundPt, Sosig sosig, Color col)
        {
            int drips = UnityEngine.Random.Range(4, 9);
            for (int i = 0; i < drips; i++)
            {
                yield return new WaitForSeconds(UnityEngine.Random.Range(1.5f, 3.5f));
                if (sosig == null) yield break;

                var hits = Physics.RaycastAll(woundPt + Vector3.up * 0.1f, Vector3.down, 6f);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    if (h.collider.attachedRigidbody != null) continue;
                    if (h.collider.GetComponentInParent<SosigLink>() != null) continue;
                    if (h.normal.y < 0.5f) continue;
                    if (h.collider.GetComponentInParent<Canvas>() != null) continue;
                    if (h.collider.gameObject.layer == 5) continue;
                    SpawnGrowingStain(h.point, h.normal, col);
                    break;
                }
            }
        }

        static bool _growingStainLoggedOnce;

        // Spawns a hard-edge circle decal that starts at radius r and grows to 2×r.
        internal static void SpawnGrowingStain(Vector3 pos, Vector3 normal, Color col)
        {
            if (!_growingStainLoggedOnce)
            {
                _growingStainLoggedOnce = true;
                Log.LogInfo("[BloodSystem] SpawnGrowingStain pos=" + pos + " normal=" + normal);
            }
            Material mat = GetDripMat(col);
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.003f, 0.02f);
            Vector3    qup = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f
                           ? Vector3.forward : Vector3.up;
            Quaternion rot = Quaternion.LookRotation(normal, qup)
                           * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));

            var mesh = new Mesh();
            mesh.vertices  = new Vector3[] {
                new Vector3(-1,-1,0), new Vector3(1,-1,0),
                new Vector3(1, 1,0), new Vector3(-1,1,0) };
            mesh.uv        = new Vector2[] {
                new Vector2(0,0), new Vector2(1,0),
                new Vector2(1,1), new Vector2(0,1) };
            mesh.colors    = new Color[] { col, col, col, col };
            mesh.triangles = new int[] { 0, 2, 3, 0, 1, 2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            var go = new GameObject("GS");
            go.transform.position   = pos + normal * 0.003f;
            go.transform.rotation   = rot;
            go.transform.localScale = Vector3.one * r;
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
        }

        // Single stretched stain: one ellipse quad per droplet, elongated along the
        // surface-projected travel direction. Head-on impact = round; grazing = elongated.
        // Same elongation logic as BuildDotMesh splash dots.
        internal static void SpawnDripStainStreak(Vector3 origin, Vector3 worldVel, Vector3 hitNormal, Color col, List<GameObject> shotList = null, bool sprayStreak = false)
        {
            Material mat = GetDripMat(col);
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.024f, 0.072f);

            Vector3 N    = hitNormal.normalized;
            Vector3 vDir = worldVel.sqrMagnitude > 0.001f ? worldVel.normalized : -N;

            float sinAngle = Mathf.Abs(Vector3.Dot(vDir, N));
            float elong    = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 6f);

            // Spray streaks: short = fully opaque, long = 50% min alpha; normal streaks: full opacity.
            if (sprayStreak)
                col = new Color(col.r, col.g, col.b,
                                Mathf.Lerp(1.0f, 0.5f, (elong - 1f) / 5f));

            // Elongation direction = velocity projected onto surface plane
            Vector3 elongDir = worldVel - Vector3.Dot(worldVel, N) * N;
            if (elongDir.sqrMagnitude > 0.001f)
                elongDir.Normalize();
            else
            {
                Vector3 up2 = Mathf.Abs(Vector3.Dot(N, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
                elongDir = Vector3.Cross(N, up2).normalized;
            }
            Vector3 perpDir = Vector3.Cross(N, elongDir);
            if (perpDir.sqrMagnitude < 0.001f) perpDir = Vector3.Cross(N, Vector3.forward);
            perpDir.Normalize();

            Vector3 qr = elongDir * (r * elong);
            Vector3 qu = perpDir  * r;
            Vector3 bp = origin + N * 0.003f;

            var mesh = new Mesh();
            mesh.vertices  = new Vector3[] { bp-qr-qu, bp+qr-qu, bp+qr+qu, bp-qr+qu };
            mesh.uv        = new Vector2[] { new Vector2(0,0), new Vector2(1,0),
                                             new Vector2(1,1), new Vector2(0,1) };
            mesh.colors    = new Color[]   { col, col, col, col };
            mesh.triangles = new int[]     { 0,2,3, 0,1,2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            var go = new GameObject("DStr");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            TrackGO(go, shotList);
            _fadingStains.Add(new FadingStainState { M = mesh, BaseCol = col, SpawnTime = Time.time });
        }

        // Small round dot left by spray particles — Alloy + soft circle, same look as BD splash dots.
        // Alpha carried from particle fade color, so near-death spray leaves faint marks naturally.
        internal static void SpawnSprayDot(Vector3 pos, Vector3 normal, Color col)
        {
            Material mat = GetBloodMat(col); // Alloy + soft circle (same as BuildDotMesh)
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.008f, 0.036f);
            Vector3    qup = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            Quaternion q   = Quaternion.LookRotation(-normal, qup)
                           * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            Vector3 qr = q * Vector3.right * r;
            Vector3 qu = q * Vector3.up    * r;
            Vector3 bp = pos + normal * 0.003f;

            var mesh = new Mesh();
            mesh.vertices  = new Vector3[] { bp-qr-qu, bp+qr-qu, bp+qr+qu, bp-qr+qu };
            mesh.uv        = new Vector2[] { new Vector2(0,0), new Vector2(1,0),
                                             new Vector2(1,1), new Vector2(0,1) };
            mesh.colors    = new Color[]   { col, col, col, col };
            mesh.triangles = new int[]     { 0,2,3, 0,1,2 };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            var go = new GameObject("SD");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            TrackGO(go, null); // drip queue — no shot context
            _fadingStains.Add(new FadingStainState { M = mesh, BaseCol = col, SpawnTime = Time.time });
        }

        // ── Mesh building ─────────────────────────────────────────────────────────

        static void BuildDotMesh(List<DotData> dots, Transform parent, Color col, List<GameObject> shotList = null)
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
                    float shadeMult = Mathf.Lerp(0.1f, 1.0f, tanShade);
                    float totalMult = darkMult * shadeMult;
                    // Alpha fades over last 30m before max range.
                    // Alloy blend = SrcAlpha/OneMinusSrcAlpha so no premul needed.
                    float fadeStart = BloodSystemPlugin.CfgRange.Value - 30f;
                    float alpha = (fadeStart > 0f && d.Dist > fadeStart)
                        ? 1f - Mathf.Clamp01((d.Dist - fadeStart) / 30f)
                        : 1f;
                    Color vc = new Color(col.r * totalMult, col.g * totalMult, col.b * totalMult, alpha);

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
                mesh.RecalculateTangents();

                var go = new GameObject("BD");
                if (parent != null) go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().mesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.material          = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows    = false;
                UnityEngine.Object.Destroy(go, CfgLifetime.Value);
                TrackGO(go, shotList);

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


    // Per-bullet inter-frame state written by PreMove, read by Damage postfix.
    public class SplatterTracker : MonoBehaviour
    {
        public Vector3 LastBulletDir;
        public float   LastBulletSpeed = 400f;
        // Pending exit-blood: set by OnSosigLinkDamage, consumed by PostMove after velocity check.
        public bool    PendingBlood;
        public Vector3 PendingExitPt;
        public Vector3 PendingStrikeDir;
        public Sosig   PendingSrc;
        public Color   PendingCol;
        public Vector3 PendingEntryPt;
    }

    // Attached to a ParticleSystem. Detects particles near any static surface and stamps stains.
    // Works on vanilla BleedingEvent PSes (SetSosig) and on the shared spray PSes (SetUseParticleColor).
    public class VanillaDripStainer : MonoBehaviour
    {
        ParticleSystem            _ps;
        ParticleSystem.Particle[] _buf;
        Sosig                     _sosig;
        int                       _skip;
        bool                      _useParticleColor;
        bool                      _sprayMode;
        int                       _maxRaycast = 30;

        public void SetSosig(Sosig s)          { _sosig = s; }
        public void SetUseParticleColor()      { _useParticleColor = true; }
        public void SetSprayMode()             { _sprayMode = true; }
        public void SetMaxRaycast(int n)       { _maxRaycast = n; }

        void Start()
        {
            _ps = GetComponent<ParticleSystem>();
            if (_sosig == null) _sosig = GetComponentInParent<Sosig>();
            if (_ps != null)
                _buf = new ParticleSystem.Particle[Mathf.Max(_ps.main.maxParticles, 64)];
        }

        void Update()
        {
            if (_ps == null || _buf == null) return;
            if (++_skip < 3) return; _skip = 0;

            int n = _ps.GetParticles(_buf);
            if (n == 0) return;
            int checkN = Mathf.Min(n, _maxRaycast);

            bool local = _ps.main.simulationSpace == ParticleSystemSimulationSpace.Local;
            bool dirty = false;

            for (int i = 0; i < checkN; i++)
            {
                Vector3 pos = local
                    ? _ps.transform.TransformPoint(_buf[i].position)
                    : (Vector3)_buf[i].position;
                Vector3 worldVel = local
                    ? _ps.transform.TransformDirection(_buf[i].velocity)
                    : (Vector3)_buf[i].velocity;

                // Tiny one-time horizontal spread at birth — only applies when particle is brand-new (>97% life).
                if (!_useParticleColor && _buf[i].remainingLifetime >= _buf[i].startLifetime * 0.97f)
                {
                    float angle   = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    Vector3 horiz = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle))
                                  * UnityEngine.Random.Range(0.1f, 0.35f);
                    _buf[i].velocity = (Vector3)_buf[i].velocity + horiz;
                    dirty = true;
                }

                RaycastHit h = default(RaycastHit);
                bool found = false;

                // 1. Velocity direction — catches walls, ramps, surfaces the particle is moving toward
                float vMag = worldVel.magnitude;
                if (vMag > 0.1f && Physics.Raycast(pos, worldVel / vMag, out h, 0.3f))
                    found = true;

                // 2. Downward proximity — floors the particle is hovering over
                if (!found && Physics.Raycast(pos + Vector3.up * 0.02f, Vector3.down, out h, 0.03f))
                    found = true;

                // 3. Expiry cast — only for BleedingEvent PSes (not spray).
                // Particle almost dead → stamp wherever it actually is, including walls.
                if (!found && !_useParticleColor && _buf[i].remainingLifetime < _buf[i].startLifetime * 0.08f)
                {
                    // Try velocity direction first (catches walls/ramps blood is flying toward)
                    if (vMag > 0.1f) Physics.Raycast(pos, worldVel / vMag, out h, 0.8f);
                    // Downward fallback for floor
                    if (ReferenceEquals(h.collider, null))
                        Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, out h, 1.5f);
                }

                if (ReferenceEquals(h.collider, null)) continue;

                if (h.collider.attachedRigidbody != null) continue;
                if (h.collider.GetComponentInParent<SosigLink>() != null) continue;
                if (h.collider.GetComponentInParent<Canvas>() != null) continue;
                if (h.collider.gameObject.layer == 5) continue;

                Color col = _useParticleColor
                    ? (Color)_buf[i].GetCurrentColor(_ps)
                    : BloodSystemPlugin.GetSosigBloodColor(_sosig);

                if (_sprayMode)
                {
                    float roll = UnityEngine.Random.value;
                    if (roll < 0.80f)
                        BloodSystemPlugin.SpawnSprayDot(h.point, h.normal, col);
                    else if (roll >= 0.95f)
                        BloodSystemPlugin.SpawnDripStainStreak(h.point, worldVel, h.normal, col, null, true);
                    // 0.80–0.95 (15%): nothing spawned
                }
                else
                {
                    BloodSystemPlugin.SpawnDripStainStreak(h.point, worldVel, h.normal, col);
                }
                _buf[i].remainingLifetime = 0f;
                dirty = true;
            }
            if (dirty) _ps.SetParticles(_buf, n);
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
        static readonly FieldInfo FBleedingEvents =
            typeof(Sosig).GetField("m_bleedingEvents",
                BindingFlags.NonPublic | BindingFlags.Instance);

        internal static bool Ok => !ReferenceEquals(FLastColliderHit, null)
                                && !ReferenceEquals(FVelocity,        null);

        // Maps each SosigLink to the last bullet strikeDir that hit it.
        // Written by OnSosigLinkDamage, read by OnLinkExplodes for gib direction.
        static readonly System.Collections.Generic.Dictionary<SosigLink, Vector3> _strikeDir =
            new System.Collections.Generic.Dictionary<SosigLink, Vector3>();
        static readonly System.Collections.Generic.Dictionary<SosigLink, float> _strikeSpeed =
            new System.Collections.Generic.Dictionary<SosigLink, float>();

        // Set in PreMove, valid until PostMove clears it. Unity is single-threaded so no races.
        // Lets OnSosigLinkDamage (fires inside MoveBullet) write to the current bullet's tracker.
        static SplatterTracker _activeBulletTracker;

        // ── Sosig.Start: attach drip polling components ───────────────────────────

        static bool _sosigStartLoggedOnce;

        [HarmonyPatch(typeof(Sosig), "Start")]
        [HarmonyPostfix]
        static void OnSosigStart(Sosig __instance)
        {
            try
            {
                // BleedingEvent PSes don't exist at spawn — they're created dynamically by BleedingUpdate.
                // VanillaDripStainer is attached there instead. Only do Alloy grab here.
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

        // ── Sosig.BleedingUpdate: fires every frame — attaches VanillaDripStainer to live bleed PSes ──

        [HarmonyPatch(typeof(Sosig), "BleedingUpdate")]
        [HarmonyPostfix]
        static void OnBleedingUpdate(Sosig __instance)
        {
            if (ReferenceEquals(FBleedingEvents, null)) return;
            try
            {
                var events = FBleedingEvents.GetValue(__instance) as System.Collections.Generic.List<Sosig.BleedingEvent>;
                if (events == null || events.Count == 0) return;
                for (int i = 0; i < events.Count; i++)
                {
                    var ev = events[i];
                    if (ev == null || ev.m_system == null) continue;
                    if (ev.m_system.GetComponent<VanillaDripStainer>() != null) continue;
                    var stainer = ev.m_system.gameObject.AddComponent<VanillaDripStainer>();
                    stainer.SetSosig(__instance);
                    stainer.SetMaxRaycast(30);
                }
            }
            catch { }
        }

        // ── Bullet pre-move: snapshot state BEFORE this step's movement/collision ─

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPrefix]
        static void PreMove(BallisticProjectile __instance)
        {
            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker == null) tracker = __instance.gameObject.AddComponent<SplatterTracker>();
            _activeBulletTracker = tracker;

            var vel = (Vector3)FVelocity.GetValue(__instance);
            if (vel.magnitude > 0.01f)
            {
                tracker.LastBulletDir   = vel.normalized;
                tracker.LastBulletSpeed = vel.magnitude;
            }
        }

        // ── Bullet post-move ──────────────────────────────────────────────────────
        // OnSosigLinkDamage stores pending blood data. Here we confirm penetration by
        // checking that the bullet still has velocity (armor-stopped bullets have ~0 speed).

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPostfix]
        static void PostMove(BallisticProjectile __instance)
        {
            _activeBulletTracker = null; // clear — no longer inside this bullet's MoveBullet

            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker != null && tracker.PendingBlood)
            {
                tracker.PendingBlood = false;
                // Velocity after MoveBullet: near-zero = bullet was stopped (armor/low KE), skip blood.
                // Non-zero = bullet continued = penetration confirmed.
                var vel = (Vector3)FVelocity.GetValue(__instance);
                if (vel.magnitude > 0.5f)
                {
                    if (!_bloodFiredOnce)
                    {
                        _bloodFiredOnce = true;
                        BloodSystemPlugin.Log.LogInfo("[BloodSystem] First blood confirmed"
                            + " vel=" + vel.magnitude.ToString("F1")
                            + " exitPt=" + tracker.PendingExitPt);
                    }
                    var shotList = BloodSystemPlugin.StartShotGroup();
                    BloodSystemPlugin.SpawnProjection(tracker.PendingExitPt,  tracker.PendingStrikeDir, tracker.PendingSrc, vel.magnitude, false, shotList);
                    BloodSystemPlugin.SpawnBloodSpray(tracker.PendingExitPt, tracker.PendingStrikeDir, tracker.PendingCol);
                    BloodSystemPlugin.SpawnBloodDrops(tracker.PendingExitPt,  tracker.PendingStrikeDir, tracker.PendingCol, 10, shotList);
                    BloodSystemPlugin.SpawnBloodDrops(tracker.PendingEntryPt, -tracker.PendingStrikeDir, tracker.PendingCol, 8,  shotList);
                }
            }

            var currentCollider = FLastColliderHit.GetValue(__instance) as Collider;
            if (ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)
                && !BloodSystemPlugin._alloyGrabPending
                && !ReferenceEquals(currentCollider, null) && currentCollider != null
                && currentCollider.attachedRigidbody == null
                && currentCollider.GetComponentInParent<SosigLink>() == null)
            {
                BloodSystemPlugin._instance.StartCoroutine(BloodSystemPlugin.TryGrabAlloyFromScene());
            }
        }

        // ── SosigLink.Damage: fires when a bullet damages a link ─────────────────
        // d.point = exact hit surface point, d.strikeDir = bullet direction, d.hitNormal = outward normal.
        // Entry drops fire at d.point. Exit blood fires at d.point + strikeDir*0.35f (past the body).
        // No penetration geometry check needed — game already called Damage because the bullet hit.

        static bool _dbgDamageClassLogged;

        [HarmonyPatch(typeof(SosigLink), "Damage")]
        [HarmonyPostfix]
        static void OnSosigLinkDamage(SosigLink __instance, Damage d)
        {
            try
            {
                if (!BloodSystemPlugin.CfgEnabled.Value) return;

                // _activeBulletTracker is null if this Damage call did not come from a bullet's MoveBullet.
                // (e.g. explosion, melee, environment). Skip those.
                if (ReferenceEquals(_activeBulletTracker, null)) return;

                if (!_dbgDamageClassLogged)
                {
                    _dbgDamageClassLogged = true;
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] SosigLink.Damage class="
                        + d.Class + " kinetic=" + d.Dam_TotalKinetic
                        + " strikeDir=" + d.strikeDir + " sourcePoint=" + d.Source_Point
                        + " point=" + d.point);
                }

                // Direction: d.strikeDir first; fall back to tracker direction or source→hit vector
                Vector3 sDir;
                if (d.strikeDir.sqrMagnitude > 0.001f)
                    sDir = d.strikeDir.normalized;
                else if (_activeBulletTracker.LastBulletDir.sqrMagnitude > 0.001f)
                    sDir = _activeBulletTracker.LastBulletDir;
                else if ((d.point - d.Source_Point).sqrMagnitude > 0.001f)
                    sDir = (d.point - d.Source_Point).normalized;
                else
                    sDir = Vector3.forward;

                _strikeDir[__instance]  = sDir;
                _strikeSpeed[__instance] = _activeBulletTracker.LastBulletSpeed;

                Sosig   src    = __instance.S;
                Color   col    = BloodSystemPlugin.GetSosigBloodColor(src);
                Vector3 exitPt = d.point + sDir * 0.35f;
                // Try to find actual sosig exit surface
                RaycastHit xh;
                if (Physics.Raycast(__instance.transform.position, sDir, out xh, 2f))
                {
                    SosigLink xlk = xh.collider.GetComponentInParent<SosigLink>();
                    if (xlk != null && ReferenceEquals(xlk.S, __instance.S))
                        exitPt = xh.point + sDir * 0.02f;
                }
                // Clip exitPt above any static floor between entry and exit.
                // Cast from ABOVE d.point so we're clear of the floor surface even if the
                // ragdoll is partially embedded. Uses RaycastAll to skip sosig bodies.
                {
                    Vector3 clipFrom = d.point + Vector3.up * 0.35f;
                    float   clipDist = (exitPt - clipFrom).magnitude + 0.1f;
                    var clipHits = Physics.RaycastAll(clipFrom, sDir, clipDist);
                    System.Array.Sort(clipHits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var ch in clipHits)
                    {
                        if (ch.collider.GetComponentInParent<SosigLink>() != null) continue;
                        if (ch.collider.attachedRigidbody != null) continue;
                        if (ch.normal.y < 0.5f) continue;
                        exitPt = ch.point + ch.normal * 0.05f;
                        break;
                    }
                }

                if (!BloodSystemPlugin._dbgDecalLogged)
                {
                    BloodSystemPlugin._dbgDecalLogged = true;
                    try
                    {
                        var sb = new System.Text.StringBuilder("[BloodSystem] DBG decal types: ");
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

                // Store for PostMove velocity check (armor → velocity ≈ 0 → no blood).
                var t = _activeBulletTracker;
                t.PendingBlood      = true;
                t.PendingExitPt     = exitPt;
                t.PendingStrikeDir  = sDir;
                t.PendingSrc        = src;
                t.PendingCol        = col;
                t.PendingEntryPt    = d.point;
            }
            catch (Exception ex)
            {
                BloodSystemPlugin.Log.LogWarning("[BloodSystem] OnSosigLinkDamage: " + ex.Message);
            }
        }

        // ── SosigLink.LinkExplodes: fires when a segment gibs ────────────────────
        // Replaces PrevHitLink gib detection — fires in the same frame as the destruction.

        [HarmonyPatch(typeof(SosigLink), "LinkExplodes")]
        [HarmonyPostfix]
        static void OnLinkExplodes(SosigLink __instance, Damage.DamageClass damClass)
        {
            try
            {
                if (!BloodSystemPlugin.CfgEnabled.Value) return;
                if (__instance == null) return;

                Vector3 pos = __instance.transform.position;
                Sosig   src = __instance.S;
                Color   col = BloodSystemPlugin.GetSosigBloodColor(src);

                Vector3 dir;
                if (!_strikeDir.TryGetValue(__instance, out dir) || dir.sqrMagnitude < 0.001f)
                    dir = Vector3.up;
                _strikeDir.Remove(__instance);

                float entrySpd;
                if (!_strikeSpeed.TryGetValue(__instance, out entrySpd)) entrySpd = 300f;
                _strikeSpeed.Remove(__instance);
                float spd = 400f; // projection ray spread speed — fixed for gibs
                // speedScale: at 300 m/s = 1.0×, at 600 m/s = 2.0×, clamped so it's subtle
                float speedScale = Mathf.Clamp(entrySpd / 300f, 0.5f, 2.5f);

                if (!_bloodFiredOnce)
                {
                    _bloodFiredOnce = true;
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] First blood via LinkExplodes gib pos=" + pos + " entrySpd=" + entrySpd.ToString("F0") + " scale=" + speedScale.ToString("F2"));
                }

                var shotList = BloodSystemPlugin.StartShotGroup();
                BloodSystemPlugin.SpawnProjection(pos, dir, src, spd, true, shotList);
                BloodSystemPlugin.SpawnBloodSpray(pos, dir, col, true, speedScale);
                BloodSystemPlugin.SpawnBloodDrops(pos, dir, col, 10, shotList);

                if (!ReferenceEquals(src, null))
                    BloodSystemPlugin._instance.StartCoroutine(
                        BloodSystemPlugin.TagGibsDeferred(pos, src));
            }
            catch (Exception ex)
            {
                BloodSystemPlugin.Log.LogWarning("[BloodSystem] OnLinkExplodes: " + ex.Message);
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
                    BloodSystemPlugin._dripMatCache.Clear();
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
