namespace Highball
{
    /// <summary>
    /// One optimization, independently toggleable. Car-shaped features are offered each
    /// car in a fixed priority order and the first one to claim it acts on it, so two
    /// features can never mutate the same rigidbody. Global-state features are driven by
    /// Tick instead.
    /// </summary>
    internal interface IFeature
    {
        string Id { get; }
        string DisplayName { get; }

        /// <summary>The player's toggle, backed by Settings.</summary>
        bool Enabled { get; }

        /// <summary>
        /// Called once per evaluation pass on features that are Enabled. Features that act
        /// on global state rather than individual cars do their work here. Car-shaped
        /// features can leave it empty.
        /// </summary>
        void Tick(float deltaTime);

        /// <summary>Hands everything back, unconditionally.</summary>
        void ReleaseAll();

        string[] TelemetryHeaders { get; }
        string[] TelemetryValues { get; }
    }

    /// <summary>
    /// A feature that acts on individual cars, and therefore participates in claim
    /// arbitration. The first enabled ICarFeature to claim a car acts on it.
    /// </summary>
    internal interface ICarFeature : IFeature
    {
        bool TryClaim(TrackedCar car);
        void Release(TrackedCar car);
    }
}
