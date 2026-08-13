namespace Highball
{
    /// <summary>
    /// One optimization, independently toggleable. Features are offered each car in a
    /// fixed priority order and the first one to claim it acts on it, so two features can
    /// never mutate the same rigidbody.
    /// </summary>
    internal interface IFeature
    {
        string Id { get; }
        string DisplayName { get; }

        /// <summary>Shown in the panel. Experimental features ship off.</summary>
        bool IsExperimental { get; }

        /// <summary>The player's toggle, backed by Settings.</summary>
        bool Enabled { get; }

        /// <summary>
        /// Flipped by the A/B harness when this feature is the experiment target. A
        /// feature that is Enabled but not Active must claim nothing and release
        /// everything, so a BASELINE window is a true control.
        /// </summary>
        bool Active { get; set; }

        /// <summary>Returns true if this feature took the car and acted on it.</summary>
        bool TryClaim(TrackedCar car);

        /// <summary>Hands one car back, unconditionally.</summary>
        void Release(TrackedCar car);

        /// <summary>Hands every car back, unconditionally.</summary>
        void ReleaseAll();

        string[] TelemetryHeaders { get; }
        string[] TelemetryValues { get; }
    }
}
