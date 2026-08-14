using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace Highball
{
    /// <summary>
    /// Highball — a performance mod for Railroader.
    ///
    /// Hosts a set of independently-toggleable features over a shared core that discovers
    /// rolling stock, computes per-car facts once per pass, and arbitrates which feature
    /// may act on which car.
    ///
    /// CSV telemetry is available but off by default: recording is a measurement activity,
    /// and ordinary play should write nothing.
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;
        public static bool Enabled;

        private static CarRegistry _registry;
        private static Evaluator _evaluator;
        private static FeatureHost _host;
        private static Telemetry _telemetry;
        private static FrameBudgetProbe _budget;
        private static ScriptAttributionProbe _scripts;
        private static RenderInventoryProbe _renderInventory;
        private static InstancingFeature _instancing;
        private static Harmony _harmony;

        private static float _refreshTimer;
        private static float _evalTimer;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings.Instance = UnityModManager.ModSettings.Load<Settings>(modEntry);

            _registry = new CarRegistry();
            _evaluator = new Evaluator();
            _budget = new FrameBudgetProbe();
            _scripts = new ScriptAttributionProbe();
            _renderInventory = new RenderInventoryProbe(_registry.Cars);
            _instancing = new InstancingFeature(_registry.Cars);
            _host = new FeatureHost(new IFeature[]
            {
                // Priority order == claim order: FeatureHost.Apply offers each car to
                // ICarFeatures in this array's order, and the first enabled one to claim it
                // wins, so a second car-acting feature added here would only ever see the
                // cars this one declined.
                new CarRendererFeature(),
                // Acts on terrains, not cars, so it never claims and its position here
                // doesn't affect arbitration. Kept after the car-acting features so
                // priority order stays readable.
                new TerrainLodFeature(),
                // Acts on materials, not cars; never claims either.
                _instancing,
                // Read-only and never claim. Kept last so mutating features stay first.
                _budget,
                _scripts,
                _renderInventory
            });

            // A car reaped by discovery may still be claimed by a feature; hand it back
            // to every feature before it drops out of the table. CarRegistry has no
            // feature state of its own, so it cannot do this itself.
            _registry.OnCarRemoved = car => _host.ReleaseAll(car);

            _telemetry = new Telemetry(_host, _registry, _evaluator);
            _telemetry.Init();

            // In-game preferences tab: purely additive, and entirely best-effort. A failure
            // here must never fail the whole mod load — the UMM panel is always the
            // fallback, so this is wrapped on its own rather than allowed to propagate.
            try
            {
                _harmony = new Harmony("highball");
                GamePreferencesPatch.Apply(_harmony);
            }
            catch (Exception ex)
            {
                Log("In-game settings tab unavailable: " + ex.Message);
            }

            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;

            Log("Loaded.");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;

            if (value)
            {
                Log("Enabled.");
            }
            else
            {
                // Leaving claimed rigidbodies behind would be a silent, persistent
                // change to the player's save state. Always hand them back.
                _host.ReleaseAll(_registry.Cars);
                _registry.Clear();
                Log("Disabled, all cars restored.");
            }

            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            if (!Enabled || !Application.isPlaying)
            {
                return;
            }

            try
            {
                _refreshTimer += deltaTime;
                _evalTimer += deltaTime;

                if (_refreshTimer >= Settings.Instance.RefreshIntervalSeconds)
                {
                    _refreshTimer = 0f;
                    _registry.Refresh();
                }

                if (_evalTimer >= Settings.Instance.EvaluateIntervalSeconds)
                {
                    float dt = _evalTimer;
                    _evalTimer = 0f;
                    _evaluator.Evaluate(_registry.Cars);
                    _host.Apply(_registry.Cars);
                    _host.Tick(dt);
                }

                // No-op unless recording is switched on; see Telemetry.Tick.
                _telemetry.Tick(deltaTime);
            }
            catch (Exception ex)
            {
                Log("Tick failed, disabling to be safe: " + ex);
                Enabled = false;
                _host.ReleaseAll(_registry.Cars);
                _registry.Clear();
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Highball");
            GUILayout.Label($"Tracked: {_registry.TrackedCount}   Moving: {_evaluator.MovingCount}");

            GUILayout.Label(string.Format("Telemetry: {0}   rows: {1}",
                _telemetry.StatusLabel(),
                _telemetry.RowsWritten));

            if (Settings.Instance.EnableFrameBudgetProbe)
            {
                GUILayout.Label("Frame budget: " + _budget.StatusLine());
            }

            GUILayout.Space(8f);
            UnityModManager.UI.DrawFields(ref Settings.Instance, modEntry,
                DrawFieldMask.Public, Settings.Instance.OnChange);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Instance.Save(modEntry);
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            if (_host != null && _registry != null)
            {
                _host.ReleaseAll(_registry.Cars);
            }

            _registry?.Clear();
            _telemetry?.Shutdown();

            // Unpatch before releasing the reference so a UMM reload (unload then load
            // again in the same process) can never stack a second copy of the postfix
            // onto BuildTabs.
            try
            {
                _harmony?.UnpatchAll("highball");
            }
            catch (Exception ex)
            {
                Log("Failed to unpatch in-game settings tab: " + ex.Message);
            }
            _harmony = null;

            return true;
        }

        public static void Log(string msg)
        {
            // UMM's own logger already prefixes every line with the mod's display name, so
            // adding "[Highball] " here doubled it into "[Highball] [Highball] ..." in the
            // player log. Log the message alone.
            ModEntry?.Logger.Log(msg);
        }

        /// <summary>
        /// Small internal accessors so the in-game tab's live label can show the same
        /// status/rows readout Main.OnGUI already shows, without making _telemetry (or
        /// Telemetry's internals) public. Guarded against _telemetry being null: the
        /// in-game tab can in principle be rendered before Load finishes constructing it,
        /// or after OnUnload has torn it down.
        /// </summary>
        internal static string TelemetryStatus()
        {
            return _telemetry != null ? _telemetry.StatusLabel() : "n/a";
        }

        internal static int TelemetryRowsWritten()
        {
            return _telemetry != null ? _telemetry.RowsWritten : 0;
        }

        internal static string FrameBudgetStatus()
        {
            return _budget != null ? _budget.StatusLine() : "n/a";
        }

        internal static string ScriptAttributionStatus()
        {
            return _scripts != null ? _scripts.StatusLine() : "n/a";
        }

        internal static string RenderInventoryStatus()
        {
            return _renderInventory != null ? _renderInventory.StatusLine() : "n/a";
        }

        internal static string GpuInstancingStatus()
        {
            return _instancing != null ? _instancing.StatusLine() : "n/a";
        }

        /// <summary>
        /// The tracked/moving readout the UMM panel has always shown, exposed so the in-game
        /// tab can show it too. `moving` is the workload figure telemetry rows are compared
        /// against, so having it visible only from the main menu made it useless while
        /// actually driving — which is the only time it means anything.
        /// </summary>
        internal static string CarCountStatus()
        {
            if (_registry == null || _evaluator == null)
            {
                return "n/a";
            }

            return string.Format("{0} · {1} moving",
                _registry.TrackedCount, _evaluator.MovingCount);
        }

        /// <summary>
        /// A feature switched off must hand back everything it was holding. UMM calls
        /// Settings.OnChange whenever a drawn field changes but does not tell us which one,
        /// so ask every feature whether its toggle still agrees with what it is holding.
        /// Guarded against _host being null: OnChange can fire (e.g. from the settings file
        /// being deserialized) before Load has finished constructing it.
        /// </summary>
        internal static void ReleaseDisabledFeatures()
        {
            if (_host == null)
            {
                return;
            }

            IFeature[] features = _host.Features;
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i].Enabled)
                {
                    continue;
                }

                try
                {
                    features[i].ReleaseAll();
                }
                catch (Exception ex)
                {
                    Log("Feature '" + features[i].Id + "' threw from ReleaseAll(): " + ex);
                }
            }
        }

        /// <summary>
        /// Also called from Settings.OnChange, alongside ReleaseDisabledFeatures. Tells
        /// Telemetry to start or stop recording if that toggle is what changed, and to
        /// discard whatever window is in flight either way, so a settings edit mid-window
        /// never flushes a row mixing two configurations. Guarded against _telemetry being
        /// null for the same reason as ReleaseDisabledFeatures: OnChange can fire before
        /// Load has finished constructing it.
        /// </summary>
        internal static void TelemetrySettingsChanged()
        {
            _telemetry?.SettingsChanged();
        }
    }
}
