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
    /// </summary>
    [HarmonyPatch]
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
        /// so the existing release-on-disable and telemetry-window-reset logic runs
        /// exactly as it does for a UMM-panel edit. Slider ranges are copied from each
        /// field's [Draw] attribute in Settings.cs so the two UIs cannot disagree.
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
                    b.AddFieldToggle("Enable", () => s.EnableTerrainLod,
                        v => { s.EnableTerrainLod = v; s.OnChange(); }, true);

                    b.AddSlider(() => s.TreeBillboardDistanceMeters,
                        () => s.TreeBillboardDistanceMeters.ToString("F0") + " m",
                        v => { s.TreeBillboardDistanceMeters = v; s.OnChange(); },
                        10f, 250f, false, v => { });

                    b.AddSlider(() => s.DetailObjectDistanceMeters,
                        () => s.DetailObjectDistanceMeters.ToString("F0") + " m",
                        v => { s.DetailObjectDistanceMeters = v; s.OnChange(); },
                        10f, 250f, false, v => { });

                    b.AddSlider(() => s.TreeMaxFullLodCount,
                        () => s.TreeMaxFullLodCount.ToString(),
                        v => { s.TreeMaxFullLodCount = (int)v; s.OnChange(); },
                        0f, 250f, true, v => { });

                    b.AddSlider(() => s.TreeCrossFadeLengthMeters,
                        () => s.TreeCrossFadeLengthMeters.ToString("F0") + " m",
                        v => { s.TreeCrossFadeLengthMeters = v; s.OnChange(); },
                        0f, 100f, false, v => { });
                }, 8f);

                panel.AddSection("Rolling stock", b =>
                {
                    b.AddFieldToggle("Car renderer LOD", () => s.EnableCarRendererLod,
                        v => { s.EnableCarRendererLod = v; s.OnChange(); }, true);

                    b.AddSlider(() => s.CarShadowDistanceMeters,
                        () => s.CarShadowDistanceMeters.ToString("F0") + " m",
                        v => { s.CarShadowDistanceMeters = v; s.OnChange(); },
                        50f, 2000f, false, v => { });

                    b.AddFieldToggle("Solver iteration LOD (no measured benefit)",
                        () => s.EnableSolverLod,
                        v => { s.EnableSolverLod = v; s.OnChange(); }, true);
                }, 8f);

                panel.AddSection("Diagnostics", b =>
                {
                    b.AddFieldToggle("Sleep headroom probe (read-only)",
                        () => s.EnableSleepHeadroomProbe,
                        v => { s.EnableSleepHeadroomProbe = v; s.OnChange(); }, true);

                    b.AddFieldToggle("Run A/B experiment", () => s.RunExperiment,
                        v => { s.RunExperiment = v; s.OnChange(); }, true);

                    b.AddLabel(() => "Telemetry: " + Main.TelemetryModeLabel()
                                      + "   rows: " + Main.TelemetryRowsWritten(),
                        UIPanelBuilder.Frequency.Periodic);
                }, 8f);
            }
            catch (Exception ex)
            {
                Main.Log("Highball tab content failed to build: " + ex.Message);
            }
        }
    }
}
