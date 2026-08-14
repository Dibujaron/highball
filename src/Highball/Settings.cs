using UnityModManagerNet;

namespace Highball
{
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        internal static Settings Instance;

        // --- eligibility ---

        /// <summary>Cars closer than this to the camera are never touched.</summary>
        [Draw("Min distance (m)", Type = DrawType.Slider, Min = 100, Max = 3000,
              Precision = 0, Box = true, Collapsible = true,
              Tooltip = "Cars closer than this to the camera are never touched.")]
        public float MinDistanceMeters = 500f;

        /// <summary>
        /// Acceleration (m/s^2) above which a car is considered "doing something" —
        /// coupling, braking, slack action — and is restored to full fidelity.
        /// </summary>
        [Draw("Steady accel threshold (m/s^2)", Type = DrawType.Slider, Min = 0.05, Max = 3,
              Precision = 2,
              Tooltip = "Acceleration above which a car is considered \"doing something\" "
                      + "(coupling, braking, slack action) and is restored to full fidelity.")]
        public float SteadyAccelThreshold = 0.5f;

        /// <summary>How long a car must stay calm before we downgrade it.</summary>
        [Draw("Required steady time (s)", Type = DrawType.Slider, Min = 0.5, Max = 15,
              Precision = 1,
              Tooltip = "How long a car must stay calm before we downgrade it.")]
        public float RequiredSteadySeconds = 3f;

        /// <summary>Speed (m/s) above which a car counts as moving, for reporting only.</summary>
        [Draw("Moving speed threshold (m/s)", Type = DrawType.Slider, Min = 0.01, Max = 2,
              Precision = 2,
              Tooltip = "Speed above which a car counts as \"moving\" for reporting. Determines the "
                      + "moving/stationary split the headroom verdict is judged against.")]
        public float MovingSpeedThreshold = 0.1f;

        // --- features ---

        /// <summary>
        /// Experimental: its only measurement so far showed no benefit. Ships off.
        /// </summary>
        [Draw("Solver iteration LOD  [experimental]", Type = DrawType.Toggle,
              Box = true, Collapsible = true,
              Tooltip = "Lowers PhysX solver iterations on distant, steady rolling stock. "
                      + "Unproven: its only measurement so far showed no benefit.")]
        public bool EnableSolverLod = false;

        /// <summary>PhysX solver iterations applied to eligible cars. Unity's default is 6.</summary>
        [Draw("Low solver iterations", Type = DrawType.Slider, Min = 1, Max = 6, Precision = 0,
              VisibleOn = "EnableSolverLod|true")]
        public int LowSolverIterations = 2;

        /// <summary>
        /// Read-only sleep headroom probe. Answers, before any sleep code is written,
        /// whether forcing distant parked cars to sleep is worth building at all. On by
        /// default: it mutates nothing, so there is no cost to always measuring.
        /// </summary>
        [Draw("Sleep headroom probe (read-only)", Type = DrawType.Toggle,
              Box = true, Collapsible = true,
              Tooltip = "Measures how many cars are parked but still awake. Changes nothing.")]
        public bool EnableSleepHeadroomProbe = true;

        [Draw("Car renderer LOD  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Stops distant rolling stock from casting shadows. Cars never disappear or change shape.")]
        public bool EnableCarRendererLod = false;

        [Draw("Car shadow distance (m)", Type = DrawType.Slider, Min = 50, Max = 2000,
              VisibleOn = "EnableCarRendererLod|true",
              Tooltip = "Past this distance a car stops casting shadows. Its own shadow is a few pixels there.")]
        public float CarShadowDistanceMeters = 300f;

        [Draw("Tree & ground detail LOD  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Draws distant trees as flat billboards and shortens ground-detail draw distance. "
                      + "Never changes density — the forest stays as thick as you set it.")]
        public bool EnableTerrainLod = false;

