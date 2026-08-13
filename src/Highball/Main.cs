using System;
using System.Collections.Generic;
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
        private static Experiment _experiment;

        private static float _refreshTimer;
        private static float _evalTimer;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings.Instance = UnityModManager.ModSettings.Load<Settings>(modEntry);

            _registry = new CarRegistry();
            _evaluator = new Evaluator();
            _host = new FeatureHost(new IFeature[]
            {
                // Priority order. Sleep, once it exists, goes ahead of solver LOD.
                new SolverLodFeature()
            });

            // A car reaped by discovery may still be claimed by a feature; hand it back
            // to every feature before it drops out of the table. CarRegistry has no
            // feature state of its own, so it cannot do this itself.
            _registry.OnCarRemoved = car => _host.ReleaseAll(car);

            _experiment = new Experiment(_host, _registry, _evaluator);
            _experiment.Init();

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
                    _experiment.ForceActive(false);
                }
                else
                {
                    _experiment.ForceActive(true);
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
                }

                if (Settings.Instance.RunExperiment)
                {
                    _experiment.Tick(deltaTime);
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
            GUILayout.Label($"Tracked: {_registry.TrackedCount}   Moving: {_evaluator.MovingCount}   Downgraded: {CountDowngraded()}");

            if (Settings.Instance.RunExperiment)
            {
                GUILayout.Label($"Experiment window: {(_experiment.ActiveWindow ? "ACTIVE" : "BASELINE")}   rows: {_experiment.RowsWritten}");
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
            _experiment?.Shutdown();
            return true;
        }

        /// <summary>
        /// Number of currently-claimed cars, for the panel. A stopgap until Task 6 gives
        /// each feature its own telemetry column.
        /// </summary>
        private static int CountDowngraded()
        {
            IList<TrackedCar> cars = _registry.Cars;
            int count = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                if (cars[i] != null && cars[i].IsDowngraded)
                {
                    count++;
                }
            }

            return count;
        }

        public static void Log(string msg)
        {
            ModEntry?.Logger.Log("[Highball] " + msg);
        }
    }
}
