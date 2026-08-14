using System;
using HarmonyLib;
using UI.Builder;

namespace Highball
{
    /// <summary>
    /// Adds a Highball tab to Railroader's own preferences window, so the features can be
    /// tuned in-game without relaunching. The UMM panel (Main.OnGUI) remains exactly as it
    /// was — this is purely additive.
    ///
    /// Everything here is defensive: this patches another developer's UI, and if the game
    /// updates and that UI changes shape, the patch must fail to "no Highball tab", never
    /// to a broken preferences window. Every layer — resolving the type/method, applying
    /// the patch, running the postfix, and building the tab's contents — is wrapped in its
    /// own try/catch so a throw at any layer cannot propagate into the game's UI code.
    ///
    /// This class is patched manually from Apply() below, called once from Main.Load; it
    /// declares no Harmony target of its own, so it must not carry a bare [HarmonyPatch]
    /// attribute (that shape exists for harmony.PatchAll() to discover automatically, which
    /// this project never calls).
    /// </summary>
    internal static class GamePreferencesPatch
    {
        /// <summary>
        /// Resolves UI.PreferencesWindow.PreferencesBuilder.BuildTabs by name and patches it
        /// with a postfix that appends a Highball tab. Called once from Main.Load. If the
        /// type or method cannot be found, or Patch() itself throws (e.g. an incompatible
        /// game update), this logs why and returns without patching — the preferences
        /// window is left completely untouched and the UMM panel remains the only way to
        /// reach these settings, exactly as it works today.
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type builder = AccessTools.TypeByName("UI.PreferencesWindow.PreferencesBuilder");
                if (builder == null)
                {
                    Main.Log("In-game settings tab unavailable: PreferencesBuilder not found.");
                    return;
                }

                var target = AccessTools.Method(builder, "BuildTabs");
                if (target == null)
                {
                    Main.Log("In-game settings tab unavailable: BuildTabs not found.");
                    return;
                }

