using System;
using UnityEngine;
using UnityModManagerNet;

namespace Highball
{
    /// <summary>
    /// Rolling Stock Physics LOD.
    ///
    /// Reduces PhysX solver iterations on distant rolling stock that is in steady-state
    /// motion. Targets the gap left by RollingStock Optimizer, which by design never
    /// touches moving cars — and moving stock is what the community reports as the
    /// dominant cost.
    ///
    /// Ships with an A/B harness enabled by default, because the last three performance
    /// hypotheses on this problem were all wrong and this one deserves evidence too.
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
            _probe = new SleepHeadroomProbe();
            _host = new FeatureHost(new IFeature[]
            {
                // Priority order. Sleep, once it exists, goes ahead of solver LOD.
                new SolverLodFeature(),
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
                    _probe.Observe(_registry.Cars);
                    _host.Apply(_registry.Cars);
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
                GUILayout.Label($"Experiment window: {(_telemetry.ActiveWindow ? "ACTIVE" : "BASELINE")}   rows: {_telemetry.RowsWritten}");
            }

            GUILayout.Space(8f);
            Settings.Instance.DrawGui();
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
    }
}
