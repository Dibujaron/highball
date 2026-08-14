using System.Collections.Generic;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Reduces PhysX solver iterations on distant rolling stock in steady-state motion.
    ///
    /// Experimental. Its only measurement so far put ACTIVE 4.6 fps slower than BASELINE
    /// across a single window per arm, well inside a measured +/-9 fps noise floor. That
    /// is not evidence of harm, but it is not evidence of benefit either, so this ships
    /// off until a run of four or more windows per arm says otherwise.
    /// </summary>
    internal sealed class SolverLodFeature : ICarFeature
    {
        private readonly List<TrackedCar> _held = new List<TrackedCar>();

        public string Id { get { return "solver_lod"; } }
        public string DisplayName { get { return "Solver iteration LOD"; } }
        public bool IsExperimental { get { return true; } }
        public bool Enabled { get { return Settings.Instance.EnableSolverLod; } }
        public bool Active { get; set; }

        /// <summary>Acts only through claim arbitration; nothing to do on a tick.</summary>
        public void Tick(float deltaTime) { }

        public bool TryClaim(TrackedCar car)
        {
            Settings s = Settings.Instance;

            if (!Decisions.QualifiesForSolverLod(
                    car.Facts.Distance, car.Facts.SteadySeconds,
                    s.MinDistanceMeters, s.RequiredSteadySeconds))
            {
                return false;
            }

            if (car.IsDowngraded)
            {
                return true;
            }

            try
            {
                car.OriginalSolverIterations = car.Rigidbody.solverIterations;
                car.Rigidbody.solverIterations = s.LowSolverIterations;
                car.IsDowngraded = true;
                _held.Add(car);
            }
            catch
            {
                // A destroyed rigidbody is reaped on the next refresh.
                return false;
            }

            return true;
        }

        public void Release(TrackedCar car)
        {
            if (!car.IsDowngraded)
            {
                return;
            }

            try
            {
                car.Rigidbody.solverIterations = car.OriginalSolverIterations;
            }
            catch
            {
                // Same as above; nothing useful to do.
            }

            car.IsDowngraded = false;
            _held.Remove(car);
        }

        public void ReleaseAll()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                Release(_held[i]);
            }

            _held.Clear();
        }

        public string[] TelemetryHeaders { get { return new[] { "solver_downgraded" }; } }

        public string[] TelemetryValues
        {
            get { return new[] { _held.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }; }
        }
    }
}