        [Draw("Tree billboard distance (m)", Type = DrawType.Slider, Min = 10, Max = 250,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Past this distance a tree is drawn as a batched billboard instead of a 3D mesh.")]
        public float TreeBillboardDistanceMeters = 60f;

        [Draw("Max full-detail trees", Type = DrawType.Slider, Min = 0, Max = 250, Precision = 0,
              VisibleOn = "EnableTerrainLod|true")]
        public int TreeMaxFullLodCount = 50;

        [Draw("Tree crossfade length (m)", Type = DrawType.Slider, Min = 0, Max = 100,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Softens the pop as a tree switches to a billboard.")]
        public float TreeCrossFadeLengthMeters = 20f;

        [Draw("Ground detail distance (m)", Type = DrawType.Slider, Min = 10, Max = 250,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Draw distance for grass and ground detail. Density is never changed.")]
        public float DetailObjectDistanceMeters = 80f;

        // --- cadence ---

        [Draw("Refresh interval (s)", Type = DrawType.Slider, Min = 0.5, Max = 10, Precision = 2,
              Tooltip = "How often the car registry re-scans the scene for rolling stock.")]
        public float RefreshIntervalSeconds = 2f;

        [Draw("Evaluate interval (s)", Type = DrawType.Slider, Min = 0.05, Max = 2, Precision = 2,
              Tooltip = "How often eligibility is re-evaluated for every tracked car. Lower values cost "
                      + "more CPU; this is a performance mod, so 0 would defeat its own purpose.")]
        public float EvaluateIntervalSeconds = 0.25f;

        // --- experiment ---

        /// <summary>
        /// Telemetry is recorded every session regardless of this setting. Turning this on
        /// additionally alternates <see cref="ExperimentTarget"/> between baseline and
        /// active windows and logs both, so that one feature's effect can be measured
        /// without the player running a protocol. Normal play does not need this: leave it
        /// off and every row is simply stamped LIVE, reflecting whatever each feature's own
        /// toggle says.
        /// </summary>
        [Draw("Run A/B experiment (alternates one feature)", Type = DrawType.Toggle,
              Box = true, Collapsible = true,
              Tooltip = "Measurement mode, not normal operation: alternates ExperimentTarget between "
                      + "baseline and active windows and logs both so its effect can be isolated. "
                      + "Telemetry is recorded either way; normal use does not need this on.")]
        public bool RunExperiment = false;

        /// <summary>
        /// How often a telemetry row is written. Governs LIVE row cadence too, not just the
        /// A/B window length — telemetry is recorded every session, so this stays visible
        /// and adjustable regardless of RunExperiment.
        /// </summary>
        [Draw("Telemetry row interval (s)", Type = DrawType.Slider, Min = 10, Max = 120, Precision = 0,
              Tooltip = "How often a telemetry row is written. Also the length of each baseline/active "
                      + "window while the A/B experiment is running.")]
        public float ExperimentWindowSeconds = 30f;

        /// <summary>
        /// The Id of the single feature the A/B harness alternates. Every other feature
        /// holds whatever its own Enabled toggle says, so an fps delta can be attributed to
        /// this one feature rather than to all of them at once.
        /// </summary>
        [Draw("Experiment target (feature Id)", Type = DrawType.Field,
              VisibleOn = "RunExperiment|true",
              Tooltip = "The Id of the single feature the A/B harness alternates. Valid ids: "
                      + "car_renderer_lod, solver_lod, terrain_lod, sleep_headroom.")]
        public string ExperimentTarget = "solver_lod";

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
            // A feature switched off must hand back everything it was holding. UMM tells us
            // a value changed but not which, so ask every feature whether its toggle still
            // agrees with what it is holding.
            Main.ReleaseDisabledFeatures();

            // Any edit can also invalidate telemetry's in-flight window (it may now span two
            // configurations) or leave the experiment target mis-pinned if RunExperiment was
            // the field that changed. Discard the window and re-apply the current mode.
            Main.TelemetrySettingsChanged();
        }
    }
}
