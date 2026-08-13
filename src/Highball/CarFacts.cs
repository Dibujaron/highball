namespace Highball
{
    /// <summary>
    /// Per-car facts computed once per evaluation pass and read by every feature.
    /// Computing these once rather than per-feature keeps the pass cost independent of
    /// how many features are enabled.
    /// </summary>
    internal struct CarFacts
    {
        internal float Distance;
        internal float Speed;
        internal float Acceleration;

        /// <summary>Continuous seconds under the acceleration threshold.</summary>
        internal float SteadySeconds;

        /// <summary>Continuous seconds under the speed threshold. Not the same as steady:
        /// a consist at constant speed is steady but emphatically not stationary.</summary>
        internal float StationarySeconds;

        internal bool IsAsleep;
    }
}
