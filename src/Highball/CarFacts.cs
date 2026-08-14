namespace Highball
{
    /// <summary>
    /// Per-car facts computed once per evaluation pass and read by every feature.
    /// Computing these once rather than per-feature keeps the pass cost independent of
    /// how many features are enabled.
    /// </summary>
    internal struct CarFacts
    {
        /// <summary>Metres from the main camera. The only fact any feature acts on today.</summary>
        internal float Distance;

        /// <summary>Speed in m/s. Reported, not acted on: it drives the `moving` telemetry column.</summary>
        internal float Speed;
    }
}
