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

        /// <summary>
        /// A feature may only ever reduce work. Railroader sets its own values and we do
        /// not learn them until runtime, so a fixed configured value can easily be higher
        /// than the game's — which would turn an optimization into a de-optimization,
        /// silently, while the panel claimed the feature was on.
        /// </summary>
        internal static float ClampReduction(float configured, float original)
        {
            return configured < original ? configured : original;
        }

        internal static int ClampReductionInt(int configured, int original)
        {
            return configured < original ? configured : original;
        }

        /// <summary>
        /// Edge-triggered distance test with a hysteresis band. Suppression begins past
        /// the threshold and does not end until the object is well inside it. Without the
        /// band, an object hovering at the boundary would flip state every pass, and for
        /// the renderer feature that means thousands of component writes per second —
        /// a performance mod making things worse.
        /// </summary>
        internal static bool ShouldSuppressAtDistance(
            float distance, float threshold, float hysteresisMeters, bool currentlySuppressed)
        {
            if (currentlySuppressed)
            {
                return distance >= threshold - hysteresisMeters;
            }

            return distance > threshold;
        }

        /// <summary>
        /// Caps a preferred hysteresis margin so it can never reach the threshold itself.
        /// A margin at or beyond the threshold makes ShouldSuppressAtDistance's restore
        /// test (`distance >= threshold - margin`) true at every distance once suppressed,
        /// meaning a suppressed object could never be un-suppressed by moving closer,
        /// however close it gets. Settings sliders are configured independently of any
        /// feature's fixed preferred margin, so a small enough threshold value (e.g. this
        /// mod's own slider minimum) can reach that condition. Capping at half the
        /// threshold guarantees the restore band never swallows the whole eligible range;
        /// clamping at zero guarantees a degenerate (zero or negative) threshold can never
        /// produce a negative margin.
        /// </summary>
        internal static float EffectiveHysteresis(float threshold, float preferredMargin)
        {
            float cap = threshold * 0.5f;
            float margin = preferredMargin < cap ? preferredMargin : cap;
            return margin < 0f ? 0f : margin;
        }
    }
}
