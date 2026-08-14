using System;
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
    /// Ships with an A/B harness enabled by default, because the performance hypotheses
    /// tried on this problem so far were wrong when measured, and the next one deserves
    /// evidence too.
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;
        public static bool Enabled;

        private static CarRegistry _registry;
        private static Evaluator _evaluator;
        private static FeatureHost _host;
        private static Telemetry _telemetry;
        private static SleepHeadroomProbe _probe;

        private static float _refreshTimer;
        private static float _evalTimer;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings.Instance = UnityModManager.ModSettings.Load<Settings>(modEntry);

            _registry = new CarRegistry();
            _evaluator = new Evaluator();
            _probe = new SleepHeadroomProbe(_registry.Cars);
            _host = new FeatureHost(new IFeature[]
            {
                // Priority order. Sleep, once it exists, goes ahead of solver LOD.
                new SolverLodFeature(),
                // Acts on terrains, not cars, so it never claims and its position here
                // doesn't affect arbitration. Kept after the car-acting features so
                // priority order stays readable.
                new TerrainLodFeature(),
                // Read-only; never claims, so its position here doesn't affect
                // arbitration. Kept last so mutating features stay first and readable.
                _probe
            });

            // A car reaped by discovery may still be claimed by a feature; hand it back
            // to every feature before it drops out of the table. CarRegistry has no
            // feature state of its own, so it cannot do this itself.
            _registry.OnCarRemoved = car => _host.ReleaseAll(car);

            _telemetry = new Telemetry(_host, _registry, _evaluator);
            _telemetry.Init();

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
                if (Settings.Instance.RunExperiment)
                {
                    // Start on baseline so the first recorded window is a control.
                    _telemetry.ForceActive(false);
                }
                else
                {
                    _telemetry.ForceActive(true);
                }

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
                    _evaluator.Evaluate(_registry.Cars, dt);
                    _host.Apply(_registry.Cars);
                    _host.Tick(dt);
                }

                if (Settings.Instance.RunExperiment)
                {
                    _telemetry.Tick(deltaTime);
                }
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

            if (Settings.Instance.RunExperiment)
            {
                GUILayout.Label(string.Format("Experiment window: {0}   target: {1}   rows: {2}",
                    _telemetry.ActiveWindow ? "ACTIVE" : "BASELINE",
                    Settings.Instance.ExperimentTarget,
                    _telemetry.RowsWritten));
            }

            _probe.DrawStatus();

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
            return true;
        }

        public static void Log(string msg)
        {
            ModEntry?.Logger.Log("[Highball] " + msg);
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
        /// Telemetry to discard whatever window is in flight and re-apply the current mode,
        /// so a settings edit mid-window never flushes a row mixing two configurations and
        /// RunExperiment being switched off never leaves the experiment target pinned
        /// inactive forever. Guarded against _telemetry being null for the same reason as
        /// ReleaseDisabledFeatures: OnChange can fire before Load finishes constructing it.
        /// </summary>
        internal static void TelemetrySettingsChanged()
        {
            _telemetry?.SettingsChanged();
        }
    }
}
