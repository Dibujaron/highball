using UnityModManagerNet;

namespace Highball
{
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        internal static Settings Instance;

        // --- features ---

        [Draw("Car renderer LOD  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Stops distant rolling stock from casting shadows. Cars never disappear or change shape.")]
        public bool EnableCarRendererLod = false;

        [Draw("Car shadow distance (m)", Type = DrawType.Slider, Min = 50, Max = 2000,
              VisibleOn = "EnableCarRendererLod|true",
              Tooltip = "Past this distance a car stops casting shadows. Its own shadow is a few pixels there.")]
        public float CarShadowDistanceMeters = 300f;

        [Draw("GPU instancing  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Flips enableInstancing on the game's materials so identical meshes can draw "
                      + "in batches. Measured by the draw_calls/batches telemetry columns; restored "
                      + "on toggle-off.")]
        public bool EnableGpuInstancing = false;

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
              Box = true, Collapsible = true,
              Tooltip = "How often the car registry re-scans the scene for rolling stock.")]
        public float RefreshIntervalSeconds = 2f;

        [Draw("Evaluate interval (s)", Type = DrawType.Slider, Min = 0.05, Max = 2, Precision = 2,
              Tooltip = "How often eligibility is re-evaluated for every tracked car. Lower values cost "
                      + "more CPU; this is a performance mod, so 0 would defeat its own purpose.")]
        public float EvaluateIntervalSeconds = 0.25f;

        // --- diagnostics ---

        /// <summary>
        /// Read-only, but the most invasive thing here: it inserts timing markers into
        /// Unity's player loop. Ships off, and hands the loop back on toggle-off and unload.
        /// </summary>
        [Draw("Frame budget probe (read-only)", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Measures where the frame goes — physics vs rendering vs scripts — by timing "
                      + "Unity's player-loop subsystems. Changes no game state, but does insert markers "
                      + "into the update loop, so it ships off. Feeds extra telemetry columns.")]
        public bool EnableFrameBudgetProbe = false;

        /// <summary>
        /// Read-only, and the broadest thing here: it Harmony-patches every MonoBehaviour's
        /// FixedUpdate and Update across every loaded assembly, including other mods'.
        /// Ships off, unpatches on toggle-off.
        /// </summary>
        [Draw("Script attribution probe (read-only)", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Splits the C# FixedUpdate/Update cost into a ranked list of which class and "
                      + "which mod is spending it, reported to the log. Times hundreds of other "
                      + "mods' methods, so it ships off and costs a second or two at startup.")]
        public bool EnableScriptAttribution = false;

        /// <summary>
        /// Read-only one-shot census of renderers, materials and shaders, answering whether
        /// the measured zero-batching (batches == draw_calls) is fixable from a mod.
        /// </summary>
        [Draw("Render inventory probe (read-only)", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "One-shot census of renderers, unique materials, instancing flags and "
                      + "shaders, reported to the log. Costs one hitch when it runs; toggle off "
                      + "and on to run it again.")]
        public bool EnableRenderInventory = false;

        // --- telemetry ---

        /// <summary>
        /// Off by default. Recording costs a CSV row every interval and is only useful while
        /// measuring a feature, so normal play writes nothing. Turning this on mid-session
        /// starts a new file; turning it off closes the current one.
        /// </summary>
        [Draw("Record telemetry to CSV", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Writes frame timings and per-feature counters to a CSV in the game's "
                      + "persistent data folder. Off unless you are measuring something.")]
        public bool EnableTelemetry = false;

        [Draw("Telemetry row interval (s)", Type = DrawType.Slider, Min = 10, Max = 120, Precision = 0,
              VisibleOn = "EnableTelemetry|true",
              Tooltip = "How often a telemetry row is written. Each row averages the frames since "
                      + "the previous one.")]
        public float TelemetryIntervalSeconds = 30f;

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
            // configurations), and EnableTelemetry may be the field that changed. Let
            // Telemetry discard the window and start or stop recording accordingly.
            Main.TelemetrySettingsChanged();
        }
    }
}
