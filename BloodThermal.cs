using System.Collections.Generic;
using BepInEx.Configuration;
using FistVR;
using UnityEngine;

namespace BloodSystem
{
    // ── Thermal-camera support for blood decals ───────────────────────────────────
    //
    // H3VR renders thermal by swapping in a replacement shader
    // (Camera.SetReplacementShader(Hidden/Thermal, "RenderType"), PIPScope.ApplyCameraShader)
    // that reads ONE float off each object: _ThermalIntensity. Vanilla writes it from the
    // ObjectTemperature component into a MaterialPropertyBlock. The units are arbitrary, not
    // Celsius - ObjectTemperature.GetThermalIntensity returns ThermalIntensity[profile] * scale - 1,
    // so a Sosig body (profile table entry 28) sits at 27 and untouched world geometry sits at 0.
    //
    // We deliberately do NOT use the ObjectTemperature component. It has a per-instance Update()
    // and a static allTemperatureObjects list that gets walked in UpdateTemperatures(), PreCull()
    // and PostRender(); putting one on every blood decal would add hundreds of Unity Update calls
    // per frame forever, thermal camera present or not. We write the same shader properties
    // ourselves instead.
    //
    // The optimization that makes this cheap: GetBloodMat/GetDripMat already hand out CACHED,
    // SHARED Materials. Every splash chunk of the same colour points at the same Material object.
    // So one mat.SetFloat("_ThermalIntensity", v) updates every decal using it at once - no
    // per-decal state, no per-frame simulation, no renderer lists to keep clean.
    //
    // Cooling is baked, never integrated at runtime:
    //   - At startup we solve Newton's law of cooling backwards for a handful of discrete
    //     temperature levels ("shades") and store the time each level is reached.
    //   - Blood spawned in the same time quantum with the same cooling curve forms a COHORT.
    //     A cohort owns the materials handed out during its window and walks the precomputed
    //     step table. Each step is one SetFloat per material.
    //   - Denser parts of a splat (baked from the source PNG's local alpha density at CDF build
    //     time) get a flatter, more linear curve; sparse mist follows Newton more closely and
    //     cools faster.
    //
    // Nothing is written at all until a thermal camera actually renders - see Arm(). A player who
    // never equips a thermal optic pays one list-add per cohort and nothing else.

    internal static class BloodThermal
    {
        // ── Shader property names (must match ObjectTemperature.ApplyTemperature) ──
        const string P_INTENSITY   = "_ThermalIntensity";
        const string P_TRANSVIS    = "_ThermalTransparentVisibility";
        const string P_NOALPHA     = "_ThermalDisableAlphaBlend";
        const string P_HEATMAP     = "_ThermalHeatMap";
        const string P_HEATMAPSCL  = "_ThermalHeatMapScale";
        const string P_DISABLE     = "_ThermalDisable";
        const string P_COLORALPHA  = "_ThermalUseColorAlpha";
        const string P_VERTEXCOLOR = "_ThermalUseVertexColor";

