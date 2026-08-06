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
        public float   Dark;
        public float   Bright;     // brightness from noise.png at this dot's UV (0.88-1.0)
        public Vector3 TanNorm;
        public Vector3 ElongDir;   // bullet direction projected onto hit surface (world space)
        public float   Elongation; // stretch factor: 1=round, >1=elongated along ElongDir
        public float   Dist;       // hit distance — used for range-edge alpha fade
        public byte    CurveClass; // thermal cooling curve group, baked from source-PNG blood density
        public DotData(Vector3 pos, Vector3 norm, float r, float dark, float bright, Vector3 tanNorm,
                       Vector3 elongDir, float elongation, float dist, byte curveClass)
        {
            Pos = pos; Norm = norm; R = r; Dark = dark; Bright = bright; TanNorm = tanNorm;
            ElongDir = elongDir; Elongation = elongation; Dist = dist; CurveClass = curveClass;
        }
    }

    [BepInPlugin("h3vr.invent60.bloodsystem", "Blood System", "3.4.0")]
    // Soft dependency (no hard requirement, no compile-time reference to Aiyke's assembly) purely
    // to control load order: if Aiyke IS installed, BepInEx loads it before us, so its Harmony
    // patches already exist by the time our Awake runs TryOverrideAiykePenetration below.
    [BepInDependency("Aiyke.code_mod", BepInDependency.DependencyFlags.SoftDependency)]
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
        internal static ConfigEntry<string> CfgColorOverride;
        internal static ConfigEntry<string> CfgColorOverrideMode;
        // Ken (Human mod dev, cross-mod ask): the Human mod's puppeteer AI kills a Sosig via
        // SosigLink.LinkExplodes on every ordinary death (not just real gib bursts - see the
        // realGib check in OnLinkExplodes below), which used to trigger the full splash/spray/
        // gib burst there too, every time. Default OFF for Human-mod humans specifically -
        // loosely coupled via a name-based GetComponent("HumanMarker") check so this plugin has
        // no hard dependency on the Human mod's assembly and still works fine without it installed.
        internal static ConfigEntry<bool> CfgSplashOnHumans;

        // 2026-07-23 (Ken, Human mod dev: "make hands and arms bleed less... make them bleed a
        // ratio of if shot in head or body"). Same loose-coupling pattern as CfgSplashOnHumans
        // above - the Human mod's HumanLimbHitbox knows exactly which limb group was hit (this
        // plugin only ever sees a SosigLink, and vanilla's 4-link model has no separate arm link
        // at all - Human-mod arm hits and torso hits share the same Torso SosigLink, so this
        // plugin alone could never tell them apart). Set via reflection right before the Human
        // mod forwards a hit into SosigLink.Damage, consumed and reset to 1 the instant this
        // patch reads it so it only ever scales the ONE hit it was set for.
        public static float ExternalBloodScale = 1f;

        // Player-reported toggles (aiyke mod users, 2026-07-21): splash never appeared for them
        // (see GetBloodMat fallback fix below), and they asked to be able to turn off the
        // wound-scatter spray and the vanilla-particle staining independently of everything else.
        internal static ConfigEntry<bool> CfgSplatterEnabled;
        internal static ConfigEntry<bool> CfgSprayEnabled;
        internal static ConfigEntry<bool> CfgVanillaStainEnabled;
        internal static ConfigEntry<bool> CfgDripStainsEnabled;

        // Aiyke compat (2026-07-21, verified against Aiyke's own bundled source, not guessed):
        // "Aiyke code mod pack" (BepInEx GUID "Aiyke.code_mod") fully replaces
        // BallisticProjectile.MoveBullet for player bullets via its own [HarmonyPrefix] that
        // returns false, skipping vanilla MoveBullet entirely and reimplementing penetration
        // physics itself. Our splatter trigger in PostMove depends on the bullet's FINAL
        // position ending up past the hit surface (dot<0) - Aiyke's own math only does that on a
        // "clean penetration" branch; ricochets/absorbed hits land back on the near side or
        // exactly on the surface, so dot never goes negative and SpawnProjection/SpawnBloodSpray
        // never get called at all for most hits. Two ways to cope, user's choice:
        // Aiyke compat is always "Override": on startup this mod removes Aiyke's own
        // penetration-physics/damage-multiplier patches from MoveBullet so this mod's normal
        // precise penetration detection runs. The old "Approximate" compat mode (fire blood
        // straight off Damage data instead) was dropped - always broken in practice, nobody
        // used it.

        internal static readonly Color _mustardFallback = new Color(0.9f, 0.8f, 0f, 1f);

        // Decal material cache — uses _decalTex (soft circle).
        // Keyed by colour AND thermal cohort: blood spawned at different times is at different
        // temperatures, and temperature lives on the material (see BloodThermal). With thermal
        // disabled every cohort id is 0, so this behaves exactly as the old per-colour cache.
        internal static readonly Dictionary<MatKey, Material> _matCache     = new Dictionary<MatKey, Material>();
        // Drip stain material cache: same as _matCache but texture = _hardCircleTex
        internal static readonly Dictionary<MatKey, Material> _dripMatCache = new Dictionary<MatKey, Material>();

        // Shot group tracking: all GOs from one shot grouped by ID.
        // Evict the oldest shot group when CfgMaxShots is exceeded.
        static int _nextShotId;
        static readonly Dictionary<int, List<GameObject>> _shotGroups = new Dictionary<int, List<GameObject>>();
        static readonly Queue<int>        _shotQueue = new Queue<int>();
        // Drip queue: per-particle stains from VanillaDripStainer (no shot context), evicted individually.
        static readonly Queue<GameObject> _dripQueue = new Queue<GameObject>();
        internal static ConfigEntry<int>  CfgMaxShots;
        internal static ConfigEntry<int>  CfgMaxDrips;

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
        static Texture2D   _firstBloodTex;
        static Texture2D   _normalMapTex;  // normal noise.jpg — "normal"/"norm" in filename
        static Texture2D   _noiseMapTex;   // greyscale noise.png — "noise" in filename, used for brightness
        // Per-color pre-baked soft circles — fallback when shader has no _Color tint property
        static readonly Dictionary<Color, Texture2D> _coloredTexCache = new Dictionary<Color, Texture2D>();


        // CDF data built from ALL blood PNGs combined (equal-contribution, aspect-correct)
        static Vector2[] _splatterUVs;
        static float[]   _cumWeights;
        static float[]   _splatterDarks;
        static float[]   _splatterBrights; // per-sample brightness from noise.png at same UV (0.88-1.0)
        static Vector3[] _splatterNormals;
        // Per-sample cooling curve class, baked from how densely packed with blood that part of the
        // source PNG is. 0 = densest. Only used by the thermal system.
        static byte[]    _splatterClasses;

        // Fixed tangent-space light for per-dot normal shading
        static readonly Vector3 _tanLight = new Vector3(0.5f, 0.5f, 0.707f).normalized;

        // Same 10 brightness levels used by BuildDotMesh — keeps material cache bounded (10 entries per color).
        internal static readonly float[] BRIGHT_LEVELS = { 0.700f, 0.733f, 0.767f, 0.800f, 0.833f, 0.867f, 0.900f, 0.933f, 0.967f, 1.000f };
        static readonly float[] _brightLevels = BRIGHT_LEVELS;
        static Color BrightTint(Color col)
        {
            float b = _brightLevels[UnityEngine.Random.Range(0, _brightLevels.Length)];
            return new Color(col.r * b, col.g * b, col.b * b, col.a);
        }

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

        // Shared non-alloc raycast buffer and comparer — Unity is single-threaded so sharing is safe.
        internal static readonly RaycastHit[] _rayBuf    = new RaycastHit[64];
        internal static readonly RhByDist     _rhCompare = new RhByDist();
        internal struct RhByDist : System.Collections.Generic.IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); }
        }

        // NGA SosigIntegrityConfigs color (one-time check)
        static bool  _ngaChecked;
        static bool  _ngaKetchup;
        static Color _ngaColor;

        void Awake()
        {
            Log       = Logger;
            _instance = this;

            CfgEnabled   = Config.Bind("Blood", "Enabled",          true,   "Toggle all blood effects.");
            CfgLifetime  = Config.Bind("Blood", "Lifetime seconds",  60f,   "How long splash and drip stains last before despawning. Long enough that blood is still around to look at on thermal well after it has finished cooling.");
            CfgRayCount  = Config.Bind("Blood", "Max rays per shot",  3000,   "Maximum splash ray count. Capped to the actual number of image pixels if fewer.");
            CfgConeAngle = Config.Bind("Blood", "Cone half-angle",   10f,    "Half-angle in degrees of the splash cone.");
            CfgDotSize   = Config.Bind("Blood", "Dot base radius",   0.008f, "Base radius of each splash dot in metres. Scales to Dot Max Scale at Dot Scale Range distance.");
            CfgRange          = Config.Bind("Blood", "Range metres",            40f,       "Maximum splash distance in metres.");
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
                "Dots near the wound start at Dot Base Radius and grow linearly to this range. Default 30.");
            CfgGibRayCount    = Config.Bind("Blood", "Gib Ray Count",           200,
                "Number of rays fired in random 360-degree directions when a segment explodes. " +
                "Lower values improve FPS in gib-heavy fights. Capped by image pixel count. Default 200.");
            CfgMaxShots = Config.Bind("Blood", "Max shot groups", 20,
                "Maximum number of shots whose splash and drip decals stay visible. When exceeded, the oldest shot's decals are all deleted together.");
            CfgMaxDrips = Config.Bind("Blood", "Max drip stains", 400,
                "Maximum drip stains from particle detection (VanillaDripStainer). Oldest deleted when exceeded.");
            CfgColorOverride = Config.Bind("Blood", "Color Override", "#8C1A1A",
                "Hex color (e.g. #8C1A1A) used for blood when Color Override Mode is set to Soft or Hard. Ignored when mode is Unset.");
            CfgSplashOnHumans = Config.Bind("Blood", "Splash On Human-mod Humans", false,
                "Whether the LinkExplodes splash/spray/gib burst plays on Sosigs controlled by the Human mod (invent60's puppeteer AI). Default off - that mod calls LinkExplodes on every ordinary kill, not just real grenade/gib bursts.");
            CfgColorOverrideMode = Config.Bind("Blood", "Color Override Mode", "0",
                "Type 1 for Soft Override: replaces normal blood color, but sosigs with a color specifically set via NGA SosigIntegrityConfigs (e.g. zombies) keep their own color. " +
                "Type 2 for Hard Override: replaces blood color for every sosig, no exceptions. " +
                "Any other value = Unset: no override, vanilla per-sosig color behavior.");
            CfgSplatterEnabled = Config.Bind("Blood", "Splatter Enabled", true,
                "Projected blood splash dots on walls/floor/props from the bullet wound. The main splatter effect.");
            CfgSprayEnabled = Config.Bind("Blood", "Spray Enabled", true,
                "Blood particles that scatter outward from the wound in a wide cone/sphere, not just along the bullet path. Off by default - many players found this looked like an unintended splatter-sphere.");
            CfgVanillaStainEnabled = Config.Bind("Blood", "Vanilla Particle Staining Enabled", true,
                "Whether vanilla sosig bleed-out particles get intercepted and made to leave a stain when they land. When off, vanilla bleed particles behave as in unmodded H3VR (fall/bounce, no stain).");
            CfgDripStainsEnabled = Config.Bind("Blood", "Blood Drip Stains Enabled", true,
                "Our own dripping-wound blood drops that fall from the wound over time and stain the floor.");

            BloodThermal.BindConfig(Config);
            // Must run before the CDF is built — BuildSampleDataFromAll asks it how many cooling
            // curve classes to split the splatter samples into.
            BloodThermal.Init();

            // Disarm on every scene load. Thermal work only restarts once a thermal optic is
            // actually looked through in the new scene, so a level with no thermal camera in it
            // costs nothing at all.
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                BloodThermal.OnSceneChanged();
            };

            // Soft-circle for splash dots, hard-circle for drip stains
            _decalTex      = MakeSoftCircle(96);
            _hardCircleTex = MakeHardCircle(64);
            // RGB-encoded copies so thermal can read the shape (see MakeHeatMask).
            BloodThermal.RegisterHeatMask(_decalTex,      MakeHeatMask(_decalTex));
            BloodThermal.RegisterHeatMask(_hardCircleTex, MakeHeatMask(_hardCircleTex));

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
                _dotQuadMesh.normals   = new[] { Vector3.forward, Vector3.forward,
                                                  Vector3.forward, Vector3.forward };
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
            BloodThermal.MarkAlwaysHot(_fogMat);
            BloodThermal.MarkAlwaysHot(_pelletMat);
            // Give flying-dot PS the same material as spray pellets (blood texture, alpha-blend)
            if (!ReferenceEquals(_flyingDotPS, null) && !ReferenceEquals(_pelletMat, null))
                _flyingDotPS.GetComponent<ParticleSystemRenderer>().material = _pelletMat;
            BuildSprayPSes();

            // Every annotated patch class needs its OWN PatchAll call. Harmony's PatchAll(Type)
            // processes exactly the type it is handed and does NOT descend into nested types, so
            // WfxDecalMaterialGrab and OnslaughtNaturalDeathPatch — both nested inside
            // BloodSystemPatches — had never been patched at all since the day they were written.
            // Proven from a real log: ThermalArmHook.Prepare threw on its Type null-compare and was
            // reported by Harmony, while WfxDecalMaterialGrab.Prepare (identical bad compare, same
            // load) threw nothing, because it was never invoked.
            //
            // Consequence while they were dead: the WFX bullet-hole decal material was never
            // grabbed (blood fell back to the cached Alloy material or the scene scan), and the
            // Onslaught natural-death + concurrent-modification crash fix never ran.
            var harmony = new Harmony("h3vr.invent60.bloodsystem");
            harmony.PatchAll(typeof(BloodSystemPatches));
            harmony.PatchAll(typeof(ThermalArmHook));
            harmony.PatchAll(typeof(BloodSystemPatches.WfxDecalMaterialGrab));
            harmony.PatchAll(typeof(BloodSystemPatches.OnslaughtNaturalDeathPatch));
            Log.LogInfo("[BloodSystem] 3.4.0 loaded. FieldsOK=" + BloodSystemPatches.Ok);

            bool aiykePresent = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("Aiyke.code_mod");
            if (aiykePresent)
            {
                Log.LogInfo("[BloodSystem] Aiyke code mod pack detected - overriding its MoveBullet penetration patches so this mod's own splatter detection works.");
                TryOverrideAiykePenetration();
            }
        }

        // The only per-frame work this plugin does. BloodThermal.Tick is a single float compare
        // on almost every frame — it only touches anything when a precomputed cooling step is due.
        void Update()
        {
            BloodThermal.Tick();
        }

        // "Override" compat mode: surgically removes only Aiyke's own patches on
        // BallisticProjectile.MoveBullet (its penetration rework + damage-output-multiplier
        // prefixes), identified by declaring-type full name so this works without a compile-time
        // reference to Aiyke's assembly. Leaves every other Aiyke patch (aim assist, red blood,
        // alertness, hit sounds, etc - all on different methods) untouched.
        static void TryOverrideAiykePenetration()
        {
            try
            {
                var mb = AccessTools.Method(typeof(BallisticProjectile), "MoveBullet", new[] { typeof(float) });
                if (ReferenceEquals(mb, null)) return;
                var info = Harmony.GetPatchInfo(mb);
                if (ReferenceEquals(info, null) || ReferenceEquals(info.Prefixes, null)) return;

                var harmony = new Harmony("h3vr.invent60.bloodsystem");
                int removed = 0;
                foreach (var p in info.Prefixes)
                {
                    // H3VR's Mono is missing MethodInfo/Type.op_Equality - a plain "!= null" or
                    // "== null" on reflection members throws MissingMethodException here (crashes
                    // Awake entirely, silently skipping the unpatch). ReferenceEquals avoids the
                    // operator call. See feedback-no-type-equality memory.
                    if (!ReferenceEquals(p.PatchMethod, null) && !ReferenceEquals(p.PatchMethod.DeclaringType, null)
                        && p.PatchMethod.DeclaringType.FullName == "plugin.code_mod")
                    {
                        harmony.Unpatch(mb, p.PatchMethod);
                        removed++;
                    }
                }
                Log.LogInfo("[BloodSystem] Aiyke Compat Override: removed " + removed
                    + " Aiyke prefix patch(es) from BallisticProjectile.MoveBullet.");
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] TryOverrideAiykePenetration: " + ex.Message); }
        }

        // ── Drip stain material cache (hard circle, cached per color) ─────────────

        // Drips and spray are thin, exposed blood — always the thinnest cooling class.
        internal static Material GetDripMat(Color col)
        {
            return GetDripMat(col, BloodThermal.CurrentCohort(BloodThermal.Classes - 1));
        }

        internal static Material GetDripMat(Color col, int cohortId)
        {
            var key = new MatKey(col, cohortId);
            Material m;
            if (_dripMatCache.TryGetValue(key, out m) && !ReferenceEquals(m, null)) return m;
            Material src = GetBloodMat(col, cohortId);
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
            if (!ReferenceEquals(_hardCircleTex, null)) m.mainTexture = _hardCircleTex;
            else if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;
            // cloned source material keeps its own albedo; we only override _MainTex.
            // NOTE: writing our texture into Alloy's _ColorRGBOpacityA slot broke rendering.
            // (albedo override removed - see SetAllAlbedo)
            // Set explicitly rather than relying on new Material(src) to carry override tags.
            m.SetOverrideTag("RenderType", "Transparent"); // see ApplyBloodProps
            _dripMatCache[key] = m;
            BloodThermal.RegisterMaterial(m, key, true);
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
                    string fname = Path.GetFileNameWithoutExtension(f).ToLower();
                    // r2modman/Thunderstore install icon.png directly next to the DLL, in this
                    // same folder - without this it gets swept up and used as a blood splatter
                    // shape texture on every install, not just a local dev-profile quirk.
                    if (fname == "icon") continue;

                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!t.LoadImage(File.ReadAllBytes(f))) continue;
                    t.filterMode = FilterMode.Trilinear;
                    if (fname.Contains("normal") || fname.Contains("norm"))
                    {
                        _normalMapTex = t;
                        Log.LogInfo("[BloodSystem] NormalMap=" + Path.GetFileName(f));
                    }
                    else if (fname.Contains("noise"))
                    {
                        _noiseMapTex = NormalizeLumTex(t);
                        Log.LogInfo("[BloodSystem] NoiseTex=" + Path.GetFileName(f));
                    }
                    else
                    {
                        result.Add(NormalizeLumTex(t, true));
                    }
                }
            }
            catch (Exception ex) { BloodSystemPlugin.Log.LogWarning("[BloodSystem] LoadAllPngs: " + ex.Message); }
            return result;
        }

        // Stretch luminance dynamic range so darkest pixel = 0, brightest = 1.
        // keepAlpha=true: skip transparent pixels when finding min/max and preserve alpha (for blood shape textures).
        // keepAlpha=false: treat all pixels equally and set alpha=1 (for greyscale noise/maps).
        static Texture2D NormalizeLumTex(Texture2D src, bool keepAlpha = false)
        {
            Color[] pix = src.GetPixels();
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var c in pix)
            {
                if (keepAlpha && c.a < 0.02f) continue;
                float g = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                if (g < mn) mn = g;
                if (g > mx) mx = g;
            }
            float range = mx - mn;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            // range ~0 means every opaque pixel has identical brightness — a flat-color splat
            // mask (shape carried entirely in alpha), not a broken image. (g-mn)/range would
            // divide by ~0, so force pure white instead of falling back to the untouched
            // (still-colored) source, which silently defeated tinting for exactly this case.
            bool flat = range < 0.001f;
            for (int i = 0; i < pix.Length; i++)
            {
                float ng;
                if (flat)
                {
                    ng = 1f;
                }
                else
                {
                    float g = pix[i].r * 0.299f + pix[i].g * 0.587f + pix[i].b * 0.114f;
                    ng = (g - mn) / range;
                }
                pix[i] = new Color(ng, ng, ng, keepAlpha ? pix[i].a : 1f);
            }
            tex.SetPixels(pix);
            tex.Apply();
            tex.filterMode = FilterMode.Trilinear;
            return tex;
        }

        // Sample greyscale noise.png at [-1,1] UV → brightness in [0.88, 1.0].
        static float LookupNoiseBright(Vector2 uv)
        {
            if (ReferenceEquals(_noiseMapTex, null)) return 1f;
            float u  = uv.x * 0.5f + 0.5f;
            float v  = uv.y * 0.5f + 0.5f;
            int   px = Mathf.Clamp(Mathf.RoundToInt(u * (_noiseMapTex.width  - 1)), 0, _noiseMapTex.width  - 1);
            int   py = Mathf.Clamp(Mathf.RoundToInt(v * (_noiseMapTex.height - 1)), 0, _noiseMapTex.height - 1);
            float g  = _noiseMapTex.GetPixel(px, py).grayscale;
            return Mathf.Lerp(0.3f, 1.0f, g);
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
        // Thermal reads _ThermalHeatMap as RGB, but MakeSoftCircle/MakeHardCircle put the circle
        // shape entirely in ALPHA and leave RGB pure white. Handing those straight to the thermal
        // shader gives uniform heat over the whole quad — a square. This bakes alpha into RGB so
        // the heat map actually carries the shape. (The blood PNGs used by spray particles already
        // have real RGB content, which is why those came out round without this.)
        static Texture2D MakeHeatMask(Texture2D src)
        {
            if (ReferenceEquals(src, null)) return null;
            int w = src.width, h = src.height;
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var srcPix = src.GetPixels();
            var dstPix = new Color[srcPix.Length];
            for (int i = 0; i < srcPix.Length; i++)
            {
                float a = srcPix[i].a;
                dstPix[i] = new Color(a, a, a, 1f);
            }
            outTex.SetPixels(dstPix);
            outTex.Apply();
            return outTex;
        }

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
                float a  = d2 > 1f ? 0f : Mathf.Clamp01(Mathf.Exp(-d2 * 15f));
                pix[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        // Sharper soft circle for drip stains: same gaussian as MakeSoftCircle but steeper falloff.
        static Texture2D MakeHardCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            var pix = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float d2 = dx*dx + dy*dy;
                float d  = Mathf.Sqrt(d2);
                float t  = Mathf.Clamp01((d - 0.6f) / 0.4f);
                float a  = d > 1f ? 0f : 1f - (t * t * (3f - 2f * t));
                pix[y*size+x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }

        // Hemisphere normal map: encodes outward-pointing surface normals of a sphere as RGB.
        // Center texel = (0,0,1) pointing straight out; edge texels curve back toward surface.
        // Set as _BumpMap so the Alloy shader does real per-pixel lighting on each dot.
        static Texture2D MakeHemisphereNormalMap(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) * 0.5f;
            var pix = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c;
                float dy = (y - c) / c;
                float r2 = dx*dx + dy*dy;
                Vector3 N = r2 > 1f
                    ? Vector3.forward
                    : new Vector3(dx, dy, Mathf.Sqrt(1f - r2)).normalized;
                // Tangent-space normal map encoding: R=X, G=Y, B=Z, all in [0,1]
                pix[y*size+x] = new Color(N.x*0.5f+0.5f, N.y*0.5f+0.5f, N.z*0.5f+0.5f, 1f);
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
                float a = d2 > 1f ? 0f : Mathf.Clamp01(Mathf.Exp(-d2 * 8f));
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
            var uvList     = new List<Vector2>();
            var darkList   = new List<float>();
            var brightList = new List<float>();
            var normList   = new List<Vector3>();
            var wList      = new List<float>();
            var densList   = new List<float>();
            float cumul  = 0f;

            foreach (var tex in textures)
            {
                int w = tex.width, h = tex.height;
                Color[] pixels = tex.GetPixels();
                float ar = (float)w / h;

                // How densely packed with blood each pixel's neighbourhood is. Thick pooled areas
                // hold heat far longer than a fine mist of separated droplets, so this is what
                // decides which cooling curve a ray ends up on. Baked here, once, at startup —
                // the CDF is static, so there is nothing left to work out at projection time.
                float[] density = BuildDensityMap(pixels, w, h);

                var imgUVs   = new List<Vector2>(w * h / 4);
                var imgDarks = new List<float>(w * h / 4);
                var imgWts   = new List<float>(w * h / 4);
                var imgDens  = new List<float>(w * h / 4);
                float imgTotal = 0f;

                for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                {
                    Color c   = pixels[py * w + px];
                    float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
                    // Alpha alone drives selection weight — post-normalize textures are white
                    // (lum=1) wherever they have ink, so a (1-lum) factor here would zero out
                    // every pixel and silently drop the whole texture from the splatter CDF.
                    float wt  = c.a;
                    if (wt < 0.02f) continue;

                    float u, v;
                    if (ar >= 1f)
                    {
                        u = ((float)px / (w - 1)) * 2f - 1f;
                        v = (((float)py / (h - 1)) * 2f - 1f) / ar;
                    }
                    else
                    {
                        u = (((float)px / (w - 1)) * 2f - 1f) * ar;
                        v = ((float)py / (h - 1)) * 2f - 1f;
                    }

                    imgUVs.Add(new Vector2(u, v));
                    imgDarks.Add(1f - lum);
                    imgWts.Add(wt);
                    imgDens.Add(density[py * w + px]);
                    imgTotal += wt;
                }

                if (imgTotal < 0.001f || imgUVs.Count == 0) continue;

                LogDensityStats(tex.name, imgDens);

                // Normalized mode (default): rescale this image against its own range so its
                // densest areas always become the slowest-cooling class and its scattered edges
                // the fastest. Density is box-blurred ALPHA coverage, and alpha is an artistic
                // choice - the same splatter drawn at 60% opacity reads as thinner everywhere
                // while being structurally identical - so an absolute reading is not portable
                // between images. Since the blood PNGs are meant to be swapped by dropping new
                // files in the plugin folder, anything else would need reconfiguring per image.
                //
                // The ends come from percentiles, not the outright min and max, because those are
                // single pixels: one freak-dense pixel would stretch the top of the range on its
                // own and squash everything else into the thin classes.
                if (BloodThermal.NormalizeDensity)
                {
                    var dSorted = imgDens.ToArray();
                    System.Array.Sort(dSorted);
                    float dLo = dSorted[Mathf.Clamp((int)(dSorted.Length * 0.02f), 0, dSorted.Length - 1)];
                    float dHi = dSorted[Mathf.Clamp((int)(dSorted.Length * 0.98f), 0, dSorted.Length - 1)];

                    // Purely relative, with no absolute floor under the top of the range. Clamping
                    // it to a fixed coverage figure would be measuring against the original image
                    // again, which is exactly what normalizing is meant to avoid - the two rules
                    // would contradict each other and the fixed one would quietly win for any
                    // image thinner than the threshold.
                    //
                    // Thin blood reaching the slowest-cooling group was a measurement fault, not a
                    // scaling one: the blur window used to be narrower than a droplet, so a lone
                    // droplet measured as solid. With the window sized properly a spray image
                    // measures thin throughout, and spreading it across the classes is correct -
                    // its heaviest parts really are the parts that hold heat longest.
                    float dRange = dHi - dLo;
                    for (int i = 0; i < imgDens.Count; i++)
                        imgDens[i] = dRange > 1e-5f ? Mathf.Clamp01((imgDens[i] - dLo) / dRange) : 0f;
                }

                float norm = 1f / imgTotal;
                for (int i = 0; i < imgUVs.Count; i++)
                {
                    cumul += imgWts[i] * norm;
                    uvList.Add(imgUVs[i]);
                    darkList.Add(imgDarks[i]);
                    brightList.Add(LookupNoiseBright(imgUVs[i]));
                    normList.Add(LookupNormal(imgUVs[i]));
                    densList.Add(imgDens[i]);
                    wList.Add(cumul);
                }
            }

            _splatterUVs     = uvList.ToArray();
            _splatterDarks   = darkList.ToArray();
            _splatterBrights = brightList.ToArray();
            _splatterNormals = normList.ToArray();
            _cumWeights      = wList.ToArray();
            _splatterClasses = BuildCurveClasses(densList);
            LogClassDistribution(densList);
        }

        // Raw, absolute density distribution for one source image — the numbers to choose
        // Density Class Min / Max from. Density is box-blurred alpha coverage, so 0.8 means the
        // neighbourhood is ~80% blood and 0.1 means scattered droplets.
        static void LogDensityStats(string name, List<float> dens)
        {
            try
            {
                if (dens.Count == 0) return;
                var s = dens.ToArray();
                System.Array.Sort(s);
                int n = s.Length;
                Log.LogInfo("[BloodSystem] Density '" + name + "' n=" + n
                    + " min=" + s[0].ToString("F3")
                    + " p05=" + s[n * 5 / 100].ToString("F3")
                    + " p25=" + s[n * 25 / 100].ToString("F3")
                    + " p50=" + s[n / 2].ToString("F3")
                    + " p75=" + s[n * 75 / 100].ToString("F3")
                    + " p95=" + s[n * 95 / 100].ToString("F3")
                    + " max=" + s[n - 1].ToString("F3"));
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] LogDensityStats: " + ex.Message); }
        }

        // Checks the density classes actually mean what they are meant to mean.
        //
        // A sample's UV becomes a RAY DIRECTION: uv near (0,0) fires down the cone axis and lands
        // in the middle of the splash, large |uv| fires wide and lands out at the edge. Density is
        // measured from the source PNG at that same UV. So "the packed middle cools last, the
        // scattered outside cools first" only holds if the dense pixels really are the ones near
        // the middle of the artwork — which is a property of the PNGs, not of this code, and is
        // worth seeing rather than assuming. Mean radius rising with class index means it works.
        static void LogClassDistribution(List<float> densities)
        {
            try
            {
                int classes = BloodThermal.Classes;
                if (_splatterClasses == null || _splatterClasses.Length == 0) return;

                var count = new int[classes];
                var sumR  = new double[classes];
                var sumD  = new double[classes];
                for (int i = 0; i < _splatterClasses.Length; i++)
                {
                    int c = _splatterClasses[i];
                    if (c >= classes) c = classes - 1;
                    count[c]++;
                    sumR[c] += _splatterUVs[i].magnitude;
                    sumD[c] += densities[i];
                }

                var sb = new System.Text.StringBuilder();
                sb.Append("[BloodSystem] Density classes (0=densest, cools slowest):");
                for (int c = 0; c < classes; c++)
                {
                    sb.Append("\n  class ").Append(c).Append(": ").Append(count[c]).Append(" samples (")
                      .Append((100f * count[c] / _splatterClasses.Length).ToString("F1")).Append("%)");
                    if (count[c] > 0)
                        sb.Append("  meanDensity=").Append((sumD[c] / count[c]).ToString("F3"))
                          .Append("  meanRadius=").Append((sumR[c] / count[c]).ToString("F3"));
                }
                sb.Append("\n  meanRadius should RISE with class index if the packed middle is class 0.");
                Log.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Log.LogWarning("[BloodSystem] LogClassDistribution: " + ex.Message); }
        }

        // How many landing steps a shot gets, from how far its furthest dot travelled.
        //
        // Every step is a separate BuildDotMesh call emitting its own chunks, so this is the
        // single knob that decides what a shot costs. Anchored on the distances that matter and
        // interpolated between, rather than derived from flight time - flight time made the count
        // depend on bullet speed too, so a slow pistol quietly cost more than a rifle.
        //
        //   under 3m : 1  - the flight is over in milliseconds, animating it shows nothing
        //         3m : 1
        //        10m : 2
        //        40m : 5  - and capped there, however far the shot reaches
        static int AnimStepsForDistance(float dist)
        {
            if (dist <= 3f)  return 1;
            if (dist <= 10f) return Mathf.RoundToInt(Mathf.Lerp(1f, 2f, (dist -  3f) /  7f));
            if (dist <= 40f) return Mathf.RoundToInt(Mathf.Lerp(2f, 5f, (dist - 10f) / 30f));
            return 5;
        }

        // Separable box blur with a sliding window — each output pixel adds the value entering the
        // window and subtracts the one leaving, so the cost is the same whatever the radius. The
        // naive version reread the whole window per pixel, which made a large radius unaffordable
        // and is why the radius used to be a handful of pixels.
        static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            var tmp = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0f; int n = 0;
                int hi = Mathf.Min(w - 1, r);
                for (int i = 0; i <= hi; i++) { sum += src[row + i]; n++; }
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = sum / n;
                    int add = x + r + 1, rem = x - r;
                    if (add < w)  { sum += src[row + add]; n++; }
                    if (rem >= 0) { sum -= src[row + rem]; n--; }
                }
            }
            var dst = new float[w * h];
            for (int x = 0; x < w; x++)
            {
                float sum = 0f; int n = 0;
                int hi = Mathf.Min(h - 1, r);
                for (int i = 0; i <= hi; i++) { sum += tmp[i * w + x]; n++; }
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum / n;
                    int add = y + r + 1, rem = y - r;
                    if (add < h)  { sum += tmp[add * w + x]; n++; }
                    if (rem >= 0) { sum -= tmp[rem * w + x]; n--; }
                }
            }
            return dst;
        }

        // How much thermal mass sits around each pixel. Runs once per source PNG at startup.
        //
        // Opacity alone is not enough, and neither is average coverage. Blurred alpha gives the
        // same 0.30 for a fine spray of opaque droplets covering 30% of the area as it does for one
        // solid sheet drawn at 30% opacity, yet separate droplets should shed heat fast while a
        // continuous sheet holds it. What separates them is the VARIANCE inside the window:
        // scattered droplets swing between fully opaque and empty, a translucent sheet is flat.
        //
        // Variance alone flips the wrong way at high coverage though - 90% opaque with a few holes
        // has exactly the same raw variance as 10% scattered dots, and would be written off as
        // thin. So fragmentation is weighted by how much empty space there is (1 - mean): once
        // blood covers most of the area the droplets are touching anyway and it behaves as a mass.
        //
        //   scattered opaque droplets, 10% area   mean 0.10  frag 1.0  ->  0.01   cools fastest
        //   solid sheet at 30% opacity            mean 0.30  frag 0.0  ->  0.30   linear
        //   opaque with plenty around it, 90%     mean 0.90  frag 1.0  ->  0.81   most linear
        static float[] BuildDensityMap(Color[] pixels, int w, int h)
        {
            // The window has to be WIDER THAN A DROPLET, or an isolated droplet reads as a solid
            // mass: the window sits entirely inside it, sees nothing but opaque pixels, measures
            // zero variance and reports maximum density. That is how scattered spray was ending up
            // in the slowest-cooling group. A fixed 4px radius was a 9x9 window on a 1024-1200px
            // splatter image, where a droplet is comfortably 20-60px across - it could never see
            // the empty space around one. Scaling with the image keeps the measurement meaningful
            // whatever resolution someone drops in, and the sliding-window blur makes the larger
            // radius free.
            int r = Mathf.Max(BloodThermal.DensityBlur,
                              Mathf.RoundToInt(Mathf.Min(w, h) * BloodThermal.DensityBlurFraction));
            float fw = Mathf.Clamp01(BloodThermal.FragmentWeight);

            var a   = new float[w * h];
            var aSq = new float[w * h];
            for (int i = 0; i < a.Length; i++)
            {
                float v = pixels[i].a;
                a[i]   = v;
                aSq[i] = v * v;
            }

            float[] mean   = BoxBlur(a,   w, h, r);
            float[] meanSq = BoxBlur(aSq, w, h, r);

            var dst = new float[w * h];
            for (int i = 0; i < dst.Length; i++)
            {
                float m   = mean[i];
                float var = Mathf.Max(0f, meanSq[i] - m * m);
                // Largest variance possible at this coverage - fully binary opaque/empty.
                float maxVar = m * (1f - m);
                float frag = maxVar > 1e-5f ? Mathf.Clamp01(var / maxVar) : 0f;
                dst[i] = m * (1f - fw * frag * (1f - m));
            }
            return dst;
        }

        // Buckets samples into equal-width bands between two FIXED density thresholds, so a given
        // thickness always lands in the same class no matter which image it came from or what else
        // is installed. Density is absolute box-blurred alpha coverage; see LogDensityStats.
        // Class 0 = densest, cools slowest and most linearly; last class = thinnest, pure Newton.
        //
        // Anything at or above Density Class Max is class 0, anything at or below Min is the
        // thinnest class. A PNG that is entirely thin mist therefore uses only the thin classes,
        // which is the point - the earlier per-image rescale would have spread it across all of
        // them and pretended it had a dense core.
        static byte[] BuildCurveClasses(List<float> densities)
        {
            int n = densities.Count;
            var result = new byte[n];
            int classes = BloodThermal.Classes;
            if (classes <= 1 || n == 0) return result;

            // Each image has already been rescaled to 0-1 against its own range, so the band edges
            // are simply that range split evenly.
            const float lo = 0f, hi = 1f, range = hi - lo;

            var hist = new int[classes];
            for (int i = 0; i < n; i++)
            {
                float t = Mathf.Clamp01((densities[i] - lo) / range);
                // Densest is class 0, so invert before banding. The 0.9999 keeps t==1 inside the
                // top band instead of rounding past the last index.
                int cls = Mathf.Clamp((int)((1f - t) * classes * 0.9999f), 0, classes - 1);
                result[i] = (byte)cls;
                hist[cls]++;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[BloodSystem] Class bands over normalized density:");
            for (int c = 0; c < classes; c++)
            {
                float bandHi = hi - range * c / classes;
                float bandLo = hi - range * (c + 1) / classes;
                sb.Append("\n  class ").Append(c).Append(" density ")
                  .Append(bandLo.ToString("F2")).Append("-").Append(bandHi.ToString("F2"))
                  .Append(" -> ").Append(hist[c]).Append(" samples (")
                  .Append((100f * hist[c] / n).ToString("F1")).Append("%)");
            }
            Log.LogInfo(sb.ToString());
            return result;
        }

        static void BuildFallbackGrid(int side)
        {
            var uvList     = new List<Vector2>(side * side);
            var darkList   = new List<float>(side * side);
            var brightList = new List<float>(side * side);
            var normList   = new List<Vector3>(side * side);
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                var uv = new Vector2(((float)x / (side - 1)) * 2f - 1f,
                                     ((float)y / (side - 1)) * 2f - 1f);
                uvList.Add(uv);
                darkList.Add(0.8f);
                brightList.Add(LookupNoiseBright(uv));
                normList.Add(LookupNormal(uv));
            }
            _splatterUVs     = uvList.ToArray();
            _splatterDarks   = darkList.ToArray();
            _splatterBrights = brightList.ToArray();
            _splatterNormals = normList.ToArray();
            _cumWeights      = new float[0];
            // Uniform grid has no real density structure — put everything on one curve.
            _splatterClasses = new byte[uvList.Count];
        }

        static void SampleSplatter(out Vector2 uv, out float dark, out float bright, out Vector3 tanNorm,
                                   out byte curveClass)
        {
            if (_splatterUVs == null || _splatterUVs.Length == 0)
            {
                uv         = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f));
                dark       = 0.8f;
                bright     = 1f;
                tanNorm    = Vector3.forward;
                curveClass = 0;
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
            dark    = (!ReferenceEquals(_splatterDarks,   null) && idx < _splatterDarks.Length)   ? _splatterDarks[idx]   : 0.8f;
            bright  = (!ReferenceEquals(_splatterBrights, null) && idx < _splatterBrights.Length) ? _splatterBrights[idx] : 1f;
            tanNorm = (!ReferenceEquals(_splatterNormals, null) && idx < _splatterNormals.Length)  ? _splatterNormals[idx] : Vector3.forward;
            curveClass = (!ReferenceEquals(_splatterClasses, null) && idx < _splatterClasses.Length) ? _splatterClasses[idx] : (byte)0;
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
        // NOTE: previously also tried building a "hardcoded" Alloy/Core transparent material
        // here from a blank clone (new Material(Shader.Find("Alloy/Core")) + keywords). Removed:
        // per [[reference-alloy-shaders]], a blank Alloy/Core clone renders INVISIBLE in H3VR's
        // compiled shader variants no matter what keywords/blend state are set on it (only
        // variants actually in use by real game assets get compiled in) - it silently produced
        // an invisible _decalSourceMat AND pre-empted the one path that does work
        // (WfxDecalMaterialGrab cloning the real WFX_BulletHoleDecal material), making splatter
        // permanently invisible. GetBloodMat's own Legacy Shaders/Transparent/Diffuse fallback
        // (below) now covers visibility until a real WFX/scene grab succeeds.
        static void TryLoadFromBundle()  // name kept so Awake call doesn't change
        {
            // Cache file fallback (covers the case where the shader name changed between game versions)
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

                    // Decals are deliberately NOT excluded here. A bullet hole is Alloy/Core at
                    // renderQueue 3000, so it wins this scan - and that is wanted. Unity only
                    // compiles the shader variants real game assets actually use, so a variant
                    // taken from a live decal is guaranteed to render; one assembled from guessed
                    // keywords can come out invisible, which is what made splatter permanently
                    // invisible before 3.3.0. The decal is the most reliable source available.
                    //
                    // What it must NOT contribute is its albedo, a 2x2 atlas of bullet holes -
                    // that is where "every blood dot is four bullet holes" came from, and it is
                    // fixed at the source now by writing our texture into every albedo slot the
                    // shader has (SetAllAlbedo), rather than by refusing the material.
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
        // Visible fallback shader while _decalSourceMat hasn't been grabbed yet (before any
        // static-surface bullet hit triggers WfxDecalMaterialGrab). Per [[reference-alloy-shaders]]
        // this is a CONFIRMED-working non-Alloy path in H3VR's compiled shaders (correct alpha,
        // no PBR). Splash/spray/drip dots used to return null and draw nothing at all until Alloy
        // was grabbed - this is what made splatter appear "never" for players who hadn't yet shot
        // a wall (or, with Alloy still ungrabbed, forever).
        static Shader _fallbackDecalShader;
        static bool   _fallbackDecalShaderSearched;

        internal static Material GetBloodMat(Color col)
        {
            return GetBloodMat(col, BloodThermal.CurrentCohort(BloodThermal.Classes - 1));
        }

        internal static Material GetBloodMat(Color col, int cohortId)
        {
            var key = new MatKey(col, cohortId);
            Material m;
            if (_matCache.TryGetValue(key, out m) && !ReferenceEquals(m, null)) return m;

            Material src = _decalSourceMat;
            if (ReferenceEquals(src, null))
            {
                if (!_fallbackDecalShaderSearched)
                {
                    _fallbackDecalShaderSearched = true;
                    _fallbackDecalShader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                }
                if (ReferenceEquals(_fallbackDecalShader, null)) return null; // truly no visible shader available
                m = new Material(_fallbackDecalShader);
                if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;
                m.color = col;
                m.SetOverrideTag("RenderType", "Transparent"); // see ApplyBloodProps
                _matCache[key] = m;
                BloodThermal.RegisterMaterial(m, key, false);
                return m;
            }

            m = new Material(src);
            if (!ReferenceEquals(_decalTex, null)) m.mainTexture = _decalTex;
            ApplyBloodProps(m, col);
            _matCache[key] = m;
            BloodThermal.RegisterMaterial(m, key, false);
            return m;
        }



        // Blood = wet dielectric fluid. NOT metallic. NOT emissive.
        // Alloy property names from Josh015/Alloy source: _Metal, _Roughness, _Specularity, _EmissionColor.
        // NOT _Metallic, NOT _GlowColor, NOT _Emission, NOT _SpecColor, NOT _Shininess (those are Standard).
        // Alloy shaders do NOT read _MainTex for albedo - they read _ColorRGBOpacityA. Material
        // .mainTexture only ever writes _MainTex, so setting it left the SOURCE material's albedo
        // in place and the blood decal drew whatever that material happened to carry. When the
        // source was grabbed from a bullet-hole decal that albedo is a 2x2 atlas of bullet holes,
        // and every blood dot rendered as four holes in the corners of its quad.
        //
        // Writing our own texture into every albedo slot the shader actually has makes the decal
        // independent of whatever material it was cloned from, which is the real fix - the source
        // material is only wanted for its shader and blend state, never its textures.
        static readonly string[] ALBEDO_PROPS = { "_MainTex", "_ColorRGBOpacityA", "_BaseMap", "_BaseColorMap" };

        static void SetAllAlbedo(Material m, Texture tex)
        {
            if (ReferenceEquals(tex, null)) return;
            for (int i = 0; i < ALBEDO_PROPS.Length; i++)
                if (m.HasProperty(ALBEDO_PROPS[i])) m.SetTexture(ALBEDO_PROPS[i], tex);
        }

        static void ApplyBloodProps(Material m, Color col)
        {
            // (albedo override removed - see SetAllAlbedo)
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

            // Thermal is rendered with SetReplacementShader(shader, "RenderType"), so the tag is
            // what decides which SubShader draws blood. Our source is Alloy/Core, whose tag is
            // RenderType=Opaque - confirmed from THERMAL DIAG - so thermal drew every decal as a
            // solid quad with alpha ignored, i.e. a black square around each dot, no matter what
            // blend modes or heat map were set. Blend state is material-level and cannot change a
            // shader tag; SetOverrideTag can, and is the same mechanism Unity's own Standard
            // shader uses to switch itself to transparent.
            m.SetOverrideTag("RenderType", "Transparent");

            // Unity Standard / legacy fallback
            if (m.HasProperty("_Metallic"))       m.SetFloat("_Metallic",       0f);
            if (m.HasProperty("_Smoothness"))     m.SetFloat("_Smoothness",     0.1f);
            if (m.HasProperty("_SpecColor"))      m.SetColor("_SpecColor",      Color.black);
            if (m.HasProperty("_Shininess"))      m.SetFloat("_Shininess",      0.05f);

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
                mn.startSize       = new ParticleSystem.MinMaxCurve(0.010f, 0.030f);
                mn.startRotation   = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mn.maxParticles    = 2000;
                mn.gravityModifier = 0.05f; // outer fog barely falls — less dense, less gravity
                mn.loop            = false;
                mn.playOnAwake     = false;
                // World, not the Local default. These systems are repositioned between
                // emissions, and in Local space that drags every particle already in the
                // air along with the transform - the spray visibly jumped and flickered
                // instead of staying where it came out.
                mn.simulationSpace = ParticleSystemSimulationSpace.World;
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
                // World, not the Local default. These systems are repositioned between
                // emissions, and in Local space that drags every particle already in the
                // air along with the transform - the spray visibly jumped and flickered
                // instead of staying where it came out.
                mn.simulationSpace = ParticleSystemSimulationSpace.World;
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
                // World, not the Local default. These systems are repositioned between
                // emissions, and in Local space that drags every particle already in the
                // air along with the transform - the spray visibly jumped and flickered
                // instead of staying where it came out.
                mn.simulationSpace = ParticleSystemSimulationSpace.World;
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
        }

        // ── Spawn: splash projection ──────────────────────────────────────────────

        // 2026-07-22 (Ken, cross-mod ask: "make the pellets, rays, fragments, whatever that
        // comes from grenade and hit sosigs and humans cause 1/5th the splatter rays casted
        // than the set one"). rayCountScale lets a caller cut the ray count for a specific
        // damage class without touching the shared CfgRayCount/CfgGibRayCount config values
        // everything else still uses at full strength. Defaults to 1f (no change) for every
        // existing call site.
        internal static void SpawnProjection(Vector3 exitPt, Vector3 projDir,
                                              Sosig srcSosig, float bulletSpeed,
                                              bool gib = false, List<GameObject> shotList = null,
                                              float rayCountScale = 1f)
        {
            if (!CfgEnabled.Value || !CfgSplatterEnabled.Value) return;
            try
            {
                Color   col       = GetSosigBloodColor(srcSosig);
                Vector3 fwd       = projDir.normalized;
                float   tanHalf   = Mathf.Tan(CfgConeAngle.Value * Mathf.Deg2Rad) * 0.8f;
                float   range     = CfgRange.Value;
                float   projSpeed = Mathf.Max(1f, bulletSpeed * CfgSpeedRatio.Value) + CfgSpeedBias.Value;

                Vector3 worldUp = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.99f
                                ? Vector3.forward : Vector3.up;
                Vector3 right = Vector3.Cross(worldUp, fwd).normalized;
                Vector3 up    = Vector3.Cross(fwd, right);

                float randRad = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float cosR = Mathf.Cos(randRad), sinR = Mathf.Sin(randRad);
                Vector3 r2 = right * cosR + up * sinR;
                Vector3 u2 = up    * cosR - right * sinR;
                right = r2; up = u2;

                bool animated  = string.Equals(CfgProjectionMode.Value, "Animated",  System.StringComparison.OrdinalIgnoreCase);
                bool immediate = string.Equals(CfgProjectionMode.Value, "Immediate", System.StringComparison.OrdinalIgnoreCase);
                float scaleMax   = CfgDotScaleMax.Value;
                float scaleSlope = (scaleMax - 1f) / Mathf.Max(0.1f, CfgDotScaleRange.Value);

                int sampleCap = (_splatterUVs != null && _splatterUVs.Length > 0) ? _splatterUVs.Length : int.MaxValue;
                int N = gib
                    ? Mathf.Min(Mathf.Max(1, CfgGibRayCount.Value), sampleCap)
                    : Mathf.Min(Mathf.Max(1, CfgRayCount.Value),    sampleCap);
                if (rayCountScale < 0.999f) N = Mathf.Max(1, Mathf.RoundToInt(N * rayCountScale));

                // Gib explosion: spread rays over multiple frames to avoid a single huge spike.
                if (gib)
                {
                    _instance.StartCoroutine(SpawnGibGradual(exitPt, N, range, scaleMax, scaleSlope, col, srcSosig, shotList));
                    return;
                }

                // Secondary guard: if exitPt still ended up inside/below a static floor,
                // cast from 0.5m above it downward — floor within 0.6m means exitPt is embedded.
                {
                    int snapN = Physics.RaycastNonAlloc(exitPt + Vector3.up * 0.5f, Vector3.down, _rayBuf, 0.6f);
                    System.Array.Sort(_rayBuf, 0, snapN, _rhCompare);
                    for (int si = 0; si < snapN; si++)
                    {
                        RaycastHit sh = _rayBuf[si];
                        if (sh.collider.attachedRigidbody != null) continue;
                        if (sh.collider.GetComponentInParent<SosigLink>() != null) continue;
                        if (sh.normal.y < 0.5f) continue;
                        exitPt = sh.point + sh.normal * 0.05f;
                        break;
                    }
                }

                // Animation steps are CAPPED and stretched over the shot's own flight time, rather
                // than being one fixed 0.025s slice of flight each.
                //
                // Every step costs a separate BuildDotMesh call, and each of those emits its own
                // set of chunks - GameObject, mesh, renderer, material per brightness level per
                // density class. Slicing by a fixed time meant a distant shot, or a slow bullet,
                // produced more steps and therefore multiplied the whole chunk count: a 40m shot
                // ran 3 steps and a slow pistol 4, so the real cost was three to four times the
                // per-step figure. Distance was silently a performance setting.
                //
                // With a cap the far shot costs exactly what the near one does; the steps simply
                // spread further apart in time to still track the dots' flight. Close range needs
                // no animation at all - the flight is over in a few milliseconds - so it collapses
                // to a single step and one BuildDotMesh.
                const float NO_ANIM_DISTANCE = 3f;
                var staticBins = new Dictionary<int, List<DotData>>();
                var dynBins    = new Dictionary<int, Dictionary<Transform, List<DotData>>>();

                // Furthest hit decides how the steps are spread, so it has to be known before
                // anything is binned. Collected in the ray loop below, applied after.
                var pending    = new List<DotData>();
                var pendingPar = new List<Transform>();
                float maxDist  = 0f;

                if (_flyBuf == null || _flyBuf.Length < N) _flyBuf = new ParticleSystem.Particle[N];
                int flyCount = 0;
                Color32 col32 = col;

                for (int i = 0; i < N; i++)
                {
                    Vector2 uv; float dark; float bright; Vector3 tanNorm; byte curveClass;
                    SampleSplatter(out uv, out dark, out bright, out tanNorm, out curveClass);

                    Vector3 dir = gib
                        ? UnityEngine.Random.onUnitSphere
                        : (fwd + right * uv.x * tanHalf + up * uv.y * tanHalf).normalized;

                    // Origin steps BACK along bullet path so downward shots start above the floor, not below it.
                    // That backward step (and a wide cone on a small target like a head) means a ray
                    // can easily clip back into the SAME sosig that emitted it before ever reaching the
                    // wall - a plain Physics.Raycast stops at that first hit and the ray is silently
                    // dropped, which is why headshots especially could produce little/no splatter even
                    // on a confirmed penetration. Cast through all hits instead and skip past the source
                    // sosig's own colliders (and its weapon) to find whatever is actually behind it.
                    int hitCount = Physics.RaycastNonAlloc(exitPt - fwd * 0.15f, dir, _rayBuf, range);
                    System.Array.Sort(_rayBuf, 0, hitCount, _rhCompare);
                    RaycastHit h = default(RaycastHit);
                    bool foundHit = false;
                    for (int hi = 0; hi < hitCount; hi++)
                    {
                        RaycastHit cand = _rayBuf[hi];
                        if (IsSourceSosig(cand.collider, srcSosig)) continue;
                        if (cand.collider.GetComponentInParent<SosigWeapon>() != null) continue;
                        h = cand;
                        foundHit = true;
                        break;
                    }
                    if (!foundHit) continue;

                    float dotR = CfgDotSize.Value * Mathf.Clamp(1f + h.distance * scaleSlope, 1f, scaleMax);
                    if (h.distance > maxDist) maxDist = h.distance;

                    float sinAngle  = Mathf.Abs(Vector3.Dot(dir, h.normal));
                    float elong     = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 8f);
                    Vector3 elongVec = dir - Vector3.Dot(dir, h.normal) * h.normal;
                    if (elongVec.sqrMagnitude > 0.001f) elongVec.Normalize();
                    else elongVec = right;

                    Rigidbody hitRb = h.collider.attachedRigidbody;
                    Transform par   = hitRb != null ? hitRb.transform : null;
                    var dd = new DotData(h.point, h.normal, dotR, dark, bright, tanNorm, elongVec, elong, h.distance, curveClass);

                    // Held until the furthest hit is known, then binned against it.
                    pending.Add(dd);
                    pendingPar.Add(par);

                    if (animated && !ReferenceEquals(_flyingDotPS, null) && flyCount < _flyBuf.Length)
                    {
                        float t = h.distance / projSpeed;
                        _flyBuf[flyCount].position          = exitPt;
                        _flyBuf[flyCount].velocity          = dir * projSpeed;
                        _flyBuf[flyCount].startLifetime     = t;
                        _flyBuf[flyCount].remainingLifetime = t;
                        _flyBuf[flyCount].startSize         = dotR * 2f;
                        _flyBuf[flyCount].startColor        = col32;
                        flyCount++;
                    }
                }

                // Spread the capped steps across this shot's own reach. A dot landing at half the
                // furthest distance falls in the middle step regardless of whether "furthest" is
                // four metres or forty, so the step count - and with it the chunk count - no
                // longer depends on how far away the wall was.
                int steps = (!animated || maxDist < NO_ANIM_DISTANCE) ? 1 : AnimStepsForDistance(maxDist);
                float binSpan = Mathf.Max(0.0001f, maxDist) / steps;
                for (int i = 0; i < pending.Count; i++)
                {
                    DotData dd    = pending[i];
                    Transform par = pendingPar[i];
                    int bin = Mathf.Clamp((int)(dd.Dist / binSpan), 0, steps - 1);

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
                // Each step now covers binSpan metres of flight, so its wait is that distance's
                // travel time - keeping the decals landing with the dots that are still in flight.
                float binSeconds = binSpan / projSpeed;

                if (staticBins.Count == 0 && dynBins.Count == 0) return;

                if (animated && !ReferenceEquals(_flyingDotPS, null) && flyCount > 0)
                {
                    int prevCount = _flyingDotPS.GetParticles(_flyMergeBuf);
                    int copyN     = Mathf.Min(flyCount, _flyMergeBuf.Length - prevCount);
                    if (copyN > 0)
                    {
                        System.Array.Copy(_flyBuf, 0, _flyMergeBuf, prevCount, copyN);
                        _flyingDotPS.SetParticles(_flyMergeBuf, prevCount + copyN);
                        if (!_flyingDotPS.isPlaying) _flyingDotPS.Play();
                    }
                }

                if (!_dbgDotLogged)
                {
                    _dbgDotLogged = true;
                    Log.LogInfo("[BloodSystem] First projection: " + flyCount + " particles speed=" + projSpeed.ToString("F1") + " N=" + N + " mode=" + CfgProjectionMode.Value);
                }

                if (immediate)
                {
                    var allStatic = new List<DotData>();
                    foreach (var kv in staticBins) allStatic.AddRange(kv.Value);
                    if (allStatic.Count > 0) BuildDotMesh(allStatic, null, col, shotList);
                    var dynFlat = new Dictionary<Transform, List<DotData>>();
                    foreach (var bkv in dynBins)
                        foreach (var pkv in bkv.Value)
                        {
                            if (!dynFlat.ContainsKey(pkv.Key)) dynFlat[pkv.Key] = new List<DotData>();
                            dynFlat[pkv.Key].AddRange(pkv.Value);
                        }
                    foreach (var kv in dynFlat)
                        if (!ReferenceEquals(kv.Key, null) && kv.Key != null)
                            BuildDotMesh(kv.Value, kv.Key, col, shotList);
                }
                else
                {
                    _instance.StartCoroutine(DoDelayedSpawn(staticBins, dynBins, col, binSeconds, shotList));
                }
            }
            catch (Exception ex) { Log.LogError("[BloodSystem] SpawnProjection: " + ex); }
        }

        const int GIB_BATCH = 50; // gib rays per frame — 200 rays spread over 4 frames

        static IEnumerator SpawnGibGradual(Vector3 pos, int N, float range, float scaleMax, float scaleSlope,
                                           Color col, Sosig srcSosig, List<GameObject> shotList)
        {
            var staticDots = new List<DotData>();
            var dynDots    = new Dictionary<Transform, List<DotData>>();

            for (int i = 0; i < N; i++)
            {
                Vector2 uv; float dark; float bright; Vector3 tanNorm; byte curveClass;
                SampleSplatter(out uv, out dark, out bright, out tanNorm, out curveClass);
                Vector3 dir = UnityEngine.Random.onUnitSphere;

                // Gib rays start at the segment's own position, right inside the sosig's remaining
                // body - pass through the source sosig's colliders the same way the exit-wound rays
                // do instead of dropping any ray that happens to clip another part of itself first.
                int gHitCount = Physics.RaycastNonAlloc(pos, dir, _rayBuf, range);
                System.Array.Sort(_rayBuf, 0, gHitCount, _rhCompare);
                RaycastHit h = default(RaycastHit);
                bool gFound = false;
                for (int ghi = 0; ghi < gHitCount; ghi++)
                {
                    RaycastHit gc = _rayBuf[ghi];
                    if (IsSourceSosig(gc.collider, srcSosig)) continue;
                    if (gc.collider.GetComponentInParent<SosigWeapon>() != null) continue;
                    h = gc;
                    gFound = true;
                    break;
                }
                if (gFound)
                {
                    float dotR     = CfgDotSize.Value * Mathf.Clamp(1f + h.distance * scaleSlope, 1f, scaleMax);
                    float sinAngle = Mathf.Abs(Vector3.Dot(dir, h.normal));
                    float elong    = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 8f);
                    Vector3 elongVec = dir - Vector3.Dot(dir, h.normal) * h.normal;
                    if (elongVec.sqrMagnitude > 0.001f) elongVec.Normalize(); else elongVec = Vector3.right;

                    var dd = new DotData(h.point, h.normal, dotR, dark, bright, tanNorm, elongVec, elong, h.distance, curveClass);
                    Rigidbody rb = h.collider.attachedRigidbody;
                    if (rb == null)
                        staticDots.Add(dd);
                    else
                    {
                        Transform t = rb.transform;
                        if (!dynDots.ContainsKey(t)) dynDots[t] = new List<DotData>();
                        dynDots[t].Add(dd);
                    }
                }

                if ((i + 1) % GIB_BATCH == 0) yield return null;
            }

            if (staticDots.Count > 0) BuildDotMesh(staticDots, null, col, shotList);
            foreach (var kv in dynDots)
                if (!ReferenceEquals(kv.Key, null) && kv.Key != null)
                    BuildDotMesh(kv.Value, kv.Key, col, shotList);
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
        // follow: the body part the blood came out of, so a staggered spray tracks it. Null for a
        // stationary source.
        internal static void SpawnBloodSpray(Vector3 pos, Vector3 fwd, Color col, bool explode = false, float speedScale = 1f, float burstFraction = 1f, Transform follow = null)
        {
            if (!CfgEnabled.Value || !CfgSprayEnabled.Value) return;

            // The outer and mid layers are released over about a second rather than all at once.
            // Emitted in one go they mark the single spot the sosig occupied at the instant it was
            // hit; released in slices, each slice comes out wherever the body has moved to by
            // then, so a sosig that is running when it takes the hit leaves a short trail of spray
            // along its path instead of one puff hanging in the air behind it.
            if (!ReferenceEquals(_instance, null))
            {
                // A 360 burst means the part came apart, so there is nothing left to trail from -
                // it stays put regardless of what the rest of the body does.
                Transform trailTarget = explode ? null : follow;

                _instance.StartCoroutine(DoTrailingSpray(pos, fwd, col, explode, speedScale, burstFraction,
                                                         trailTarget, doFog: true, doPellet: false, seconds: 0.3f));
                _instance.StartCoroutine(DoTrailingSpray(pos, fwd, col, explode, speedScale, burstFraction,
                                                         trailTarget, doFog: false, doPellet: true, seconds: 1f));
                SprayJetOnly(pos, fwd, col, explode, speedScale, burstFraction);
                return;
            }

            SpraySprayImmediate(pos, fwd, col, explode, speedScale, burstFraction);
        }

        // Inner drops plus the surface staining, both of which belong to the instant of impact.
        static void SprayJetOnly(Vector3 pos, Vector3 fwd, Color col, bool explode, float speedScale, float burstFraction)
        {
            SpraySprayImmediate(pos, fwd, col, explode, speedScale, burstFraction,
                                doFog: false, doPellet: false, doJet: true, doStain: true);
        }

        // Releases the outer and mid layers over about a second instead of in one puff, each
        // slice emitted wherever the wound has moved to by then. A sosig shot while running
        // leaves a trail along its path rather than a single cloud where it used to be.
        //
        // The particle systems are shared singletons, so two sprays inside the same second
        // interleave their slices. Each slice still emits at a sensible place, but the layer
        // settings belong to whichever spray wrote them last.
        static IEnumerator DoTrailingSpray(Vector3 pos, Vector3 fwd, Color col, bool explode,
                                           float speedScale, float burstFraction, Transform follow,
                                           bool doFog, bool doPellet, float seconds)
        {
            const int   SLICES     = 6;
            const float FRONT_LOAD = 0.5f;   // released instantly, so the hit still reads as a hit

            float wait  = seconds / SLICES;
            // Splitting a layer evenly across its window made it look like it had vanished: the
            // total was unchanged, but only a fraction was on screen at any moment. Most goes out
            // at impact and the rest trails behind it.
            float trail = (1f - FRONT_LOAD) / (SLICES - 1);

            for (int i = 0; i < SLICES; i++)
            {
                // Only the emission POINT moves. The systems simulate in world space, so blood
                // already in the air stays where it came out - a later slice simply starts from
                // wherever the wound has got to, which is what draws the trail.
                Vector3 at = pos;
                if (follow != null) at = follow.position;   // Unity null: destroyed link falls back

                SpraySprayImmediate(at, fwd, col, explode, speedScale, burstFraction,
                                    doFog: doFog, doPellet: doPellet, doJet: false, doStain: false,
                                    countScale: (i == 0) ? FRONT_LOAD : trail);
                yield return new WaitForSeconds(wait);
            }
        }

        static void SpraySprayImmediate(Vector3 pos, Vector3 fwd, Color col, bool explode, float speedScale, float burstFraction,
                                        bool doFog = true, bool doPellet = true, bool doJet = true, bool doStain = true, float countScale = 1f)
        {
            Quaternion rot = Quaternion.LookRotation(fwd);
            float sc = explode ? Mathf.Clamp(speedScale, 0.2f, 2.5f) : 1f;
            float bf = Mathf.Clamp01(burstFraction);

            // ── Outer fog: slow mist, blooms wide ────────────────────────────────
            if (doFog && !ReferenceEquals(_fogPS, null))
            {
                _fogPS.transform.position = pos;
                _fogPS.transform.rotation = rot;
                var mn = _fogPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.4f * sc, 0.8f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(0.5f * sc, 2.0f * sc);
                    var sh = _fogPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.3f;
                    _fogPS.Emit(Mathf.RoundToInt(500 * bf * countScale));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.8f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
                    var sh = _fogPS.shape;
                    sh.shapeType = ParticleSystemShapeType.ConeShell; sh.angle = 85f; sh.radius = 0.02f;
                    _fogPS.Emit(Mathf.RoundToInt(500 * countScale));
                }
            }

            // ── Mid fog: medium blobs, moderate speed ─────────────────────────────
            if (doPellet && !ReferenceEquals(_pelletPS, null))
            {
                _pelletPS.transform.position = pos;
                _pelletPS.transform.rotation = rot;
                var mn = _pelletPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.4f * sc, 0.8f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(1.5f * sc, 5.0f * sc);
                    var sh = _pelletPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.05f;
                    _pelletPS.Emit(Mathf.RoundToInt(600 * bf * countScale));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.0f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(1.5f, 4.0f);
                    var sh = _pelletPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 20f; sh.radius = 0.02f;
                    _pelletPS.Emit(Mathf.RoundToInt(400 * countScale));
                }
            }

            // ── Inner drops: fast, dense core ─────────────────────────────────────
            if (doJet && !ReferenceEquals(_jetPS, null))
            {
                _jetPS.transform.position = pos;
                _jetPS.transform.rotation = rot;
                var mn = _jetPS.main;
                mn.startColor = new ParticleSystem.MinMaxGradient(col);
                if (explode)
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.4f * sc, 0.8f * sc);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(3.0f * sc, 9.0f * sc);
                    var sh = _jetPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Sphere; sh.radius = 0.03f;
                    _jetPS.Emit(Mathf.RoundToInt(300 * bf * countScale));
                }
                else
                {
                    mn.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.0f);
                    mn.startSpeed    = new ParticleSystem.MinMaxCurve(8.0f, 18.0f);
                    var sh = _jetPS.shape;
                    sh.shapeType = ParticleSystemShapeType.Cone; sh.angle = 5f; sh.radius = 0.005f;
                    _jetPS.Emit(Mathf.RoundToInt(200 * countScale));
                }
            }

            // Direct surface staining: immediate raycasts approximate where spray particles land.
            // Avoids particle-polling; reliable at any range since it's not tied to particle travel distance.
            // Runs once with the initial burst, never per trail slice.
            if (doStain)
            {
                Vector3 right3 = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
                right3 = Vector3.Cross(right3, fwd).normalized;
                Vector3 up3 = Vector3.Cross(fwd, right3);

                if (explode)
                {
                    // Gib: 20 random sphere directions, 4m range.
                    for (int i = 0; i < 20; i++)
                    {
                        Vector3 d = UnityEngine.Random.onUnitSphere;
                        RaycastHit sh;
                        if (Physics.Raycast(pos, d, out sh, 4f) && sh.collider.attachedRigidbody == null)
                            SpawnSprayDot(sh.point, sh.normal, col);
                    }
                }
                else
                {
                    // Fog layer: 8 rays at exactly 85° (ring of mist perpendicular to bullet).
                    for (int i = 0; i < 8; i++)
                    {
                        float phi = i * (Mathf.PI * 2f / 8f);
                        Vector3 d = (fwd * 0.087f + (right3 * Mathf.Cos(phi) + up3 * Mathf.Sin(phi)) * 0.996f).normalized;
                        RaycastHit sh;
                        if (Physics.Raycast(pos, d, out sh, 0.6f) && sh.collider.attachedRigidbody == null)
                            SpawnSprayDot(sh.point, sh.normal, col);
                    }
                    // Pellet layer: 8 rays spread across 20° cone.
                    for (int i = 0; i < 8; i++)
                    {
                        float phi = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                        float s   = Mathf.Sin(20f * Mathf.Deg2Rad);
                        float c   = Mathf.Cos(20f * Mathf.Deg2Rad);
                        Vector3 d = (fwd * c + (right3 * Mathf.Cos(phi) + up3 * Mathf.Sin(phi)) * s).normalized;
                        RaycastHit sh;
                        if (Physics.Raycast(pos, d, out sh, 2.5f) && sh.collider.attachedRigidbody == null)
                            SpawnSprayDot(sh.point, sh.normal, col);
                    }
                    // Jet layer: 4 rays in tight 5° cone, longer range (fast drops).
                    for (int i = 0; i < 4; i++)
                    {
                        float phi = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                        float s   = Mathf.Sin(5f * Mathf.Deg2Rad);
                        float c   = Mathf.Cos(5f * Mathf.Deg2Rad);
                        Vector3 d = (fwd * c + (right3 * Mathf.Cos(phi) + up3 * Mathf.Sin(phi)) * s).normalized;
                        RaycastHit sh;
                        if (Physics.Raycast(pos, d, out sh, 5f) && sh.collider.attachedRigidbody == null)
                            SpawnSprayDot(sh.point, sh.normal, col);
                    }
                }
            }
        }

        // ── Spawn: drip stain quad (static surfaces only) ─────────────────────────

        internal static void SpawnDripStain(Vector3 pos, Vector3 normal, Color col, float scale = 1f)
        {
            col = BrightTint(col);
            int cohortId = BloodThermal.CurrentCohort(BloodThermal.Classes - 1);
            BloodThermal.NoteSpawnPosition(cohortId, pos);
            Material mat = GetBloodMat(col, cohortId);
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
            Vector3 dsn = normal.normalized;
            mesh.normals   = new[] { dsn, dsn, dsn, dsn };
            mesh.colors    = new[] { col, col, col, col };
            mesh.triangles = new[] { 0, 2, 3, 0, 1, 2 };
            mesh.RecalculateBounds();

            var go = new GameObject("DS");
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            BloodThermal.RegisterRenderer(mr, cohortId, pos);
        }

        // ── Custom blood drop effect ──────────────────────────────────────────────
        // Launches small blood drops from a wound point. Each drop simulates gravity,
        // then on surface contact spawns a hard-edge circle stain that grows to 1.5x.

        static bool _dbgDropLogged;

        // Spawns visible blood-drop particles at the wound and a coroutine that predicts landing
        // and places stain decals at the right time — no fragile particle polling needed.
        internal static void SpawnBloodDrops(Vector3 pos, Vector3 outward, Color col, int count, List<GameObject> shotList = null)
        {
            if (!CfgEnabled.Value || !CfgDripStainsEnabled.Value) return;
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
            { var dmat = new Material(_pelletMat); dmat.color = col; BloodThermal.MarkAlwaysHot(dmat); psr.material = dmat; }
            ps.Play();
            ps.Emit(count);
            UnityEngine.Object.Destroy(go, 6f);

            // Predict landing and schedule stains via coroutine — no particle polling
            _instance.StartCoroutine(DoDropStains(spawnPos, col, count, shotList));
        }

        // Cross-mod ask (Ken): "blood drip stains seem to be staining the air... around waist
        // height of humans... invisible thing... looks round". Root cause: the floor-detection
        // filters below already exclude the sosig's OWN vanilla body (attachedRigidbody != null,
        // SosigLink parent), but the Human mod adds its own separate hitbox colliders
        // (HumanLimbHitbox, roughly capsule/sphere-shaped around limb midpoints - e.g. a hip/
        // pelvis one would sit right around waist height) that live OUTSIDE that vanilla
        // hierarchy entirely, on a custom visual rig. A downward raycast grazing one of those at
        // a shallow angle can register a roughly-upward normal and get accepted as "the floor".
        // Name-based lookup (no compile-time dependency on that mod's assembly, safe/no-op if
        // it's not installed) walking up the hit's own transform chain, since GetComponentInParent
        // has no string-type overload in this Unity version.
        static bool HasComponentInParentByName(Component c, string typeName)
        {
            Transform t = c.transform;
            while (t != null)
            {
                if (t.GetComponent(typeName) != null) return true;
                t = t.parent;
            }
            return false;
        }

        static IEnumerator DoDropStains(Vector3 pos, Color col, int count, List<GameObject> shotList)
        {
            // RaycastAll so sosig body between wound and floor doesn't block
            var origin = pos + Vector3.up * 0.1f;
            int hitN   = Physics.RaycastNonAlloc(origin, Vector3.down, _rayBuf, 6f);
            System.Array.Sort(_rayBuf, 0, hitN, _rhCompare);

            RaycastHit floor = default;
            bool found = false;
            for (int hi = 0; hi < hitN; hi++)
            {
                RaycastHit h = _rayBuf[hi];
                if (h.collider.attachedRigidbody != null) continue;
                if (h.collider.GetComponentInParent<SosigLink>() != null) continue;
                if (HasComponentInParentByName(h.collider, "HumanLimbHitbox")) continue;
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
            if (!CfgEnabled.Value || !CfgDripStainsEnabled.Value) yield break;
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
                    if (HasComponentInParentByName(h.collider, "HumanLimbHitbox")) continue;
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
            int cohortId = BloodThermal.CurrentCohort(BloodThermal.Classes - 1);
            BloodThermal.NoteSpawnPosition(cohortId, pos);
            Material mat = GetDripMat(col, cohortId);
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.001f, 0.007f);
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
            mesh.normals   = new Vector3[] { normal, normal, normal, normal };
            mesh.colors    = new Color[] { col, col, col, col };
            mesh.triangles = new int[] { 0, 2, 3, 0, 1, 2 };
            mesh.RecalculateBounds();

            var go = new GameObject("GS");
            go.transform.position   = pos + normal * 0.003f;
            go.transform.rotation   = rot;
            go.transform.localScale = Vector3.one * r;
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            BloodThermal.RegisterRenderer(mr, cohortId, pos);
        }

        // Single stretched stain: one ellipse quad per droplet, elongated along the
        // surface-projected travel direction. Head-on impact = round; grazing = elongated.
        // Same elongation logic as BuildDotMesh splash dots.
        internal static void SpawnDripStainStreak(Vector3 origin, Vector3 worldVel, Vector3 hitNormal, Color col, List<GameObject> shotList = null, bool sprayStreak = false)
        {
            Vector3 N    = hitNormal.normalized;
            Vector3 vDir = worldVel.sqrMagnitude > 0.001f ? worldVel.normalized : -N;

            float sinAngle = Mathf.Abs(Vector3.Dot(vDir, N));
            float elong    = Mathf.Clamp(1f / Mathf.Max(0.15f, sinAngle), 1f, 6f);

            // Spray streaks: reduce alpha for long grazing streaks.
            // Quantized to 5 steps because alpha is part of the material cache key — a continuous
            // value here means a brand new Material for very nearly every streak. That was always
            // wasteful; it matters more now that the cache key also carries a cohort id.
            if (sprayStreak)
            {
                float aT = Mathf.Round(Mathf.Clamp01((elong - 1f) / 5f) * 4f) / 4f;
                col = new Color(col.r, col.g, col.b, Mathf.Lerp(0.9f, 0.7f, aT));
            }

            col = BrightTint(col);

            int cohortId = BloodThermal.CurrentCohort(BloodThermal.Classes - 1);
            BloodThermal.NoteSpawnPosition(cohortId, origin);
            Material mat = GetDripMat(col, cohortId);
            if (ReferenceEquals(mat, null)) return;

            float r = UnityEngine.Random.Range(0.008f, 0.024f);

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

            // Elongation via non-uniform scale: X=elongDir (stretched), Y=perpDir, Z=N.
            // LookRotation(N, perpDir) → Z=N, X=elongDir, Y=perpDir (since Cross(perpDir,N)=elongDir).
            var go = new GameObject("DStr");
            go.transform.position   = origin + N * 0.003f;
            go.transform.rotation   = Quaternion.LookRotation(N, perpDir);
            go.transform.localScale = new Vector3(r * elong * 2f, r * 2f, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _dotQuadMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            TrackGO(go, shotList);
            BloodThermal.RegisterRenderer(mr, cohortId, origin);
        }

        // Small round dot left by spray particles — Alloy + soft circle, same look as BD splash dots.
        // Alpha carried from particle fade color, so near-death spray leaves faint marks naturally.
        internal static void SpawnSprayDot(Vector3 pos, Vector3 normal, Color col)
        {
            col = BrightTint(col);
            int cohortId = BloodThermal.CurrentCohort(BloodThermal.Classes - 1);
            BloodThermal.NoteSpawnPosition(cohortId, pos);
            Material mat = GetBloodMat(col, cohortId);
            if (ReferenceEquals(mat, null) || ReferenceEquals(_dotQuadMesh, null)) return;

            float r    = UnityEngine.Random.Range(0.008f, 0.036f);
            Vector3    qup = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            Quaternion rot = Quaternion.LookRotation(normal, qup)
                           * Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));

            var go = new GameObject("SD");
            go.transform.position   = pos + normal * 0.003f;
            go.transform.rotation   = rot;
            go.transform.localScale = Vector3.one * (r * 2f);
            go.AddComponent<MeshFilter>().sharedMesh = _dotQuadMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial    = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            UnityEngine.Object.Destroy(go, CfgLifetime.Value);
            TrackGO(go, null);
            BloodThermal.RegisterRenderer(mr, cohortId, pos);
        }

        // ── Mesh building ─────────────────────────────────────────────────────────

        static void BuildDotMesh(List<DotData> dots, Transform parent, Color col, List<GameObject> shotList = null)
        {
            if (dots.Count == 0) return;

            // Group dots into brightness buckets. Each bucket gets its own darkened material
            // so the color difference is baked into _Color — no vertex-color dependency.
            //
            // Thermal adds a second axis: heat is a per-material value, so dots that cool at
            // different rates cannot share a mesh. The key becomes brightness × curve class.
            // With Curve Classes set to 1 (or thermal off) classes==1 and this is identical to
            // the old brightness-only chunking — no extra draw calls. Empty combinations are
            // skipped below, so the real chunk count stays well under the worst case.
            // ActiveClasses, not Classes: 1 until a thermal camera has actually rendered, so a
            // player without a thermal optic pays the same chunk count as before this system
            // existed rather than eight times it.
            int classes = BloodThermal.Enabled ? BloodThermal.ActiveClasses : 1;

            // All ten brightness levels, always. Thinning them to hold a chunk budget was the
            // wrong axis to spend: per-dot brightness variation is what stops splatter looking
            // flat, and halving it was immediately visible. The chunk count is controlled by the
            // density class count instead, which is the axis that only matters on thermal.
            float[] levels = BRIGHT_LEVELS;

            var buckets = new List<int>[levels.Length * classes];
            for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();

            for (int i = 0; i < dots.Count; i++)
            {
                DotData d   = dots[i];
                float dark  = Mathf.Lerp(0.4f, 1.0f, d.Dark);
                float shade = Mathf.Lerp(0.55f, 1.0f, Mathf.Clamp01(Vector3.Dot(d.TanNorm, _tanLight)));
                float total = dark * shade * d.Bright;
                int best = 0;
                float bestDist = Mathf.Abs(total - levels[0]);
                for (int j = 1; j < levels.Length; j++)
                {
                    float dist = Mathf.Abs(total - levels[j]);
                    if (dist < bestDist) { bestDist = dist; best = j; }
                }
                int cls = d.CurveClass < classes ? d.CurveClass : classes - 1;
                buckets[best * classes + cls].Add(i);
            }

            bool dbgDone = BloodSystemPlugin._dbgDotLogged;
            int chunksBuilt = 0;
            for (int bk = 0; bk < buckets.Length; bk++)
            {
                if (buckets[bk].Count == 0) continue;
                chunksBuilt++;
                int b   = bk / classes;
                int cls = bk % classes;
                float lv = levels[b];
                Color matCol = new Color(Mathf.Clamp01(col.r * lv), Mathf.Clamp01(col.g * lv), Mathf.Clamp01(col.b * lv), col.a);
                int cohortId = BloodThermal.CurrentCohort(cls);
                // Tell the cohort where its blood is before any material is made, so the first
                // heat value already accounts for local ambient.
                BloodThermal.NoteSpawnPosition(cohortId, dots[buckets[bk][0]].Pos);
                Material mat = GetBloodMat(matCol, cohortId);
                if (ReferenceEquals(mat, null)) continue;

                const int MAX = 16383;
                int total2 = buckets[bk].Count;
                for (int start = 0; start < total2; start += MAX)
                {
                    int count = Mathf.Min(MAX, total2 - start);
                    var verts = new Vector3[count * 4];
                    var uvs   = new Vector2[count * 4];
                    var norms = new Vector3[count * 4];
                    var tris  = new int[count * 6];

                    for (int i = 0; i < count; i++)
                    {
                        DotData d    = dots[buckets[bk][start + i]];
                        Vector3 norm = d.Norm;
                        float   r    = d.R;

                        Vector3 elongDir = d.ElongDir;
                        Vector3 perpDir  = Vector3.Cross(norm, elongDir);
                        if (perpDir.sqrMagnitude < 0.001f)
                        {
                            Vector3 qup2 = Mathf.Abs(Vector3.Dot(norm, Vector3.up)) > 0.9f
                                         ? Vector3.forward : Vector3.up;
                            perpDir = Vector3.Cross(norm, qup2);
                        }
                        perpDir.Normalize();

                        Vector3 qr = elongDir * (r * d.Elongation);
                        Vector3 qu = perpDir  * r;
                        Vector3 bp = d.Pos + norm * 0.003f;

                        Vector3 c0 = bp-qr-qu, c1 = bp+qr-qu, c2 = bp+qr+qu, c3 = bp-qr+qu;
                        if (parent != null)
                        {
                            c0 = parent.InverseTransformPoint(c0);
                            c1 = parent.InverseTransformPoint(c1);
                            c2 = parent.InverseTransformPoint(c2);
                            c3 = parent.InverseTransformPoint(c3);
                        }

                        int v = i * 4;
                        verts[v]=c0; verts[v+1]=c1; verts[v+2]=c2; verts[v+3]=c3;
                        norms[v]=norm; norms[v+1]=norm; norms[v+2]=norm; norms[v+3]=norm;
                        uvs[v]  =new Vector2(0,0); uvs[v+1]=new Vector2(1,0);
                        uvs[v+2]=new Vector2(1,1); uvs[v+3]=new Vector2(0,1);
                        int t = i * 6;
                        tris[t]=v; tris[t+1]=v+2; tris[t+2]=v+3;
                        tris[t+3]=v; tris[t+4]=v+1; tris[t+5]=v+2;
                    }

                    var mesh = new Mesh();
                    mesh.vertices  = verts;
                    mesh.uv        = uvs;
                    mesh.normals   = norms;
                    mesh.triangles = tris;
                    mesh.RecalculateBounds();

                    var go = new GameObject("BD");
                    if (parent != null) go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().mesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    // sharedMaterial, not material: the cached material IS the shared cooling
                    // state that BloodThermal writes to. Assigning through .material risks Unity
                    // handing this renderer a private copy, which would silently detach it from
                    // its cohort and leave it stuck at whatever heat it spawned with.
                    mr.sharedMaterial    = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows    = false;
                    UnityEngine.Object.Destroy(go, CfgLifetime.Value);
                    TrackGO(go, shotList);
                    BloodThermal.RegisterRenderer(mr, cohortId, dots[buckets[bk][start]].Pos);

                    if (!dbgDone)
                    {
                        dbgDone = true;
                        BloodSystemPlugin._dbgDotLogged = true;
                        DotData d0 = dots[buckets[bk][start]];
                        Vector3 wp = parent != null ? parent.TransformPoint(verts[0]) : verts[0];
                        BloodSystemPlugin.Log.LogInfo("[BloodSystem] DBG dot[0] worldPos=" + wp
                            + " r=" + d0.R + " norm=" + d0.Norm
                            + " matShader=" + mat.shader.name
                            + " matColor=" + mat.GetColor("_Color")
                            + " hasTex=" + (!ReferenceEquals(mat.mainTexture, null)));
                    }
                }
            }
            BloodThermal.DebugNoteChunks(chunksBuilt, dots.Count);
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

            // Blood/Color Override Mode: "1" (spaces stripped) = soft, "2" = hard, anything else = unset.
            string ovMode = (CfgColorOverrideMode.Value ?? "").Replace(" ", "");
            bool hardOverride = ovMode == "2";
            bool softOverride = ovMode == "1";

            // Hard override wins over everything, including NGA per-sosig colors.
            if (hardOverride)
            {
                Color hardCol;
                if (ColorUtility.TryParseHtmlString(CfgColorOverride.Value, out hardCol)) return hardCol;
            }

            if (_ngaKetchup) return _ngaColor;

            // Soft override replaces the default mustard color but not an NGA-configured
            // per-sosig color (e.g. zombies) — that case already returned above.
            if (softOverride)
            {
                Color softCol;
                if (ColorUtility.TryParseHtmlString(CfgColorOverride.Value, out softCol)) return softCol;
            }

            // BUG FIX (confirmed via a user report + their cfg: Mode="0"/Unset with a yellow hex
            // Override that was correctly being ignored per Unset's own contract, then STILL
            // showing red - because this fell through to red on purpose). Previous reasoning here
            // was backwards: `SosigClownMode` is not a "blood is yellow" toggle - decompiled
            // Sosig.cs only uses it to swap in `FXM.GetClownFX(...)` novelty particle PREFABS
            // (confetti-style), a completely separate system from this plugin's own tinted
            // particles/decals. Vanilla H3VR blood is mustard-yellow unconditionally; there is no
            // real "vanilla red blood" mode to fall back to. Unset must just mean mustard.
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
            if (col == null) return false;
            if (src == null) return false;
            SosigLink lk = col.GetComponentInParent<SosigLink>();
            if (lk != null && lk.S != null && lk.S == src) return true;
            if (col.transform.IsChildOf(src.transform)) return true;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null)
            {
                var tag = rb.GetComponent<SosigGibTag>();
                if (tag != null && tag.SourceSosig == src) return true;
            }
            return false;
        }
    }

    // ── Helper MonoBehaviours ─────────────────────────────────────────────────────

    public class SosigGibTag : MonoBehaviour
    {
        public Sosig SourceSosig;
    }


    // Per-bullet inter-frame state written by PreMove, read by Damage postfix and PostMove.
    public class SplatterTracker : MonoBehaviour
    {
        public Vector3    LastBulletDir;
        public float      LastBulletSpeed = 400f;
        // Geometry-based exit detection (restored from b555e65): bullet must physically enter
        // and then leave a SosigLink collider. Armor/faceshields are not SosigLinks → immune.
        public Collider   PrevCollider;
        public SosigLink  PrevHitLink;
        public Vector3    LastSosigLinkPos;
        public Sosig      LastSosig;
        // Optional accurate data from SosigLink.Damage — used if available, not the trigger.
        public bool    PendingBlood;
        public Vector3 PendingExitPt;
        public Vector3 PendingStrikeDir;
        public Sosig   PendingSrc;
        public Color   PendingCol;
        public Vector3 PendingEntryPt;
        // The body part the bullet actually went through. PostMove only has the projectile, which
        // is gone a moment later, so the trailing spray needs this to know what to follow.
        public SosigLink PendingWoundLink;
        public bool    IsPlayerShot;
        public float   PendingBloodScale = 1f;
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
        int                       _maxRaycast = 8;

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
            if (++_skip < 4) return; _skip = 0;

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

                float vMag = worldVel.magnitude;
                // Spray particles are fast (8-18 m/s); clamp at 0.08m misses walls in 1 frame.
                // Drip particles are slow; keep original short window to avoid false wall hits.
                float castDist = _sprayMode
                    ? Mathf.Max(0.05f, vMag * Time.deltaTime * 5f)
                    : Mathf.Clamp(vMag * Time.deltaTime * 3f, 0.04f, 0.08f);
                if (vMag > 0.05f && Physics.Raycast(pos, worldVel / vMag, out h, castDist))
                    found = true;

                // Downward proximity for slow/settling particles just above a floor
                if (!found && Physics.Raycast(pos + Vector3.up * 0.02f, Vector3.down, out h, 0.04f))
                    found = true;

                if (!found && !_useParticleColor && _buf[i].remainingLifetime < _buf[i].startLifetime * 0.08f)
                {
                    if (vMag > 0.05f) Physics.Raycast(pos, worldVel / vMag, out h, 0.08f);
                    if (ReferenceEquals(h.collider, null))
                        Physics.Raycast(pos + Vector3.up * 0.04f, Vector3.down, out h, 0.06f);
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
                    else if (roll >= 0.90f)
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

    // ── Thermal arming ────────────────────────────────────────────────────────────
    //
    // Every thermal render in the game funnels through PIPScope.ApplyCameraShader — both the
    // scope path (PIPScope's own render loop) and the standalone handheld/head-mounted
    // ThermalNVCamera. Postfixing it is the one reliable "a thermal camera is actually being used"
    // signal, and it lets the whole blood-heat system stay completely dormant until then.
    //
    // Deliberately NOT gated on PIPScope.temperatureRenderingEnabled: that flag is raised
    // mid-render and cleared again in the same frame, so it always reads false from an Update.
    //
    // Top-level (not nested inside BloodSystemPatches) and patched by its own explicit PatchAll
    // call — Harmony's PatchAll(Type) only processes the type it is handed, not nested types.
    [HarmonyPatch]
    static class ThermalArmHook
    {
        // ReferenceEquals, never == / != : H3VR's Mono has no Type.op_Equality/op_Inequality, so
        // a plain null-compare on a Type throws MissingMethodException at runtime. That killed
        // this whole patch class on first load once already.
        static bool Prepare() { return !ReferenceEquals(AccessTools.TypeByName("PIPScope"), null); }
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("PIPScope");
            if (ReferenceEquals(t, null)) return null;
            return AccessTools.Method(t, "ApplyCameraShader");
        }
        static void Postfix(Camera cam, bool isThermal)
        {
            if (!isThermal) return;
            BloodThermal.Arm();
            BloodThermal.LogThermalDiagnostics(cam);
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
        static readonly FieldInfo FHit =
            typeof(BallisticProjectile).GetField("m_hit",
                BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FBleedingEvents =
            typeof(Sosig).GetField("m_bleedingEvents",
                BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FPlayerIFF =
            typeof(FVRPlayerBody).GetField("m_playerIFF",
                BindingFlags.NonPublic | BindingFlags.Instance);

        // Cached once the player body exists. -1 = not resolved yet (never filter out on unknown).
        static int _playerIFF = -1;

        // Perf: skip blood entirely for bullets not fired by the player (PC gets blasted by
        // full-auto sosig crossfire during big fights — that's most of the frame spikes).
        internal static bool IsPlayerBullet(BallisticProjectile bp)
        {
            if (_playerIFF < 0)
            {
                if (GM.CurrentPlayerBody == null || ReferenceEquals(FPlayerIFF, null)) return true;
                try { _playerIFF = (int)FPlayerIFF.GetValue(GM.CurrentPlayerBody); }
                catch { return true; }
            }
            return bp.Source_IFF == _playerIFF;
        }

        internal static bool Ok => !ReferenceEquals(FLastColliderHit, null)
                                && !ReferenceEquals(FVelocity,        null)
                                && !ReferenceEquals(FHit,             null);

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
            if (!BloodSystemPlugin.CfgEnabled.Value || !BloodSystemPlugin.CfgVanillaStainEnabled.Value) return;
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
                    stainer.SetMaxRaycast(8);
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

            // Bullets are pooled/reused — clear stale state from a previous life of this object.
            tracker.PendingBlood  = false;
            tracker.IsPlayerShot  = IsPlayerBullet(__instance);

            // Non-player bullet: don't hand this tracker to OnSosigLinkDamage, which no-ops
            // when _activeBulletTracker is null — skips its raycast work entirely.
            _activeBulletTracker = tracker.IsPlayerShot ? tracker : null;

            tracker.PrevCollider = FLastColliderHit.GetValue(__instance) as Collider;

            var vel = (Vector3)FVelocity.GetValue(__instance);
            if (vel.magnitude > 0.01f)
            {
                tracker.LastBulletDir   = vel.normalized;
                tracker.LastBulletSpeed = vel.magnitude;
            }
        }

        // ── Bullet post-move ──────────────────────────────────────────────────────
        // dot < 0 detection (from 0cde31f): fires in the SAME tick as the hit.
        // dot = Dot(bullet_pos - hit.point, hit.normal).
        //   Penetrating bullet: ends up past the surface → dot < 0 → fire.
        //   Deflected bullet: ends up outside/back → dot ≥ 0 → no fire.
        // Collider-change guard prevents re-firing while bullet stays inside same link.
        // Confirmed byte-identical to the shipped v3.2.2 binary (decompiled and diffed 2026-07-26)
        // — a multi-tick variant was tried and reverted the same day after it introduced a stale-
        // pooled-tracker bug; don't re-attempt without a much more careful pooling-lifecycle guard.

        [HarmonyPatch(typeof(BallisticProjectile), "MoveBullet", typeof(float))]
        [HarmonyPostfix]
        static void PostMove(BallisticProjectile __instance)
        {
            _activeBulletTracker = null;

            if (!Ok) return;
            var tracker = __instance.GetComponent<SplatterTracker>();
            if (tracker == null) return;

            var currentCollider = FLastColliderHit.GetValue(__instance) as Collider;

            // Only act when bullet just hit a new surface.
            if (ReferenceEquals(currentCollider, tracker.PrevCollider)) goto alloyCheck;

            {
                bool curCsAlive = !ReferenceEquals(currentCollider, null);
                bool curUAlive  = curCsAlive && currentCollider != null;
                if (!curUAlive) goto alloyCheck;
                if (!tracker.IsPlayerShot) goto alloyCheck;  // sosig/AI hit — skip blood, perf

                // Two ways a collider can belong to a sosig: vanilla link colliders have a SosigLink
                // in their parent chain (links live in their own detached hierarchy), while overlay
                // mods (Human) put replacement hit colliders under the Sosig root itself - those have
                // a Sosig in the parent chain but no SosigLink. Accept either.
                SosigLink shotLink = currentCollider.GetComponentInParent<SosigLink>();
                Sosig hitSosig = shotLink != null ? shotLink.S : currentCollider.GetComponentInParent<Sosig>();
                if (hitSosig == null) goto alloyCheck;

                var     hit = (RaycastHit)FHit.GetValue(__instance);
                float   dot = Vector3.Dot(__instance.transform.position - hit.point, hit.normal);
                if (dot >= 0f) goto alloyCheck;  // deflected or glancing — not a penetration

                Vector3 dir     = tracker.LastBulletDir.sqrMagnitude > 0.01f
                                ? tracker.LastBulletDir : __instance.transform.forward;
                float   spd     = tracker.LastBulletSpeed > 1f ? tracker.LastBulletSpeed : 400f;
                Sosig   src     = hitSosig;
                Color   col     = BloodSystemPlugin.GetSosigBloodColor(src);

                // Use Damage-derived data if it's for THIS hit (same collider change tick).
                bool    hasPending = tracker.PendingBlood;
                Vector3 entryPt = hasPending ? tracker.PendingEntryPt  : hit.point;
                Vector3 exitPt  = hasPending ? tracker.PendingExitPt   : hit.point + dir * 0.35f;
                float   bloodScale = hasPending ? tracker.PendingBloodScale : 1f;
                tracker.PendingBlood = false;
                tracker.PendingBloodScale = 1f;

                if (!_bloodFiredOnce)
                {
                    _bloodFiredOnce = true;
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] First blood (dot=" + dot.ToString("F2")
                        + ") exitPt=" + exitPt + " vel=" + spd.ToString("F0"));
                }

                var shotList = BloodSystemPlugin.StartShotGroup();
                BloodSystemPlugin.SpawnProjection(exitPt, dir, src, spd, false, shotList, bloodScale);
                // Follow the WOUND, not the bullet. __instance here is the BallisticProjectile,
                // which is destroyed almost immediately - passing its transform meant the trail
                // lost its target at once and every slice fell back to the fixed impact point,
                // leaving the spray hanging where the sosig used to be.
                Transform woundT = (hasPending && tracker.PendingWoundLink != null)
                                 ? tracker.PendingWoundLink.transform : null;
                BloodSystemPlugin.SpawnBloodSpray(exitPt, dir, col, false, 1f, bloodScale, woundT);
                BloodSystemPlugin.SpawnBloodDrops(exitPt,  dir, col, Mathf.Max(1, Mathf.RoundToInt(10 * bloodScale)), shotList);
                BloodSystemPlugin.SpawnBloodDrops(entryPt, -dir, col, Mathf.Max(1, Mathf.RoundToInt(8 * bloodScale)), shotList);
            }

            alloyCheck:
            if (ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)
                && !BloodSystemPlugin._alloyGrabPending
                && !ReferenceEquals(currentCollider, null) && currentCollider != null
                && currentCollider.attachedRigidbody == null
                && currentCollider.GetComponentInParent<SosigLink>() == null)
            {
                BloodSystemPlugin._instance.StartCoroutine(BloodSystemPlugin.TryGrabAlloyFromScene());
            }
        }

        // ── SosigLink.Damage: captures accurate hit data for PostMove ────────────
        // Stores entry point, direction, color into SplatterTracker.PendingBlood.
        // PostMove uses this data when firing blood — NOT as the penetration trigger itself.

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
                // Try to find actual sosig exit surface.
                // 2026-07-21 (cross-mod report via Ken: "sphere in the middle of thigh length,
                // blood just appears there, no visible drip path from a real wound"). BUG
                // (found, not guessed): this raycast started from __instance.transform.position
                // - the SosigLink's OWN generic anchor point, not the real entry wound (d.point).
                // For a modded rig (e.g. H3VR-Human) whose actual hit surface lives on a custom
                // hitbox positioned nowhere near vanilla's generic per-link transform (a shared
                // upper-leg link sits between BOTH thighs), this searched for the exit surface
                // from entirely the wrong origin - landing exitPt near that generic anchor
                // instead of near the real wound, which reads as blood "just appearing" with no
                // connecting drip trail. Cast from the real entry point instead.
                RaycastHit xh;
                if (Physics.Raycast(d.point, sDir, out xh, 2f))
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
                    int clipN = Physics.RaycastNonAlloc(clipFrom, sDir, BloodSystemPlugin._rayBuf, clipDist);
                    System.Array.Sort(BloodSystemPlugin._rayBuf, 0, clipN, BloodSystemPlugin._rhCompare);
                    for (int ci = 0; ci < clipN; ci++)
                    {
                        RaycastHit ch = BloodSystemPlugin._rayBuf[ci];
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

                // 2026-07-23: consumed and reset immediately so it only ever applies to THIS hit.
                float bloodScale = BloodSystemPlugin.ExternalBloodScale;
                BloodSystemPlugin.ExternalBloodScale = 1f;

                // Store for PostMove velocity check (armor → velocity ≈ 0 → no blood).
                var t = _activeBulletTracker;
                t.PendingBlood      = true;
                t.PendingExitPt     = exitPt;
                t.PendingStrikeDir  = sDir;
                t.PendingSrc        = src;
                t.PendingCol        = col;
                t.PendingEntryPt    = d.point;
                t.PendingWoundLink  = __instance;
                t.PendingBloodScale = bloodScale;
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

                // Cross-mod ask (Ken): skip the splash/spray/gib burst for Human-mod humans by
                // default - GetComponent(string) does a name-based lookup with no compile-time
                // dependency on that mod's assembly, so this is a no-op (and no crash) if the
                // Human mod isn't installed at all.
                bool isHumanModHuman = !ReferenceEquals(src, null) && src.GetComponent("HumanMarker") != null;
                if (isHumanModHuman && !BloodSystemPlugin.CfgSplashOnHumans.Value) return;

                // Sosig.ClearSosig - the despawn routine - walks EVERY link and calls
                // LinkExplodes(Abstract) on each one. That is a corpse being cleaned up, not
                // anything being hit, and it was firing a full 360 degree burst per link: four
                // bursts out of a body that nobody shot, with nothing visibly coming apart. That
                // is the "360 without a segment breaking" report. Abstract only ever arrives from
                // that cleanup path, so it is never blood.
                if (damClass == Damage.DamageClass.Abstract) return;

                Color   col = BloodSystemPlugin.GetSosigBloodColor(src);

                // LinkExplodes fires whenever a body part's integrity hits zero — that's every
                // normal kill shot (usually Torso), not just dramatic dismemberment. Only treat
                // it as a real 360° gib burst when the game itself is actually going to spawn
                // gib chunks (same check FistVR.Sosig.DestroyLink uses) — otherwise fall back to
                // a normal directional spray so a plain kill doesn't look like a gib explosion.
                // LinkExplodes destroys the link outright (DestroyLink ends in
                // Destroy(link.gameObject)), so reaching here from real damage genuinely means a
                // segment was removed and the sphere burst is earned. No need to guess from which
                // body part it was - that was a proxy, and a wrong one.
                bool chunksOn = !ReferenceEquals(src, null) && src.UsesGibs
                    && GM.Options != null
                    && GM.Options.SimulationOptions.SosigChunksMode == SimulationOptions.SosigChunks.Enabled;
                bool realGib = chunksOn;

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

                // 2026-07-22 (Ken: "make the pellets, rays, fragments, whatever that comes from
                // grenade and hit sosigs and humans cause 1/5th the splatter rays casted than
                // the set one"). Applies to both sosigs and Human-mod humans (this call site
                // runs for anyone NOT already excluded above) - explosive-class kills only, every
                // other damage class keeps the full configured ray count unchanged.
                float rayScale = damClass == Damage.DamageClass.Explosive ? 0.2f : 1f;
                var shotList = BloodSystemPlugin.StartShotGroup();
                BloodSystemPlugin.SpawnProjection(pos, dir, src, spd, realGib, shotList, rayScale);
                // No follow target - this link is destroyed as part of the same call.
                BloodSystemPlugin.SpawnBloodSpray(pos, dir, col, realGib, speedScale);
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
        // internal, not the default private-for-a-nested-type: Awake has to be able to name this
        // to pass it to PatchAll. See the PatchAll block in Awake for why that is necessary.
        [HarmonyPatch]
        internal static class WfxDecalMaterialGrab
        {
            static bool _grabbed;

            // WFX_BulletHoleDecal is from the War FX asset package and does NOT exist in H3VR 1.0
            // — "WFX mat grabbed" has never once appeared in a log. The game's own decal component
            // is FistVR.ImpactDecal, so try that first and keep the WFX name only as a fallback
            // for older builds. Both are looked up by name so this assembly needs no compile-time
            // dependency on either.
            static readonly string[] DecalTypeNames = { "FistVR.ImpactDecal", "ImpactDecal", "WFX_BulletHoleDecal" };

            static System.Type FindDecalType()
            {
                for (int i = 0; i < DecalTypeNames.Length; i++)
                {
                    var t = AccessTools.TypeByName(DecalTypeNames[i]);
                    if (!ReferenceEquals(t, null)) return t;
                }
                return null;
            }

            // FistVR.ImpactDecal has no Start/Awake/OnEnable at all — its only method is
            // SetHeat(bool), called when a decal is placed, which serves the same purpose here.
            static readonly string[] DecalMethodNames = { "Start", "Awake", "OnEnable", "SetHeat" };

            static System.Reflection.MethodBase FindDecalMethod()
            {
                var t = FindDecalType();
                if (ReferenceEquals(t, null)) return null;
                for (int i = 0; i < DecalMethodNames.Length; i++)
                {
                    var m = AccessTools.Method(t, DecalMethodNames[i]);
                    if (!ReferenceEquals(m, null)) return m;
                }
                return null;
            }

            // Verifies a real target resolves — returning null from TargetMethod throws inside
            // Harmony, and this class has already spent its whole life silently doing nothing.
            // Disabled. This existed only to find a decal material at runtime, which the
            // shipped alloy_mat.cache now supplies before anything spawns. It was dead code
            // for the mod's entire life anyway - nested patch classes are never passed to
            // PatchAll - and the one time it did run it grabbed a bullet-hole material and
            // put that 2x2 atlas on the blood. A second route to a known bug, with nothing
            // left to gain.
            static bool Prepare() => false;
            static System.Reflection.MethodBase TargetMethod() { return FindDecalMethod(); }
            static void Postfix(Component __instance)
            {
                if (_grabbed) return;

                // Only ever a LAST RESORT, never a preference. Grabbing this material when we
                // already had a working one produced four bullet holes in the corners of every
                // blood decal: the game's impact-decal material is Alloy, whose albedo is
                // _ColorRGBOpacityA and holds a 2x2 atlas of bullet holes. Setting
                // Material.mainTexture only writes _MainTex, so the atlas kept being sampled and
                // each decal's 0..1 UVs spanned all four holes. Fixing that properly means
                // rewriting every Alloy albedo slot, which is not worth it — the cached/scene
                // material already works, and the thermal square was a RenderType tag problem
                // (see SetOverrideTag in ApplyBloodProps), not a source-material problem.
                if (!ReferenceEquals(BloodSystemPlugin._decalSourceMat, null)) { _grabbed = true; return; }
                try
                {
                    var r = __instance.GetComponent<Renderer>();
                    if (ReferenceEquals(r, null) || ReferenceEquals(r.sharedMaterial, null)) return;

                    Material prev = BloodSystemPlugin._decalSourceMat;
                    string prevDesc = ReferenceEquals(prev, null)
                        ? "none"
                        : prev.shader.name + " RenderType=" + prev.GetTag("RenderType", false, "<none>");

                    BloodSystemPlugin._decalSourceMat = new Material(r.sharedMaterial);
                    BloodSystemPlugin._decalSourceMat.SetInt("_Cull", 0);
                    BloodSystemPlugin._decalSourceSearched = true;
                    BloodSystemPlugin._matCache.Clear();
                    BloodSystemPlugin._dripMatCache.Clear();
                    _grabbed = true;
                    // RenderType is what the thermal replacement shader keys on, so it is the
                    // field worth seeing in the log when blood renders as a square.
                    BloodSystemPlugin.Log.LogInfo("[BloodSystem] WFX mat grabbed on decal Start: "
                        + r.sharedMaterial.shader.name
                        + " RenderType=" + r.sharedMaterial.GetTag("RenderType", false, "<none>")
                        + " (replaced: " + prevDesc + ")");
                }
                catch (Exception ex)
                {
                    BloodSystemPlugin.Log.LogWarning("[BloodSystem] WFxDecalGrab: " + ex.Message);
                }
            }
        }

        // Ken asked for this to live in BloodSystem ("add to blood system mod to tamper with
        // onslaught a bit") since it already establishes the soft-dependency Harmony pattern (see
        // WfxDecalMaterialGrab above) for interoperating with mods this assembly doesn't reference
        // at compile time. Two things, both from decompiling OnslaughtManager.Update directly:
        //
        // 1. Ken: "dont explode entire sosig right when they die, let them die more naturally."
        //    Stock Onslaught calls sosig.ClearSosig() the instant BodyState flips to Dead - an
        //    abrupt teleport-out-of-existence with no fall/settle. Swapped for TickDownToClear(3f),
        //    the same natural vanilla despawn route our own Human mod's corpse timer already uses -
        //    the body actually ragdolls/falls for a few seconds before disappearing.
        //
        // 2. Stock crash fix (confirmed via a user log with this EXACT stack trace):
        //    `foreach (var s in spawnedSosigs) { ... spawnedSosigs.Remove(s); ... }` - mutating a
        //    List while a foreach iterates it throws InvalidOperationException the moment two Sosigs
        //    die in the same Update tick (trivially likely with several humans fighting). This
        //    Prefix removes dead entries from spawnedSosigs and increments `kills` itself, SAFELY,
        //    BEFORE the original method's own foreach ever sees them - so by the time the original
        //    runs, its (buggy) loop simply finds no BodyState==Dead entries left to choke on. The
        //    original still runs afterward for everything else (difficulty/UI text, spawn calls,
        //    the player-death endgame check) completely untouched.
        [HarmonyPatch]
        internal static class OnslaughtNaturalDeathPatch
        {
            static bool Prepare() => !ReferenceEquals(AccessTools.TypeByName("localpcnerd.OnslaughtMode.OnslaughtManager"), null);

            static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("localpcnerd.OnslaughtMode.OnslaughtManager");
                if (ReferenceEquals(t, null)) return null;
                return AccessTools.Method(t, "Update");
            }

            static void Prefix(object __instance)
            {
                try
                {
                    var mgrType = __instance.GetType();
                    var spawnedSosigsField = AccessTools.Field(mgrType, "spawnedSosigs");
                    var spawnedSosigs = !ReferenceEquals(spawnedSosigsField, null) ? spawnedSosigsField.GetValue(__instance) as System.Collections.IList : null;
                    if (spawnedSosigs == null) return;

                    var killsField = AccessTools.Field(mgrType, "kills");
                    int kills = !ReferenceEquals(killsField, null) ? (int)killsField.GetValue(__instance) : 0;

                    Type markerType = null;
                    System.Reflection.FieldInfo sosigField = null;

                    for (int i = spawnedSosigs.Count - 1; i >= 0; i--)
                    {
                        object marker = spawnedSosigs[i];
                        if (ReferenceEquals(marker, null))
                        {
                            spawnedSosigs.RemoveAt(i);
                            continue;
                        }
                        if (ReferenceEquals(markerType, null)) { markerType = marker.GetType(); sosigField = AccessTools.Field(markerType, "sosig"); }
                        if (ReferenceEquals(sosigField, null)) continue;

                        Sosig sosig = sosigField.GetValue(marker) as Sosig;
                        if (sosig == null)
                        {
                            spawnedSosigs.RemoveAt(i);
                            continue;
                        }

                        if (sosig.BodyState == Sosig.SosigBodyState.Dead)
                        {
                            sosig.TickDownToClear(3f);
                            spawnedSosigs.RemoveAt(i);
                            kills++;
                        }
                    }

                    if (!ReferenceEquals(killsField, null)) killsField.SetValue(__instance, kills);
                }
                catch (Exception ex)
                {
                    BloodSystemPlugin.Log.LogWarning("[BloodSystem] OnslaughtNaturalDeath: " + ex.Message);
                }
            }
        }
    }
}
