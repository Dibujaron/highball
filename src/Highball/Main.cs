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

        private static LodManager _lod;
        private static Experiment _experiment;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings.Instance = UnityModManager.ModSettings.Load<Settings>(modEntry);

            _lod = new LodManager();
            // A car reaped by discovery may still be downgraded; hand it back to the
            // solver action before it drops out of the table. CarRegistry has no
            // feature state of its own, so it cannot do this itself.
            _lod.Registry.OnCarRemoved = _lod.Restore;

            _experiment = new Experiment(_lod);
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
                // Leaving downgraded rigidbodies behind would be a silent, persistent
                // change to the player's save state. Always hand them back.
                _lod.Clear();
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
                _lod.Tick(deltaTime);

                if (Settings.Instance.RunExperiment)
                {
                    _experiment.Tick(deltaTime);
                }
            }
            catch (Exception ex)
            {
                Log("Tick failed, disabling to be safe: " + ex);
                Enabled = false;
                _lod.Clear();
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Highball");
            GUILayout.Label($"Tracked: {_lod.TrackedCount}   Moving: {_lod.MovingCount}   Downgraded: {_lod.DowngradedCount}");

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
            _lod?.Clear();
            _experiment?.Shutdown();
            return true;
        }

        public static void Log(string msg)
        {
            ModEntry?.Logger.Log("[Highball] " + msg);
        }
    }
}