        // ── Config ────────────────────────────────────────────────────────────────
        internal static ConfigEntry<bool>   CfgOn;
        internal static ConfigEntry<bool>   CfgKeepWarm;
        internal static ConfigEntry<float>  CfgStartTempC;
        internal static ConfigEntry<float>  CfgAmbientTempC;
        internal static ConfigEntry<float>  CfgCoolSeconds;
        internal static ConfigEntry<int>    CfgSteps;
        internal static ConfigEntry<float>  CfgCohortWindow;
        internal static ConfigEntry<int>    CfgClasses;
        internal static ConfigEntry<float>  CfgDenseLinearity;
        internal static ConfigEntry<float>  CfgSparseCoolMult;
        internal static ConfigEntry<float>  CfgFreshIntensity;
        internal static ConfigEntry<bool>   CfgUseVolumes;
        internal static ConfigEntry<int>    CfgDensityBlur;
        internal static ConfigEntry<bool>   CfgOpaqueInThermal;
        internal static ConfigEntry<float>  CfgHeatMapStrength;
        internal static ConfigEntry<string> CfgApplyMode;
        internal static ConfigEntry<float>  CfgDebugForce;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgOn = cfg.Bind("Thermal", "Thermal Blood Enabled", true,
                "Whether blood decals show up on thermal optics and cool down over time. Off = blood is invisible through thermal (vanilla behaviour) and none of this system runs.");
            CfgKeepWarm = cfg.Bind("Thermal", "Keep Blood Warm Forever", false,
                "On: blood stays at fresh body temperature for its whole lifetime and never cools. Cheapest possible mode - nothing is ever updated after spawn.");
            CfgStartTempC = cfg.Bind("Thermal", "Blood Start Temperature C", 36f,
                "Temperature blood leaves the body at, in Celsius. Purely a readability knob - H3VR has no real temperature units, this is converted to the game's arbitrary thermal scale.");
            CfgAmbientTempC = cfg.Bind("Thermal", "Ambient Temperature C", 20f,
                "Temperature blood cools toward. H3VR has NO ambient temperature of any kind - no per-map, per-area or per-surface temperature exists in the game - so this has to be set here. Once blood reaches it, it is indistinguishable from the wall it is on.");
            CfgCoolSeconds = cfg.Bind("Thermal", "Cooling Seconds", 8f,
                "How long blood takes to finish cooling and settle at the surrounding temperature. This is the WHOLE cooldown, not a half life - at this many seconds the blood is done and reads the same as the wall it is on. Replaces the old 'Cooling Half Life Seconds', which was the time to lose only half the heat and therefore took about 6x this long to actually finish.");
            CfgSteps = cfg.Bind("Thermal", "Temperature Steps", 18,
                "How many discrete temperature shades blood passes through on its way to ambient. Nothing is computed per frame - these are solved once at startup and stepped through, so raising this costs almost nothing and makes the cooldown read as a smooth fade instead of visible jumps. 2-18.");
            CfgCohortWindow = cfg.Bind("Thermal", "Cohort Window Seconds", 10f,
                "Blood spawned within this many seconds of other blood shares one cooling schedule and one set of materials. Lower = each splat cools on its own exact timeline but more materials are held at once; higher = fewer materials. This is what bounds memory as Lifetime goes up, so it is deliberately NOT tied to Temperature Steps.");
            CfgClasses = cfg.Bind("Thermal", "Curve Classes", 2,
                "How many cooling-rate groups splash dots are split into by how densely packed with blood they are. 1 = every dot cools identically and costs nothing extra. 2 = dense areas cool slower and more linearly, thin spray follows Newton and cools faster. Each class beyond 1 splits the splash mesh into more chunks (more draw calls). 1-4.");
            CfgDenseLinearity = cfg.Bind("Thermal", "Dense Linearity", 0.75f,
                "How linear the densest class's cooling curve is. 0 = pure exponential like thin blood, 1 = fully linear (a thick pool losing heat at a near-constant rate). Ignored when Curve Classes is 1.");
            CfgSparseCoolMult = cfg.Bind("Thermal", "Sparse Cool Multiplier", 1.6f,
                "How much faster the thinnest class cools than the densest. 1 = same speed. Ignored when Curve Classes is 1.");
            CfgFreshIntensity = cfg.Bind("Thermal", "Fresh Blood Thermal Intensity", 27f,
                "How bright fresh blood reads above its surroundings on thermal, in the game's own units. 27 matches a Sosig body (ObjectTemperature profile SosigBody). Raise to make fresh blood glow harder.");
            CfgUseVolumes = cfg.Bind("Thermal", "Use Temperature Volumes", true,
                "Sample any TemperatureVolume the map author placed to decide local ambient temperature, instead of always using Ambient Temperature C. Sampled once per group of blood when it spawns, never per frame. Most H3VR maps have no volumes at all, in which case this costs nothing.");
            CfgDensityBlur = cfg.Bind("Thermal", "Density Blur Radius", 4,
                "Pixel radius used when measuring how densely packed with blood each part of the source splatter PNGs is. Larger = coarser dense/thin split. Only read once at startup while building the splatter sampling table. 1-16.");
            CfgOpaqueInThermal = cfg.Bind("Thermal", "Render Blood Opaque In Thermal", false,
                "Draw blood as solid heat on thermal instead of alpha-blending it. Leave OFF: blood decals are transparent quads, so turning this on makes their fully-transparent corners draw as solid heat and every splat shows up on thermal as a big square instead of a soft round blob.");
            CfgHeatMapStrength = cfg.Bind("Thermal", "Heat Shape Strength", 1f,
                "How strongly the decal's own texture shapes its heat on thermal (feeds _ThermalHeatMap). 1 = heat falls off with the same soft round edge you see outside thermal. 0 = flat heat across the whole quad, which looks like a square. Raise above 1 to make the hot core stand out more against the faded edge.");
            CfgApplyMode = cfg.Bind("Thermal", "Apply Mode", "Material",
                "How heat is pushed to decals. Material: one write updates every decal sharing that material (fastest, default). PropertyBlock: writes each decal renderer individually via MaterialPropertyBlock, the same route the game's own ObjectTemperature uses. Only switch to PropertyBlock if blood does not show up on thermal at all in Material mode.");
            CfgDebugForce = cfg.Bind("Thermal", "Debug Force Intensity", -1f,
                "Diagnostic. -1 = off (normal cooling). 0 or higher = pin every blood decal at that thermal intensity forever, ignoring all cooling. Set to 27 to check whether blood shows up on thermal at all without waiting for or fighting the cooling curve.");
            CfgShapeMode = cfg.Bind("Thermal", "Shape Mode", "HeatMap",
                "How a decal's heat is delivered, which decides whether its square quad is visible on thermal. HeatMap: the decal sits at ambient heat and ALL of its warmth is painted through its shape texture, so the transparent corners read exactly like the wall behind them and only the round blob is hot. Intensity: the whole quad is heated and the texture only modulates it, which leaves a visible square around every splat. Use HeatMap.");
            CfgAmbientOverride = cfg.Bind("Thermal", "Thermal Ambient Intensity Override", -1f,
                "Fixes thermal showing a flat white image in custom maps. The thermal shader adds the scene's ambient light into the heat value, so a map whose Ambient Color is not black washes everything out to white. Vanilla maps ship black ambient, which is why they look right. -1 = leave the game alone. 0 = cancel the ambient contribution entirely (try this first). 1 = the game's own untouched value. This is global, not per-map. Proper fix is to set Ambient Color to black in the map's own lighting settings.");
            CfgDebugLog = cfg.Bind("Thermal", "Debug Logging", false,
                "Log the baked cooling schedule at startup, then every cooling step and every splash chunk count as they happen. For checking the system is actually working and how much it costs. Noisy - leave off for normal play.");
        }