                var postfix = AccessTools.Method(typeof(GamePreferencesPatch), nameof(BuildTabsPostfix));
                if (postfix == null)
                {
                    Main.Log("In-game settings tab unavailable: postfix method not found.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                Main.Log("In-game settings tab installed.");
            }
            catch (Exception ex)
            {
                Main.Log("In-game settings tab unavailable: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs after the game's own BuildTabs. Appends one more tab. Wrapped on its own so
        /// that even though Apply() only patches successfully-resolved methods, a runtime
        /// throw here (e.g. AddTab's signature having changed, or the builder instance being
        /// in an unexpected state) still cannot escape into the game's UI-construction code.
        /// </summary>
        private static void BuildTabsPostfix(UITabbedPanelBuilder builder)
        {
            try
            {
                builder.AddTab("Highball", "highball", BuildHighballTab);
            }
            catch (Exception ex)
            {
                Main.Log("Highball tab failed to build: " + ex.Message);
            }
        }

        /// <summary>
        /// Contents of the Highball tab. Every control reads and writes the same
        /// Settings fields the UMM panel uses, and every edit calls Settings.OnChange()
        /// so the existing release-on-disable and telemetry logic runs exactly as it does
        /// for a UMM-panel edit. Slider ranges are copied from each field's [Draw]
        /// attribute in Settings.cs so the two UIs cannot disagree.
        ///
        /// Every control goes through AddField(label, control), never AddSlider/AddLabel
        /// on their own. A bare AddSlider returns an unlabelled, full-width RectTransform:
        /// it renders with no name at all and spans the whole panel including the gutter
        /// the section title is drawn in, so the title and the slider overlap. AddField is
        /// what pairs a control with its label and lays it out in the content column —
        /// AddFieldToggle is simply the same wrapper with a toggle built in, which is why
        /// the toggles looked right while the sliders did not.
        ///
        /// Wrapped in its own try/catch (in addition to BuildTabsPostfix's) so that a
        /// throw partway through — e.g. the third section failing after the first two
        /// already added controls — is caught at the point closest to the failure,
        /// leaving whatever sections built successfully in place instead of discarding
        /// the whole tab.
        /// </summary>
        private static void BuildHighballTab(UIPanelBuilder panel)
        {
            try
            {
                Settings s = Settings.Instance;

                panel.AddSection("Trees & ground detail", b =>
                {
                    // Declared before the toggle so the toggle's own callback can capture
                    // and gate them by closure; each is assigned below once the field is
                    // built, and the toggle callback only ever runs later, after a player
                    // interaction, by which point every one is assigned.
                    IConfigurableElement billboardDistance = null;
                    IConfigurableElement detailDistance = null;
                    IConfigurableElement maxFullLod = null;
                    IConfigurableElement crossFade = null;

                    Action<bool> gateTreeSliders = enabled =>
                    {
                        Disable(billboardDistance, !enabled);
                        Disable(detailDistance, !enabled);
                        Disable(maxFullLod, !enabled);
                        Disable(crossFade, !enabled);
                    };

                    b.AddFieldToggle("Enable  [experimental]", () => s.EnableTerrainLod,
                        v => { s.EnableTerrainLod = v; s.OnChange(); gateTreeSliders(v); }, true)
                     .Tooltip("Tree & ground detail LOD",
                        "Draws distant trees as flat billboards and shortens ground-detail draw "
                        + "distance. Never changes density — the forest stays as thick as you set it.");

                    billboardDistance = b.AddField("Tree billboard distance",
                        b.AddSlider(() => s.TreeBillboardDistanceMeters,
                            () => s.TreeBillboardDistanceMeters.ToString("F0") + " m",
                            v => { s.TreeBillboardDistanceMeters = v; s.OnChange(); },
                            10f, 250f, false, v => { }))
                     .Tooltip("Tree billboard distance",
                        "Past this distance a tree is drawn as a batched billboard instead of a 3D mesh.");

                    detailDistance = b.AddField("Ground detail distance",
                        b.AddSlider(() => s.DetailObjectDistanceMeters,
                            () => s.DetailObjectDistanceMeters.ToString("F0") + " m",
                            v => { s.DetailObjectDistanceMeters = v; s.OnChange(); },
                            10f, 250f, false, v => { }))
                     .Tooltip("Ground detail distance",
                        "Draw distance for grass and ground detail. Density is never changed.");

                    maxFullLod = b.AddField("Max full-detail trees",
                        b.AddSlider(() => s.TreeMaxFullLodCount,
                            () => s.TreeMaxFullLodCount.ToString(),
                            v => { s.TreeMaxFullLodCount = (int)v; s.OnChange(); },
                            0f, 250f, true, v => { }))
                     .Tooltip("Max full-detail trees",
                        "How many trees may render as 3D meshes at once. Everything beyond this "
                        + "count is billboarded regardless of distance.");

                    crossFade = b.AddField("Crossfade length",
                        b.AddSlider(() => s.TreeCrossFadeLengthMeters,
                            () => s.TreeCrossFadeLengthMeters.ToString("F0") + " m",
                            v => { s.TreeCrossFadeLengthMeters = v; s.OnChange(); },
                            0f, 100f, false, v => { }))
                     .Tooltip("Crossfade length", "Softens the pop as a tree switches to a billboard.");

                    // Match Settings.cs's VisibleOn = "EnableTerrainLod|true": these sliders
                    // do nothing while the feature is off, so the in-game tab should say so
                    // too rather than leaving them live.
                    gateTreeSliders(s.EnableTerrainLod);
                }, 8f);

                // AddSection's third argument is the spacing BETWEEN ROWS INSIDE the
                // section, not before its title, so a section title is otherwise laid out
                // flush against the previous section's last row. Both are drawn in the same
                // narrow left gutter — the title left-aligned, field labels right-aligned —
                // so flush means the title visibly collides with the label above it
                // ("ROLLING STOCK" into "CROSSFADE LENGTH"). An explicit spacer between
                // sections is what gives each title its own band.
                panel.Spacer(16f);

                panel.AddSection("Rolling stock", b =>
                {
                    IConfigurableElement shadowDistance = null;

                    b.AddFieldToggle("Car renderer LOD  [experimental]", () => s.EnableCarRendererLod,
                        v => { s.EnableCarRendererLod = v; s.OnChange(); Disable(shadowDistance, !v); }, true)
                     .Tooltip("Car renderer LOD",
                        "Stops distant rolling stock from casting shadows. Cars never disappear "
                        + "or change shape.");

                    shadowDistance = b.AddField("Shadow distance",
                        b.AddSlider(() => s.CarShadowDistanceMeters,
                            () => s.CarShadowDistanceMeters.ToString("F0") + " m",
                            v => { s.CarShadowDistanceMeters = v; s.OnChange(); },
                            50f, 2000f, false, v => { }))
                     .Tooltip("Shadow distance",
                        "Past this distance a car stops casting shadows. Its own shadow is a few "
                        + "pixels there.");

                    // Match Settings.cs's VisibleOn = "EnableCarRendererLod|true".
                    Disable(shadowDistance, !s.EnableCarRendererLod);

                    // Lives with the rolling stock even though it sweeps the whole scene,
                    // because cars are the dominant material population (2,708 of ~2,195
                    // scene-wide uniques trace to them) and this is where a player looking
                    // to speed up cars will look.
                    b.AddFieldToggle("GPU instancing  [experimental]", () => s.EnableGpuInstancing,
                        v => { s.EnableGpuInstancing = v; s.OnChange(); }, true)
                     .Tooltip("GPU instancing",
                        "Flips enableInstancing on the game's materials so identical meshes can "
                        + "draw in batches. Measured by the draw_calls/batches telemetry columns; "
                        + "restored on toggle-off.");

                    b.AddField("Instancing", () => Main.GpuInstancingStatus(),
                        UIPanelBuilder.Frequency.Periodic);
                }, 8f);

                panel.Spacer(16f);

                panel.AddSection("Telemetry", b =>
                {
                    IConfigurableElement interval = null;

                    b.AddFieldToggle("Record to CSV", () => s.EnableTelemetry,
                        v => { s.EnableTelemetry = v; s.OnChange(); Disable(interval, !v); }, true)
                     .Tooltip("Record telemetry to CSV",
                        "Writes frame timings and per-feature counters to a CSV in the game's "
                        + "persistent data folder. Off unless you are measuring something.");

                    interval = b.AddField("Row interval",
                        b.AddSlider(() => s.TelemetryIntervalSeconds,
                            () => s.TelemetryIntervalSeconds.ToString("F0") + " s",
                            v => { s.TelemetryIntervalSeconds = v; s.OnChange(); },
                            10f, 120f, true, v => { }))
                     .Tooltip("Row interval",
                        "How often a row is written. Each row averages the frames since the previous one.");

                    Disable(interval, !s.EnableTelemetry);

                    b.AddField("Status",
                        () => Main.TelemetryStatus() + " · " + Main.TelemetryRowsWritten() + " rows",
                        UIPanelBuilder.Frequency.Periodic);
                }, 8f);

                panel.Spacer(16f);

                // Last on purpose. These are investigation tools rather than settings anyone
                // tunes while playing, and this section grew enough to push the sections
                // above it out of the panel's bounds when it sat in the middle.
                panel.AddSection("Diagnostics", b =>
                {
                    // The workload figure every telemetry row is read against, so it has to
                    // be visible while driving rather than only from the main menu.
                    b.AddField("Cars", () => Main.CarCountStatus(),
                        UIPanelBuilder.Frequency.Fast);

                    b.AddFieldToggle("Frame budget", () => s.EnableFrameBudgetProbe,
                        v => { s.EnableFrameBudgetProbe = v; s.OnChange(); }, true)
                     .Tooltip("Frame budget probe (read-only)",
                        "Measures where the frame goes — physics vs rendering vs scripts — by timing "
                        + "Unity's player-loop subsystems. Changes no game state, but does insert "
                        + "markers into the update loop. Feeds extra telemetry columns.");

                    // Values here are kept terse (p/r/s/gpu) because they are drawn into the
                    // same narrow value column as a slider, and a long string overflows the
                    // panel rather than wrapping.
                    b.AddField("ms/frame", () => Main.FrameBudgetStatus(),
                        UIPanelBuilder.Frequency.Periodic)
                     .Tooltip("Frame budget", "Physics · Rendering · Scripts · GPU wait, in ms per frame.");

                    b.AddFieldToggle("Attribution", () => s.EnableScriptAttribution,
                        v => { s.EnableScriptAttribution = v; s.OnChange(); }, true)
                     .Tooltip("Script attribution probe (read-only)",
                        "Splits the C# FixedUpdate/Update cost into a ranked list of which class and "
                        + "which mod is spending it, written to the log every telemetry interval. "
                        + "Times hundreds of other mods' methods, so it costs a second or two at "
                        + "startup and a fraction of a millisecond per frame while running.");

                    b.AddField("Methods", () => Main.ScriptAttributionStatus(),
                        UIPanelBuilder.Frequency.Periodic);

                    b.AddFieldToggle("Render inventory", () => s.EnableRenderInventory,
                        v => { s.EnableRenderInventory = v; s.OnChange(); }, true)
                     .Tooltip("Render inventory probe (read-only)",
                        "One-shot census of renderers, unique materials, instancing flags and "
                        + "shaders, reported to the log. Costs one hitch when it runs; toggle "
                        + "off and on to run it again.");

                    b.AddField("Inventory", () => Main.RenderInventoryStatus(),
                        UIPanelBuilder.Frequency.Periodic);
                }, 8f);
            }
            catch (Exception ex)
            {
                Main.Log("Highball tab content failed to build: " + ex.Message);
            }
        }

        /// <summary>
        /// Mirrors, for the in-game tab, what Settings.cs's VisibleOn already gives the UMM
        /// panel: a control whose parent feature is off should not look live. Null-safe,
        /// since a control that failed to build should be skipped rather than throw and
        /// take the rest of the section with it.
        /// </summary>
        private static void Disable(IConfigurableElement element, bool disabled)
        {
            element?.Disable(disabled);
        }
    }
}
