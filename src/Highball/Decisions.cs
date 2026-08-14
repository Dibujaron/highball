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
        /// The mirror of <see cref="ClampReduction"/>, for settings where a LARGER value
        /// means less work — the physics timestep being the case in hand, since a longer
        /// step means fewer steps per second. Same principle, opposite direction: a feature
        /// may only ever reduce work, so a configured value below the game's own is ignored
        /// rather than allowed to make the simulation run more often than it already does.
        /// </summary>
        internal static float ClampRelaxation(float configured, float original)
        {
            return configured > original ? configured : original;
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