        internal static ConfigEntry<bool>   CfgDebugLog;
        internal static ConfigEntry<float>  CfgAmbientOverride;
        internal static ConfigEntry<string> CfgShapeMode;

        // ── Baked step tables ─────────────────────────────────────────────────────
        // _stepTime[class][i] = seconds after spawn at which step i is applied
        // _heatFrac[class][i] = heat remaining at step i, 1 = fresh, 0 = ambient
        static float[][] _stepTime;
        static float[][] _heatFrac;
        static int   _classes = 1;
        static int   _steps   = 1;
        static float _quantum = 2.5f;
        static float _hotOffset;          // intensity of fresh blood above ambient
        static bool  _usePropertyBlock;
        static bool  _shapeViaHeatMap;
        static bool  _forced;             // Debug Force Intensity is active
        static float _forcedValue;
        static bool  _enabled;
        static bool  _keepWarm;

        internal static int Classes { get { return _classes; } }
        internal static bool Enabled { get { return _enabled; } }

        // ── Cohorts ───────────────────────────────────────────────────────────────
        struct MatRef
        {
            public Material  Mat;
            public MatKey    Key;
            public bool      Drip;
            public MatRef(Material mat, MatKey key, bool drip) { Mat = mat; Key = key; Drip = drip; }
        }

        class Cohort
        {
            public int   Id;
            public int   CurveClass;
            public float StartTime;
            public float RetireTime;
            public int   Step;            // index of the NEXT step to apply
            public float NextEvent;
            public float AmbIntensity;
            public bool  AmbSampled;
            public readonly List<MatRef>  Mats      = new List<MatRef>();
            public readonly List<Renderer> Renderers = new List<Renderer>();
        }

        static readonly Dictionary<int, Cohort> _cohorts = new Dictionary<int, Cohort>();
        static readonly List<Cohort> _live = new List<Cohort>();
        static readonly List<Cohort> _retire = new List<Cohort>();
        static float _nextEvent = float.MaxValue;

        static bool _armed;
        static bool _armLogged;

        // One reused block - MaterialPropertyBlock allocates, and the fallback path can touch
        // a lot of renderers in one step event.
        static MaterialPropertyBlock _mpb;

        // ── Setup ─────────────────────────────────────────────────────────────────

        internal static void Init()
        {
            _enabled  = CfgOn.Value;
            _keepWarm = CfgKeepWarm.Value;
            _usePropertyBlock = string.Equals(CfgApplyMode.Value, "PropertyBlock",
                                              System.StringComparison.OrdinalIgnoreCase);
            _shapeViaHeatMap  = !string.Equals(CfgShapeMode.Value, "Intensity",
                                               System.StringComparison.OrdinalIgnoreCase);
            _forcedValue = CfgDebugForce.Value;
            _forced      = _forcedValue >= 0f;

            _classes = Mathf.Clamp(CfgClasses.Value, 1, 4);
            _steps   = Mathf.Clamp(CfgSteps.Value,   2, 18);
            _hotOffset = CfgFreshIntensity.Value;

            // Celsius is only here so the config reads sensibly - the game has no temperature
            // units. Fresh Blood Thermal Intensity is calibrated for body heat (36C) against the
            // configured ambient, and any other start temperature scales linearly off that. Blood
            // set colder than ambient gives a negative offset, which reads as a cold spot.
            float refSpan = 36f - CfgAmbientTempC.Value;
            float span    = CfgStartTempC.Value - CfgAmbientTempC.Value;
            _hotOffset = (Mathf.Abs(refSpan) < 0.01f)
                       ? CfgFreshIntensity.Value
                       : CfgFreshIntensity.Value * (span / refSpan);

            BuildStepTables();

            // Deliberately independent of Steps. Tying the two together meant raising Steps for a
            // smoother fade also multiplied the number of live cohorts, and therefore materials,
            // for no benefit — the schedule inside a cohort can have as many steps as it likes
            // without needing more cohorts. Live cohorts are now Lifetime / window, so this is the
            // one knob that bounds memory as Lifetime grows.
            _quantum = Mathf.Max(0.25f, CfgCohortWindow.Value);

            _cohorts.Clear();
            _live.Clear();
            _nextEvent = float.MaxValue;
            _armed = false;

            BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal: enabled=" + _enabled
                + " classes=" + _classes + " steps=" + _steps
                + " quantum=" + _quantum.ToString("F2") + "s"
                + " hotOffset=" + _hotOffset.ToString("F1")
                + " mode=" + (_usePropertyBlock ? "PropertyBlock" : "Material")
                + (_keepWarm ? " KEEP-WARM" : "")
                + (_forced ? " FORCED=" + _forcedValue.ToString("F1") : ""));

