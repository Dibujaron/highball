using UnityEngine;
using UnityModManagerNet;

namespace StockPhysicsLOD
{
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        internal static Settings Instance;

        // --- eligibility ---

        /// <summary>Cars closer than this to the camera are never touched.</summary>
        public float MinDistanceMeters = 500f;

        /// <summary>
        /// Acceleration (m/s^2) above which a car is considered "doing something" —
        /// coupling, braking, slack action — and is restored to full fidelity.
        /// </summary>
        public float SteadyAccelThreshold = 0.5f;

        /// <summary>How long a car must stay calm before we downgrade it.</summary>
        public float RequiredSteadySeconds = 3f;

        /// <summary>Speed (m/s) above which a car counts as moving, for reporting only.</summary>
        public float MovingSpeedThreshold = 0.1f;

        // --- the lever ---

        /// <summary>PhysX solver iterations applied to eligible cars. Unity's default is 6.</summary>
        public int LowSolverIterations = 2;

        // --- cadence ---

        public float RefreshIntervalSeconds = 2f;
        public float EvaluateIntervalSeconds = 0.25f;

        // --- experiment ---

        /// <summary>
        /// Automatically alternate between baseline and active windows and log both,
        /// so the effect can be measured without the player running a protocol.
        /// </summary>
        public bool RunExperiment = true;

        public float ExperimentWindowSeconds = 30f;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange()
        {
        }

        public void DrawGui()
        {
            GUILayout.Label($"Min distance: {MinDistanceMeters:F0} m");
            MinDistanceMeters = GUILayout.HorizontalSlider(MinDistanceMeters, 100f, 3000f);

            GUILayout.Label($"Steady accel threshold: {SteadyAccelThreshold:F2} m/s²");
            SteadyAccelThreshold = GUILayout.HorizontalSlider(SteadyAccelThreshold, 0.05f, 3f);

            GUILayout.Label($"Required steady time: {RequiredSteadySeconds:F1} s");
            RequiredSteadySeconds = GUILayout.HorizontalSlider(RequiredSteadySeconds, 0.5f, 15f);

            GUILayout.Label($"Low solver iterations: {LowSolverIterations}");
            LowSolverIterations = Mathf.RoundToInt(GUILayout.HorizontalSlider(LowSolverIterations, 1f, 6f));

            GUILayout.Space(8f);
            RunExperiment = GUILayout.Toggle(RunExperiment, "Run A/B experiment (alternates every window)");

            GUILayout.Label($"Experiment window: {ExperimentWindowSeconds:F0} s");
            ExperimentWindowSeconds = GUILayout.HorizontalSlider(ExperimentWindowSeconds, 10f, 120f);
        }
    }
}
