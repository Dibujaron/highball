using System;
using HarmonyLib;
using UI.Builder;
using UnityEngine;
using UnityEngine.UI;

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
                    // Declared before the toggle so the toggle's own callback can capture
                    // and gate them by closure; each is assigned below once AddSlider
                    // builds it, and the toggle callback only ever runs later, after a
                    // player interaction, by which point every one is assigned.
                    RectTransform billboardDistance = null;
                    RectTransform detailDistance = null;
                    RectTransform maxFullLod = null;
                    RectTransform crossFade = null;

                    Action<bool> setTreeSlidersInteractable = value =>
                    {
                        SetInteractable(billboardDistance, value);
                        SetInteractable(detailDistance, value);
                        SetInteractable(maxFullLod, value);
                        SetInteractable(crossFade, value);
                    };

                    b.AddFieldToggle("Enable  [experimental]", () => s.EnableTerrainLod,
                        v => { s.EnableTerrainLod = v; s.OnChange(); setTreeSlidersInteractable(v); }, true);

                    billboardDistance = b.AddSlider(() => s.TreeBillboardDistanceMeters,
                        () => s.TreeBillboardDistanceMeters.ToString("F0") + " m",
                        v => { s.TreeBillboardDistanceMeters = v; s.OnChange(); },
                        10f, 250f, false, v => { });

                    detailDistance = b.AddSlider(() => s.DetailObjectDistanceMeters,
                        () => s.DetailObjectDistanceMeters.ToString("F0") + " m",
                        v => { s.DetailObjectDistanceMeters = v; s.OnChange(); },
                        10f, 250f, false, v => { });

                    maxFullLod = b.AddSlider(() => s.TreeMaxFullLodCount,
                        () => s.TreeMaxFullLodCount.ToString(),
                        v => { s.TreeMaxFullLodCount = (int)v; s.OnChange(); },
                        0f, 250f, true, v => { });

                    crossFade = b.AddSlider(() => s.TreeCrossFadeLengthMeters,
                        () => s.TreeCrossFadeLengthMeters.ToString("F0") + " m",
                        v => { s.TreeCrossFadeLengthMeters = v; s.OnChange(); },
                        0f, 100f, false, v => { });

                    // Match Settings.cs's VisibleOn = "EnableTerrainLod|true": these sliders
                    // do nothing while the feature is off, so the in-game tab should say so
                    // too rather than leaving them live.
                    setTreeSlidersInteractable(s.EnableTerrainLod);
                }, 8f);

                panel.AddSection("Rolling stock", b =>
                {
                    RectTransform shadowDistance = null;

                    Action<bool> setCarSlidersInteractable = value => SetInteractable(shadowDistance, value);

                    b.AddFieldToggle("Car renderer LOD  [experimental]", () => s.EnableCarRendererLod,
                        v => { s.EnableCarRendererLod = v; s.OnChange(); setCarSlidersInteractable(v); }, true);

                    shadowDistance = b.AddSlider(() => s.CarShadowDistanceMeters,
                        () => s.CarShadowDistanceMeters.ToString("F0") + " m",
                        v => { s.CarShadowDistanceMeters = v; s.OnChange(); },
                        50f, 2000f, false, v => { });

                    // Match Settings.cs's VisibleOn = "EnableCarRendererLod|true".
                    setCarSlidersInteractable(s.EnableCarRendererLod);

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

        /// <summary>
        /// Mirrors, for the in-game tab, the interactability Settings.cs's VisibleOn already
        /// gives the UMM panel: a slider whose parent feature is off should not look live.
        /// AddFieldToggle exposes an `interactable` parameter directly, but AddSlider does
        /// not, so the slider's own Selectable component — found in its child hierarchy,
        /// since AddSlider returns the control's outer RectTransform, not the Selectable
        /// itself — is toggled instead. Null-safe throughout: a control that failed to
        /// build, or whose hierarchy does not contain a Selectable, is left alone rather
        /// than throwing.
        /// </summary>
        private static void SetInteractable(RectTransform control, bool interactable)
        {
            if (control == null)
            {
                return;
            }

            Selectable selectable = control.GetComponentInChildren<Selectable>(true);
            if (selectable != null)
            {
                selectable.interactable = interactable;
            }
        }
    }
}