            if (CfgDebugLog.Value)
            {
                for (int c = 0; c < _classes; c++)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[BloodSystem] Thermal schedule class ").Append(c)
                      .Append(c == 0 ? " (densest)" : (c == _classes - 1 ? " (thinnest)" : ""))
                      .Append(": ");
                    for (int i = 0; i < _steps; i++)
                        sb.Append(_stepTime[c][i].ToString("F2")).Append("s=")
                          .Append((_hotOffset * _heatFrac[c][i]).ToString("F1"))
                          .Append(i == _steps - 1 ? "" : "  ");
                    BloodSystemPlugin.Log.LogInfo(sb.ToString());
                }
            }
        }

        // ── Thermal render diagnostics ────────────────────────────────────────────
        //
        // Fires once per scene, the first time a thermal camera renders in it. Exists because
        // thermal renders as a flat white image in some custom maps while working normally in
        // vanilla ones, and the difference cannot be found by reading the game's code alone —
        // every candidate cause is scene state that only exists at runtime.
        //
        // The most load-bearing line is ThermalShader. PIPScope.GetThermalShader() is
        // ManagerSingleton<FXM>.Instance.ThermalShader, and SetReplacementShader(null, ...)
        // silently CLEARS the replacement shader rather than failing — the camera then renders
        // the map normally and the thermal LUT/auto-gain stage runs over an ordinary image,
        // which is a very good way to get a white screen with no error in the log.
        static readonly HashSet<string> _diagLoggedScenes = new HashSet<string>();

        internal static void LogThermalDiagnostics(Camera cam)
        {
            try
            {
                // PIPScope.thermalAmbientIntensity is a public static the game itself never
                // assigns — it sits at 1 forever and feeds _ThermalAmbientStrength = value - 1.
                // Reassert every thermal render, since scene loads can reconstruct PIPScope state.
                if (CfgAmbientOverride.Value >= 0f)
                    PIPScope.thermalAmbientIntensity = CfgAmbientOverride.Value;

                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                // Keyed on the decal source too, not just the scene: that material gets swapped
                // when the WFX bullet-hole grab fires, which usually happens after the first
                // thermal render, and its RenderType is the thing being diagnosed.
                Material src = BloodSystemPlugin._decalSourceMat;
                string key = scene + "|" + (ReferenceEquals(src, null) ? "none" : src.shader.name);
                if (!_diagLoggedScenes.Add(key)) return;

                var sb = new System.Text.StringBuilder();
                sb.Append("[BloodSystem] THERMAL DIAG scene='").Append(scene).Append("'");

                // FXM owns the thermal shader. If it is missing or its shader field is empty,
                // there is no replacement shader and everything downstream is meaningless.
                FXM fxm = ManagerSingleton<FXM>.Instance;
                if (fxm == null)
                {
                    sb.Append(" | FXM=MISSING (no thermal shader available)");
                }
                else
                {
                    Shader ts = fxm.ThermalShader;
                    sb.Append(" | FXM=ok ThermalShader=")
                      .Append(ReferenceEquals(ts, null) ? "NULL" : ts.name)
                      .Append(ReferenceEquals(ts, null) ? "" : (ts.isSupported ? " (supported)" : " (NOT SUPPORTED)"));
                }

                if (!ReferenceEquals(cam, null))
                {
                    sb.Append(" | cam='").Append(cam.name).Append("'")
                      .Append(" clear=").Append(cam.clearFlags)
                      .Append(" bg=").Append(cam.backgroundColor)
                      .Append(" path=").Append(cam.actualRenderingPath);
                }

                // Heat sources present in the scene. Sosigs contribute one ObjectTemperature per
                // body link, so spawning one and seeing this number not move means the Sosig
                // never registered.
                sb.Append(" | ObjectTemperatures=")
                  .Append(ObjectTemperature.allTemperatureObjects == null
                          ? -1 : ObjectTemperature.allTemperatureObjects.Count);
                sb.Append(" TemperatureVolumes=")
                  .Append(TemperatureVolume.volumes == null ? -1 : TemperatureVolume.volumes.Count);

                // RenderType is what SetReplacementShader(shader, "RenderType") matches on, so it
                // decides whether blood is drawn with alpha or as a solid quad. Opaque here means
                // a square around every dot no matter what the heat map says.
                Material dsm = BloodSystemPlugin._decalSourceMat;
                sb.Append(" | decalSrc=")
                  .Append(ReferenceEquals(dsm, null)
                          ? "none"
                          : dsm.shader.name + " RenderType=" + dsm.GetTag("RenderType", false, "<none>"));

                sb.Append(" | ambientLight=").Append(RenderSettings.ambientLight)
                  .Append(" ambientIntensity=").Append(RenderSettings.ambientIntensity.ToString("F2"));

                BloodSystemPlugin.Log.LogInfo(sb.ToString());
            }
            catch (System.Exception ex)
            {
                BloodSystemPlugin.Log.LogWarning("[BloodSystem] THERMAL DIAG failed: " + ex.Message);
            }
        }

        // Diagnostic counters — only touched when Debug Logging is on.
        static int _dbgSteps, _dbgWrites;
        internal static void DebugNoteChunks(int chunkCount, int dotCount)
        {
            if (!CfgDebugLog.Value) return;
            BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal: splash built " + chunkCount
                + " chunk(s) for " + dotCount + " dots (live cohorts=" + _live.Count
                + " materials=" + BloodSystemPlugin._matCache.Count + ")");
        }

        // Solves Newton's law of cooling backwards, once, at startup.
        //
        // Newton:  f(t) = exp(-k t),  k = ln2 / halfLife,  f = fraction of the original heat
        // difference above ambient that is still there.
        //
        // A dense pool of blood does not behave like that - it has far more thermal mass than its
        // surface area, so it sheds heat at a much flatter, closer-to-constant rate. That is
        // blended in as a straight line to ambient over the same settling time.
        //
        // Instead of stepping time and asking what the temperature is (which is the per-frame
        // simulation we are avoiding), we step TEMPERATURE in equal shades and solve for the time
        // each shade is reached. That naturally produces short early steps and long late ones,
        // which is what "cools fast then levels off" actually looks like.
        static void BuildStepTables()
        {
            _stepTime = new float[_classes][];
            _heatFrac = new float[_classes][];

            float baseTau = Mathf.Max(0.25f, CfgCoolSeconds.Value);

            for (int c = 0; c < _classes; c++)
            {
                // u: 0 = densest class, 1 = thinnest
                float u = (_classes == 1) ? 0f : (float)c / (_classes - 1);
                float linearity = Mathf.Clamp01(CfgDenseLinearity.Value) * (1f - u);
                // Thin blood finishes sooner, not merely faster at the start - so the multiplier
                // shortens its whole schedule rather than only steepening its curve.
                float tau = baseTau / Mathf.Lerp(1f, Mathf.Max(0.05f, CfgSparseCoolMult.Value), u);
                // exp(-4) = 0.018, i.e. ~98% cooled at tau, which is close enough to call settled.
                float k = 4f / tau;

                _stepTime[c] = new float[_steps];
                _heatFrac[c] = new float[_steps];

                // Steps are spaced evenly in TIME, and the temperature at each is read off the
                // curve. The reverse - evenly spaced temperatures, solving for when each is
                // reached - is what this used to do, and it behaves badly: on an exponential the
                // times come out logarithmic, so the first half of the steps land in the first
                // ~15% of the schedule where the blood is still near max heat and every shade
                // looks identical, while the visible second half is left stretched over many
                // seconds apiece. That reads as "nothing happens for ages, then huge jumps".
                //
                // Even time spacing inverts that: the big temperature drops happen early, while
                // the blood is too hot to distinguish anyway, and the late steps are small and
                // regular so it settles smoothly. Newton's law still sets every value; only the
                // sampling changed.
                for (int i = 0; i < _steps; i++)
                {
                    float t = tau * i / (_steps - 1);
                    _stepTime[c][i] = t;
                    _heatFrac[c][i] = (i == _steps - 1) ? 0f : CoolCurve(t, k, tau, linearity);
                }
            }
        }

        static float CoolCurve(float t, float k, float tau, float linearity)
        {
            float expPart = Mathf.Exp(-k * t);
            float linPart = Mathf.Max(0f, 1f - t / tau);
            return Mathf.Lerp(expPart, linPart, linearity);
        }

        // (The old SolveTime bisection is gone with the equal-temperature spacing it existed to
        // support - times now come straight from the schedule, so nothing has to be solved for.)

        // ── Cohort lookup ─────────────────────────────────────────────────────────

        // Returns the cohort id blood spawning right now with this curve class belongs to.
        // Returns 0 when thermal is off so the material cache behaves exactly as it did before
        // this system existed - one entry per colour, no extra materials, no eviction.
        internal static int CurrentCohort(int curveClass)
        {
            if (!_enabled) return 0;
            if (curveClass < 0) curveClass = 0;
            if (curveClass >= _classes) curveClass = _classes - 1;

            int quantumIndex = Mathf.FloorToInt(Time.time / _quantum);
            int id = quantumIndex * 4 + curveClass;   // 4 = max classes, keeps ids unique

            Cohort co;
            if (!_cohorts.TryGetValue(id, out co))
            {
                co = new Cohort
                {
                    Id         = id,
                    CurveClass = curveClass,
                    StartTime  = quantumIndex * _quantum,
                    Step       = 0,
                    AmbSampled = false,
                };
                // Last decal in this window despawns Lifetime after the window closes; the extra
                // second is slack so a material is never destroyed out from under a live decal.
                co.RetireTime = co.StartTime + _quantum + BloodSystemPlugin.CfgLifetime.Value + 1f;
                // Step 0 is applied at creation, so the first scheduled event is step 1. In
                // keep-warm / forced modes the only event a cohort ever has is its own retirement.
                co.NextEvent  = (_keepWarm || _forced)
                              ? co.RetireTime
                              : co.StartTime + _stepTime[curveClass][1];
                _cohorts[id] = co;
                _live.Add(co);
                if (co.NextEvent < _nextEvent) _nextEvent = co.NextEvent;
            }
            return id;
        }

        // ── Registration ──────────────────────────────────────────────────────────

        // Called from GetBloodMat/GetDripMat right after a new Material is created and cached.
        // Sets the constant thermal properties, and the current heat if a thermal camera has
        // already been seen this session.
        internal static void RegisterMaterial(Material m, MatKey key, bool drip)
        {
            if (!_enabled || ReferenceEquals(m, null)) return;

            ApplyShapeProps(m);

            Cohort co;
            if (!_cohorts.TryGetValue(key.Cohort, out co)) return;
            co.Mats.Add(new MatRef(m, key, drip));

            if (_forced)                  WriteHeat(m, _forcedValue, co.AmbIntensity);
            else if (_armed || _keepWarm) WriteHeat(m, IntensityAt(co, co.Step), co.AmbIntensity);
        }

        // Airborne blood — spray particles, in-flight dots, falling drops. It is only alive for a
        // few seconds and is still fresh out of the body the whole time, so it is pinned hot and
        // never cooled. One write, at material creation, forever.
        internal static void MarkAlwaysHot(Material m)
        {
            if (!_enabled || ReferenceEquals(m, null)) return;
            ApplyShapeProps(m);
            // Airborne blood has no cohort, so ambient is the global baseline of 0.
            WriteHeat(m, _forced ? _forcedValue : _hotOffset, 0f);
        }

        // The constant, non-temperature half of the thermal property set — the part that decides
        // what SHAPE the heat is, as opposed to how hot it is.
        //
        // A blood decal is a quad; its round soft-edged shape lives entirely in its texture's
        // alpha. Outside thermal that works because the decal's own shader samples that texture.
        // Inside thermal it does NOT: the game swaps in a replacement shader (Hidden/Thermal) and
        // the decal's own shader never runs, so the replacement shader only knows what we hand it
        // through these properties. Handing it a flat white heat map with scale 0 gave every decal
        // uniform heat across its whole quad — i.e. a big square, which is exactly what showed up
        // on thermal while the same decal looked like a soft circle outside it.
        //
        // Fix: feed the decal's own shape texture in as _ThermalHeatMap, which is precisely what
        // that property is for (ObjectTemperature exposes it as thermalTexture). Heat then falls
        // off with the same gaussian the visible decal uses, so the hot area is the blood, not the
        // quad. Alpha blending is left ON for the same reason — rendering transparents as opaque
        // re-squares the decal by making the fully-transparent corners draw as solid heat.
        // Maps a decal's visible texture to an RGB-encoded copy of its shape. Needed because the
        // thermal shader samples _ThermalHeatMap as RGB, while the procedural circle textures
        // carry their shape in alpha only with RGB left pure white.
        static readonly Dictionary<Texture, Texture> _heatMasks = new Dictionary<Texture, Texture>();

        internal static void RegisterHeatMask(Texture visible, Texture heat)
        {
            if (ReferenceEquals(visible, null) || ReferenceEquals(heat, null)) return;
            _heatMasks[visible] = heat;
        }

        static Texture HeatMaskFor(Texture visible)
        {
            if (ReferenceEquals(visible, null)) return null;
            Texture heat;
            return _heatMasks.TryGetValue(visible, out heat) ? heat : visible;
        }

        static void ApplyShapeProps(Material m)
        {
            Texture shape = HeatMaskFor(m.mainTexture);
            bool hasShape = !ReferenceEquals(shape, null);

            m.SetFloat(P_TRANSVIS,    1f);   // decals are transparent quads; thermal hides those by default
            m.SetFloat(P_NOALPHA,     CfgOpaqueInThermal.Value ? 1f : 0f);
            m.SetFloat(P_DISABLE,     0f);
            m.SetFloat(P_COLORALPHA,  0f);
            m.SetFloat(P_VERTEXCOLOR, 0f);
            m.SetTexture(P_HEATMAP,   hasShape ? shape : Texture2D.whiteTexture);
            // In HeatMap mode the scale carries the actual temperature and is written per cooling
            // step by WriteHeat, so it is deliberately not set here.
            if (!_shapeViaHeatMap)
                m.SetFloat(P_HEATMAPSCL, hasShape ? Mathf.Max(0f, CfgHeatMapStrength.Value) : 0f);
        }

        // Heat is delivered through one of two channels.
        //
        // Intensity mode heats the entire quad and lets the shape texture modulate it, which
        // leaves the decal's transparent corners hotter than the wall — a visible square.
        //
        // HeatMap mode pins the quad itself at ambient and paints all the warmth through the
        // shape texture instead, so the corners read exactly like the surface behind them and
        // only the round blob is hot. That is the mode that makes a splat look like a splat.
        static void WriteHeat(Material m, float v, float amb)
        {
            if (_shapeViaHeatMap)
            {
                m.SetFloat(P_INTENSITY,  amb);
                m.SetFloat(P_HEATMAPSCL, (v - amb) * Mathf.Max(0f, CfgHeatMapStrength.Value));
            }
            else
            {
                m.SetFloat(P_INTENSITY, v);
            }
        }

        // Only does anything in the PropertyBlock fallback mode. Called from every decal spawn
        // site so switching Apply Mode needs no rebuild.
        internal static void RegisterRenderer(Renderer r, int cohortId, Vector3 worldPos)
        {
            if (!_enabled || ReferenceEquals(r, null)) return;
            NoteSpawnPosition(cohortId, worldPos);
            if (!_usePropertyBlock) return;
            Cohort co;
            if (!_cohorts.TryGetValue(cohortId, out co)) return;
            co.Renderers.Add(r);
            if (_forced)                  ApplyToRenderer(r, _forcedValue, co.AmbIntensity);
            else if (_armed || _keepWarm) ApplyToRenderer(r, IntensityAt(co, co.Step), co.AmbIntensity);
        }

        // Cohort ambient is sampled from the first blood placed in it. Cohorts are per-shot-ish,
        // so everything in one is within a few metres and shares surroundings.
        internal static void NoteSpawnPosition(int cohortId, Vector3 worldPos)
        {
            if (!_enabled) return;
            Cohort co;
            if (_cohorts.TryGetValue(cohortId, out co) && !co.AmbSampled) SampleAmbient(co, worldPos);
        }

        // ── Ambient ───────────────────────────────────────────────────────────────

        // H3VR has no ambient temperature. The only thing resembling one is TemperatureVolume, a
        // scene-authored box/sphere that feeds the thermal shader through global arrays. Nothing
        // in the game reads it back, and most maps have none - hence the Count check, which is the
        // entire cost in the normal case.
        static void SampleAmbient(Cohort co, Vector3 worldPos)
        {
            co.AmbSampled   = true;
            co.AmbIntensity = 0f;
            if (!CfgUseVolumes.Value) return;

            var vols = TemperatureVolume.volumes;
            if (vols == null || vols.Count == 0) return;

            float best = 0f;
            for (int i = 0; i < vols.Count; i++)
            {
                var v = vols[i];
                if (ReferenceEquals(v, null)) continue;
                Vector3 d = worldPos - v.transform.position;
                bool inside;
                if (v.spherical)
                {
                    float rad = Mathf.Max(v.size.x, Mathf.Max(v.size.y, v.size.z)) + v.blendDistance;
                    inside = d.sqrMagnitude <= rad * rad;
                }
                else
                {
                    inside = Mathf.Abs(d.x) <= v.size.x + v.blendDistance
                          && Mathf.Abs(d.y) <= v.size.y + v.blendDistance
                          && Mathf.Abs(d.z) <= v.size.z + v.blendDistance;
                }
                if (inside && Mathf.Abs(v.thermalIntensity) > Mathf.Abs(best)) best = v.thermalIntensity;
            }
            co.AmbIntensity = best;
        }

        static float IntensityAt(Cohort co, int step)
        {
            if (step >= _steps) step = _steps - 1;
            return co.AmbIntensity + _hotOffset * _heatFrac[co.CurveClass][step];
        }

        // ── Arming ────────────────────────────────────────────────────────────────

        // Called from the Harmony postfix on PIPScope.ApplyCameraShader the first time any thermal
        // camera renders - scope optic or handheld thermal cam, both route through it. Until this
        // fires, not a single shader property is written.
        internal static void Arm()
        {
            if (_armed || !_enabled) return;
            _armed = true;
            if (!_armLogged)
            {
                _armLogged = true;
                BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal armed - a thermal camera rendered, blood heat is now being applied.");
            }
            // Catch up everything that spawned while unarmed.
            for (int i = 0; i < _live.Count; i++) ApplyCohort(_live[i]);
        }

        // ── Per-frame ─────────────────────────────────────────────────────────────

        // The whole runtime cost of this system. On nearly every frame it is one float compare.
        // Cohorts still advance and retire while unarmed - they just don't write anything - so a
        // player who never touches a thermal optic doesn't accumulate dead cohorts.
        internal static void Tick()
        {
            if (!_enabled) return;
            float now = Time.time;
            if (now < _nextEvent) return;

            float next = float.MaxValue;
            _retire.Clear();

            for (int i = 0; i < _live.Count; i++)
            {
                Cohort co = _live[i];

                if (now >= co.RetireTime) { _retire.Add(co); continue; }

                bool stepped = false;
                while (co.Step < _steps - 1 && now >= co.NextEvent)
                {
                    co.Step++;
                    stepped = true;
                    co.NextEvent = (co.Step < _steps - 1)
                                 ? co.StartTime + _stepTime[co.CurveClass][co.Step + 1]
                                 : co.RetireTime;
                }
                if (stepped && _armed)
                {
                    ApplyCohort(co);
                    if (CfgDebugLog.Value)
                    {
                        _dbgSteps++;
                        BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal step: cohort " + co.Id
                            + " class " + co.CurveClass
                            + " step " + co.Step + "/" + (_steps - 1)
                            + " intensity=" + IntensityAt(co, co.Step).ToString("F1")
                            + " wrote " + (_usePropertyBlock ? co.Renderers.Count : co.Mats.Count)
                            + " (session: " + _dbgSteps + " steps, " + _dbgWrites + " writes)");
                    }
                }

                if (co.NextEvent < next) next = co.NextEvent;
            }

            for (int i = 0; i < _retire.Count; i++) Retire(_retire[i]);
            _retire.Clear();

            _nextEvent = next;
        }

        static void ApplyCohort(Cohort co)
        {
            float v = _forced ? _forcedValue : IntensityAt(co, co.Step);

            if (_usePropertyBlock)
            {
                for (int i = co.Renderers.Count - 1; i >= 0; i--)
                {
                    Renderer r = co.Renderers[i];
                    if (r == null) { co.Renderers.RemoveAt(i); continue; }
                    ApplyToRenderer(r, v, co.AmbIntensity);
                    _dbgWrites++;
                }
            }
            else
            {
                for (int i = 0; i < co.Mats.Count; i++)
                {
                    Material m = co.Mats[i].Mat;
                    if (!ReferenceEquals(m, null)) { WriteHeat(m, v, co.AmbIntensity); _dbgWrites++; }
                }
            }
        }

        static void ApplyToRenderer(Renderer r, float v, float amb)
        {
            // Same shape reasoning as ApplyShapeProps — the decal's own texture is what makes the
            // heat round instead of a square quad.
            Material sm = r.sharedMaterial;
            Texture shape = ReferenceEquals(sm, null) ? null : HeatMaskFor(sm.mainTexture);
            bool hasShape = !ReferenceEquals(shape, null);

            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.Clear();
            r.GetPropertyBlock(_mpb);
            _mpb.SetFloat(P_TRANSVIS,     1f);
            _mpb.SetFloat(P_NOALPHA,      CfgOpaqueInThermal.Value ? 1f : 0f);
            _mpb.SetFloat(P_DISABLE,      0f);
            _mpb.SetFloat(P_COLORALPHA,   0f);
            _mpb.SetFloat(P_VERTEXCOLOR,  0f);
            _mpb.SetTexture(P_HEATMAP,    hasShape ? shape : Texture2D.whiteTexture);

            // Same two channels as WriteHeat, on the property-block route.
            if (_shapeViaHeatMap && hasShape)
            {
                _mpb.SetFloat(P_INTENSITY,  amb);
                _mpb.SetFloat(P_HEATMAPSCL, (v - amb) * Mathf.Max(0f, CfgHeatMapStrength.Value));
            }
            else
            {
                _mpb.SetFloat(P_INTENSITY,  v);
                _mpb.SetFloat(P_HEATMAPSCL, hasShape ? Mathf.Max(0f, CfgHeatMapStrength.Value) : 0f);
            }
            r.SetPropertyBlock(_mpb);
        }

        // Every decal that could reference this cohort's materials is already gone by RetireTime,
        // so the materials can be destroyed. Without this the cache would grow forever, since its
        // key now includes the cohort id.
        static void Retire(Cohort co)
        {
            for (int i = 0; i < co.Mats.Count; i++)
            {
                MatRef mr = co.Mats[i];
                if (mr.Drip) BloodSystemPlugin._dripMatCache.Remove(mr.Key);
                else         BloodSystemPlugin._matCache.Remove(mr.Key);
                if (!ReferenceEquals(mr.Mat, null)) UnityEngine.Object.Destroy(mr.Mat);
            }
            co.Mats.Clear();
            co.Renderers.Clear();
            _cohorts.Remove(co.Id);
            _live.Remove(co);
        }
    }

    // Material cache key. Colour alone is no longer enough - two decals of the same colour
    // spawned at different times are at different temperatures, so they need different materials.
    // IEquatable + explicit GetHashCode so Dictionary lookups don't box on every call.
    internal struct MatKey : System.IEquatable<MatKey>
    {
        public readonly Color Col;
        public readonly int   Cohort;
        public MatKey(Color col, int cohort) { Col = col; Cohort = cohort; }

        public bool Equals(MatKey o) { return Cohort == o.Cohort && Col.Equals(o.Col); }
        public override bool Equals(object o) { return o is MatKey && Equals((MatKey)o); }
        public override int GetHashCode()
        {
            int h = Col.GetHashCode();
            return (h * 397) ^ Cohort;
        }
    }
}
