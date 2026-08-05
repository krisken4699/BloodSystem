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
        // Hard ceiling on curve classes. Also the stride used to build cohort ids, so it must
        // never be lowered below the configured class count.
        const int MAX_CLASSES = 20;

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
        //
        // ONE user-facing option. Everything below it is a tuned constant, on purpose: these
        // values were arrived at together by testing, they interact (the cohort window only works
        // while it stays well under the cooldown; the step count only reads smoothly because the
        // shades are spaced by temperature; the tail floor only matters because the curve is
        // exponential), and exposing them invites combinations that look worse than the default
        // with no way for a player to know why.
        internal static ConfigEntry<bool> CfgAnimate;

        internal static void BindConfig(ConfigFile cfg)
        {
            CfgAnimate = cfg.Bind("Thermal", "Animate Cooldown", true,
                "Whether blood cools down on thermal optics. On: blood leaves the body hot and fades to the temperature of its surroundings over about eight seconds, thicker pooled areas holding their heat longer than scattered spray. Off: blood still shows up on thermal at body temperature but stays there for as long as the decal lasts, and none of the cooling machinery runs at all.");
        }

        // ── Tuned constants ───────────────────────────────────────────────────────
        const float  COOL_SECONDS     = 8f;    // whole cooldown, fresh to surroundings
        const int    STEPS            = 80;    // shades on the way down, spaced evenly in temperature
        const float  COHORT_WINDOW    = 0.5f;  // blood within this long shares a schedule
        const int    CLASSES          = 8;     // density groups; draw calls are ~10x this per shot
        const float  FRAGMENT_WEIGHT  = 1f;    // how much scattered blood counts as thinner
        const bool   STAGGER          = true;  // groups cool in a wave, not as one block
        const bool   STAGGER_OUTWARD  = false; // dense middle leads the wave
        const float  DENSE_LINEARITY  = 0.75f; // thickest group's curve, 1 = straight line
        const float  TAIL_LINEARITY   = 0.3f;  // keeps the last shade from lingering
        const float  SPARSE_COOL_MULT = 1.6f;  // thinnest group finishes this much sooner
        const float  FRESH_INTENSITY  = 27f;   // matches ObjectTemperature's SosigBody
        const float  START_TEMP_C     = 36f;
        const float  AMBIENT_TEMP_C   = 20f;
        const bool   USE_VOLUMES      = true;  // honour map-authored TemperatureVolumes
        const int    DENSITY_BLUR     = 4;     // floor on the thickness-measuring radius, in pixels
        const float  DENSITY_BLUR_FRACTION = 0.03f; // radius as a share of the image's short side;
                                               // must exceed droplet size or a lone droplet reads solid
        const bool   OPAQUE_IN_THERMAL = false; // true re-squares the decals
        const float  HEATMAP_STRENGTH = 1f;
        const bool   SHAPE_VIA_HEATMAP = true;  // paint heat through the shape texture
        const bool   USE_PROPERTY_BLOCK = false; // Material path; confirmed working in-game

        // Diagnostics. Flip in source when investigating; deliberately not player-facing.
        const bool   DEBUG_LOG        = false;
        const float  DEBUG_FORCE      = -1f;   // >= 0 pins every decal at that intensity
        const float  AMBIENT_OVERRIDE = -1f;   // >= 0 overrides PIPScope.thermalAmbientIntensity

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

        // Number of density groups baked into the sampling table at startup. Fixed, because the
        // table is built once and cannot be rebuilt when a thermal optic later appears.
        internal static int Classes { get { return _classes; } }

        // Number of groups the splash mesh is actually SPLIT into right now.
        //
        // Splitting costs real money: BuildDotMesh emits one chunk - GameObject, mesh, renderer,
        // material - per brightness level per class, so 8 classes is about 80 chunks a shot
        // against 10, and with 20 shot groups retained that is 1600 renderers instead of 200.
        // The split only buys anything when something can actually SEE the groups cool at
        // different rates, so until a thermal camera has rendered even once it is pure waste and
        // every dot goes in one group.
        //
        // Blood already on the wall when a thermal optic first comes out keeps whatever grouping
        // it was built with; only later blood gets the full split. That is the right trade - the
        // alternative is charging every player who never touches thermal, forever, on the chance
        // that one day they might.
        internal static int ActiveClasses { get { return _armed ? _classes : 1; } }

        // Each image's densities are rescaled against its own range before banding, so its
        // thickest areas always become the slowest-cooling group and its scattered edges the
        // fastest. Fixed rather than optional: thickness is read from image alpha, and the same
        // splatter drawn at a lower opacity reads thinner everywhere while being structurally
        // identical - so any absolute threshold would need retuning per image, and these PNGs are
        // meant to be swapped by dropping new files into the plugin folder.
        internal const bool NormalizeDensity = true;

        internal const float FragmentWeight = FRAGMENT_WEIGHT;
        internal const int   DensityBlur    = DENSITY_BLUR;
        internal const float DensityBlurFraction = DENSITY_BLUR_FRACTION;
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
        static readonly List<Cohort> _live   = new List<Cohort>(); // still cooling
        static readonly List<Cohort> _done   = new List<Cohort>(); // cooled, holding materials
        static readonly List<Cohort> _retire = new List<Cohort>(); // scratch, cleared each use
        static float _nextEvent = float.MaxValue;

        static bool _armed;
        static bool _armLogged;

        // One reused block - MaterialPropertyBlock allocates, and the fallback path can touch
        // a lot of renderers in one step event.
        static MaterialPropertyBlock _mpb;

        // ── Setup ─────────────────────────────────────────────────────────────────

        internal static void Init()
        {
            _enabled  = true;
            _keepWarm = !CfgAnimate.Value;
            _usePropertyBlock = USE_PROPERTY_BLOCK;
            _shapeViaHeatMap  = SHAPE_VIA_HEATMAP;
            _forcedValue = DEBUG_FORCE;
            _forced      = _forcedValue >= 0f;

            // With the cooldown off every dot holds the same constant heat forever, so splitting
            // the splash mesh by density would only cost draw calls - one chunk per brightness per
            // class - to describe a difference that never appears. Collapse to a single class.
            _classes = _keepWarm ? 1 : Mathf.Clamp(CLASSES, 1, MAX_CLASSES);
            _steps   = Mathf.Clamp(STEPS,   2, 160);
            _hotOffset = FRESH_INTENSITY;

            // Celsius is only here so the config reads sensibly - the game has no temperature
            // units. Fresh Blood Thermal Intensity is calibrated for body heat (36C) against the
            // configured ambient, and any other start temperature scales linearly off that. Blood
            // set colder than ambient gives a negative offset, which reads as a cold spot.
            float refSpan = 36f - AMBIENT_TEMP_C;
            float span    = START_TEMP_C - AMBIENT_TEMP_C;
            _hotOffset = (Mathf.Abs(refSpan) < 0.01f)
                       ? FRESH_INTENSITY
                       : FRESH_INTENSITY * (span / refSpan);

            BuildStepTables();

            // Deliberately independent of Steps. Tying the two together meant raising Steps for a
            // smoother fade also multiplied the number of live cohorts, and therefore materials,
            // for no benefit — the schedule inside a cohort can have as many steps as it likes
            // without needing more cohorts. Live cohorts are now Lifetime / window, so this is the
            // one knob that bounds memory as Lifetime grows.
            _quantum = Mathf.Max(0.25f, COHORT_WINDOW);

            _cohorts.Clear();
            _live.Clear();
            _done.Clear();
            _nextEvent = float.MaxValue;
            _armed = false;

            BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal: enabled=" + _enabled
                + " classes=" + _classes + " steps=" + _steps
                + " quantum=" + _quantum.ToString("F2") + "s"
                + " hotOffset=" + _hotOffset.ToString("F1")
                + " mode=" + (_usePropertyBlock ? "PropertyBlock" : "Material")
                + (_keepWarm ? " KEEP-WARM" : "")
                + (_forced ? " FORCED=" + _forcedValue.ToString("F1") : ""));

            if (DEBUG_LOG)
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
                if (AMBIENT_OVERRIDE >= 0f)
                    PIPScope.thermalAmbientIntensity = AMBIENT_OVERRIDE;

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
            if (!DEBUG_LOG) return;
            BloodSystemPlugin.Log.LogInfo("[BloodSystem] Thermal: splash built " + chunkCount
                + " chunk(s) for " + dotCount + " dots (cooling=" + _live.Count + " cooled=" + _done.Count
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

            float baseTau = Mathf.Max(0.25f, COOL_SECONDS);

            for (int c = 0; c < _classes; c++)
            {
                // u: 0 = densest class, 1 = thinnest
                float u = (_classes == 1) ? 0f : (float)c / (_classes - 1);
                // Blends from the dense class's linearity down to a FLOOR rather than to zero.
                // A pure exponential barely moves as it nears ambient - its slope at tau is only
                // -0.009/s - so the final shade before cold used to hang for ~1.7s while every
                // other step landed inside 0.9s. A straight line descends at a constant rate, so
                // a small amount of it mixed in keeps the tail moving: slope at tau goes to about
                // -0.027/s and the last gap drops to ~0.6s. It only alters the last few percent of
                // the schedule, where the blood is nearly indistinguishable from the wall anyway.
                float denseLin = Mathf.Clamp01(DENSE_LINEARITY);
                float tailLin  = Mathf.Clamp01(TAIL_LINEARITY);
                float linearity = Mathf.Lerp(tailLin, Mathf.Max(denseLin, tailLin), 1f - u);
                // Thin blood finishes sooner, not merely faster at the start - so the multiplier
                // shortens its whole schedule rather than only steepening its curve.
                float tau = baseTau / Mathf.Lerp(1f, Mathf.Max(0.05f, SPARSE_COOL_MULT), u);
                // exp(-4) = 0.018, i.e. ~98% cooled at tau, which is close enough to call settled.
                float k = 4f / tau;

                // Stagger: shift each class's whole schedule by an equal slice of one step, so the
                // density groups update one after another in a wave instead of the entire splat
                // changing shade in the same instant. The slice is 1/classes of this class's own
                // average step, which is what makes the cadence seamless - by the time the last
                // group has updated, the first is due again with exactly that same gap, so the
                // splat is always mid-transition somewhere and never flips as a block.
                float phase = 0f;
                if (STAGGER && _classes > 1)
                {
                    int order = STAGGER_OUTWARD ? (_classes - 1 - c) : c;
                    phase = (tau / (_steps - 1)) * order / _classes;
                }

                _stepTime[c] = new float[_steps];
                _heatFrac[c] = new float[_steps];

                // Steps are spaced evenly in TEMPERATURE, solving for when each shade is reached.
                // What you actually see is the contrast between the blood and the surface behind
                // it, and that is proportional to heat above ambient - so equal heat intervals are
                // equal visual intervals, and the fade reads as one steady cadence.
                //
                // Spacing evenly in time instead makes the early drops several times larger than
                // the late ones (5.6 heat units down to 0.2 at 18 steps), which is what still felt
                // chunky. This was also the original spacing and was abandoned for the opposite
                // problem - the times come out logarithmic, and back when the schedule ran 46s the
                // final gaps were over ten seconds each. At an 8s schedule the worst gap is well
                // under a second, so the reason for avoiding it is gone.
                for (int i = 0; i < _steps; i++)
                {
                    float frac = 1f - (float)i / (_steps - 1);
                    _heatFrac[c][i] = frac;
                    _stepTime[c][i] = (i == 0) ? 0f : SolveTime(frac, k, tau, linearity) + phase;
                }
            }
        }

        // Newton's exponential never actually reaches ambient, so the schedule used to clamp the
        // final step to zero. That clamp was the "grey then suddenly black" jump: at tau the raw
        // curve still sits at exp(-4) = 0.018, which is ~0.6 heat units, while the steps just
        // before it are only 0.2-0.3 apart - so the last step was about three times every other.
        //
        // Rebasing the curve so it passes through zero at tau removes the discontinuity entirely
        // instead of hiding it: the shape is untouched, the tail just lands where it claims to.
        static float CoolCurve(float t, float k, float tau, float linearity)
        {
            if (t >= tau) return 0f;
            float raw  = Mathf.Lerp(Mathf.Exp(-k * t), Mathf.Max(0f, 1f - t / tau), linearity);
            float floorAtTau = Mathf.Lerp(Mathf.Exp(-k * tau), 0f, linearity);
            return Mathf.Clamp01((raw - floorAtTau) / Mathf.Max(1e-4f, 1f - floorAtTau));
        }

        // When is this shade reached? Bisection - the curve is monotonically decreasing, so it
        // always converges. Runs Steps * Classes times at startup and never again, which is the
        // whole point: the cost of a smoother fade is paid once during load, not per frame.
        static float SolveTime(float targetFrac, float k, float tau, float linearity)
        {
            if (targetFrac <= 0f) return tau;
            float lo = 0f, hi = tau;
            for (int it = 0; it < 40; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (CoolCurve(mid, k, tau, linearity) > targetFrac) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

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
            // Must be the hard class cap, not the configured count: a smaller stride makes ids
            // from different time windows collide, silently merging blood spawned seconds apart
            // into one cohort. This was hardcoded to 4 while the cap was 4, so raising the cap
            // would have broken it.
            int id = quantumIndex * MAX_CLASSES + curveClass;

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
            m.SetFloat(P_NOALPHA,     OPAQUE_IN_THERMAL ? 1f : 0f);
            m.SetFloat(P_DISABLE,     0f);
            m.SetFloat(P_COLORALPHA,  0f);
            m.SetFloat(P_VERTEXCOLOR, 0f);
            m.SetTexture(P_HEATMAP,   hasShape ? shape : Texture2D.whiteTexture);
            // In HeatMap mode the scale carries the actual temperature and is written per cooling
            // step by WriteHeat, so it is deliberately not set here.
            if (!_shapeViaHeatMap)
                m.SetFloat(P_HEATMAPSCL, hasShape ? Mathf.Max(0f, HEATMAP_STRENGTH) : 0f);
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
                m.SetFloat(P_HEATMAPSCL, (v - amb) * Mathf.Max(0f, HEATMAP_STRENGTH));
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
            if (!USE_VOLUMES) return;

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
            for (int i = 0; i < _done.Count; i++) ApplyCohort(_done[i]);
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

            // Only cohorts that still have steps left are walked here. Finished ones are moved to
            // _done, which is scanned separately for material destruction - otherwise this loop
            // would grow with the decal Lifetime (every cohort is kept alive until its decals
            // die, long after it stopped cooling) instead of with the far shorter cooling time.
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Cohort co = _live[i];

                if (now >= co.RetireTime) { _live.RemoveAt(i); _retire.Add(co); continue; }

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
                    if (DEBUG_LOG)
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

                // Done cooling: park it until its decals expire so it stops costing anything.
                if (co.Step >= _steps - 1)
                {
                    _live.RemoveAt(i);
                    _done.Add(co);
                    if (co.RetireTime < next) next = co.RetireTime;
                    continue;
                }

                if (co.NextEvent < next) next = co.NextEvent;
            }

            // Created in time order, so retire times are ascending and only the head can be due.
            while (_done.Count > 0 && now >= _done[0].RetireTime)
            {
                _retire.Add(_done[0]);
                _done.RemoveAt(0);
            }
            if (_done.Count > 0 && _done[0].RetireTime < next) next = _done[0].RetireTime;

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
            _mpb.SetFloat(P_NOALPHA,      OPAQUE_IN_THERMAL ? 1f : 0f);
            _mpb.SetFloat(P_DISABLE,      0f);
            _mpb.SetFloat(P_COLORALPHA,   0f);
            _mpb.SetFloat(P_VERTEXCOLOR,  0f);
            _mpb.SetTexture(P_HEATMAP,    hasShape ? shape : Texture2D.whiteTexture);

            // Same two channels as WriteHeat, on the property-block route.
            if (_shapeViaHeatMap && hasShape)
            {
                _mpb.SetFloat(P_INTENSITY,  amb);
                _mpb.SetFloat(P_HEATMAPSCL, (v - amb) * Mathf.Max(0f, HEATMAP_STRENGTH));
            }
            else
            {
                _mpb.SetFloat(P_INTENSITY,  v);
                _mpb.SetFloat(P_HEATMAPSCL, hasShape ? Mathf.Max(0f, HEATMAP_STRENGTH) : 0f);
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
            // Retire is only ever reached via the Tick lists, which have already removed it from
            // whichever one held it; these are belt-and-braces for any future direct caller.
            _live.Remove(co);
            _done.Remove(co);
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
