namespace Highball
{
    /// <summary>
    /// Every decision the mod makes that can be expressed in primitives, with no Unity
    /// types. Isolated here so it can be compiled into a console test runner and given a
    /// real red-green loop; the game itself cannot be scripted.
    /// </summary>
    internal static class Decisions
    {
        /// <summary>
        /// Advances a calm clock. Any excursion above the threshold resets it to zero,
        /// so the clock measures *continuous* calm rather than total calm.
        /// </summary>
        internal static float AccumulateCalm(float current, float value, float threshold, float deltaTime)
        {
            return value <= threshold ? current + deltaTime : 0f;
        }

        internal static bool QualifiesForSolverLod(
            float distance, float steadySeconds, float minDistance, float requiredSteadySeconds)
        {
            return distance > minDistance && steadySeconds >= requiredSteadySeconds;
        }

        /// <summary>
        /// A car may be slept only when it is far away, has been genuinely at rest for
        /// long enough, is not already asleep, and belongs to no moving consist. The
        /// consist guard matters because a car mid-train can read near-zero speed during
        /// slack action while its train is moving.
        /// </summary>
        internal static bool QualifiesForSleep(
            float distance, float stationarySeconds, bool isAsleep, bool consistMoving,
            float minDistance, float requiredStationarySeconds)
        {
            if (isAsleep || consistMoving) return false;
            return distance > minDistance && stationarySeconds >= requiredStationarySeconds;
        }

        /// <summary>
        /// The spec's pre-agreed decision rule for whether forcing sleep is worth building.
        /// Agreed before any data was collected, deliberately.
        /// </summary>
        internal static string ClassifyHeadroom(int stationaryAwake, int tracked)
        {
            if (tracked <= 0) return "none";

            float share = (float)stationaryAwake / tracked;
            if (share < 0.10f) return "none";
            return share <= 0.30f ? "marginal" : "real";
        }
    }
}
