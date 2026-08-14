using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Read-only. Answers one question before any sleep code is written: how many cars are
    /// parked but still awake, and therefore actually addressable by forcing sleep?
    ///
    /// The premise is genuinely uncertain in both directions. PhysX auto-sleeps bodies
    /// whose mass-normalized kinetic energy stays under sleepThreshold, which would make
    /// forcing sleep redundant. But bodies in constant contact or bound by joints
    /// routinely fail to auto-sleep, and rolling stock sits on track colliders with bogies
    /// and coupler constraints, which is exactly that configuration.
    /// </summary>
    internal sealed class SleepHeadroomProbe : IFeature
    {
        private readonly IList<TrackedCar> _cars;

        private int _asleep;
        private int _stationary;
        private int _stationaryAwake;
        private int _tracked;

        internal SleepHeadroomProbe(IList<TrackedCar> cars)
        {
            _cars = cars;
        }

        public string Id { get { return "sleep_headroom"; } }
        public string DisplayName { get { return "Sleep headroom probe (read-only)"; } }
        public bool IsExperimental { get { return false; } }
        public bool Enabled { get { return Settings.Instance.EnableSleepHeadroomProbe; } }
        public bool Active { get; set; }

        public void Tick(float deltaTime)
        {
            int asleep = 0, stationary = 0, stationaryAwake = 0, tracked = 0;

            for (int i = 0; i < _cars.Count; i++)
            {
                TrackedCar car = _cars[i];
                if (car?.Rigidbody == null) continue;

                tracked++;
                bool isStationary = car.Facts.Speed <= Settings.Instance.MovingSpeedThreshold;

                if (car.Facts.IsAsleep) asleep++;
                if (isStationary) stationary++;
                if (isStationary && !car.Facts.IsAsleep) stationaryAwake++;
            }

            _asleep = asleep;
            _stationary = stationary;
            _stationaryAwake = stationaryAwake;
            _tracked = tracked;
        }

        public void ReleaseAll() { }

        public string[] TelemetryHeaders
        {
            get { return new[] { "asleep", "stationary", "stationary_awake" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                return new[]
                {
                    _asleep.ToString(CultureInfo.InvariantCulture),
                    _stationary.ToString(CultureInfo.InvariantCulture),
                    _stationaryAwake.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        internal void DrawStatus()
        {
            if (!Enabled)
            {
                return;
            }

            GUILayout.Label(string.Format(
                "asleep {0}   stationary {1}   stationary+awake {2}   verdict: {3}",
                _asleep, _stationary, _stationaryAwake,
                Decisions.ClassifyHeadroom(_stationaryAwake, _tracked)));
        }
    }
}
