using System.Collections.Generic;

namespace Highball
{
    /// <summary>
    /// Reduces PhysX solver iterations on rolling stock that is far away and in
    /// steady-state motion, and restores them the moment anything interesting happens.
    ///
    /// The core bet: a boxcar cruising at constant speed 800m away does not need six
    /// solver iterations per step to look and behave correctly. A boxcar being coupled,
    /// braked, or watched does.
    ///
    /// Safety model is restore-biased. Downgrades require sustained calm; restores are
    /// immediate and unconditional. Anything we are unsure about stays at full fidelity.
    ///
    /// Discovery lives in CarRegistry and per-pass facts live in Evaluator; this class
    /// owns only the classification threshold and the solver-iteration action itself.
    /// A later task moves the action into a feature.
    /// </summary>
    internal sealed class LodManager
    {
        private readonly CarRegistry _registry = new CarRegistry();
        private readonly Evaluator _evaluator = new Evaluator();

        private float _refreshTimer;
        private float _evalTimer;

        // Live counters, surfaced in the UMM panel and the experiment log.
        internal int TrackedCount => _registry.TrackedCount;
        internal int DowngradedCount { get; private set; }
        internal int MovingCount => _evaluator.MovingCount;
        internal int EligibleCount { get; private set; }

        /// <summary>Exposed so Main can wire reaping back to Restore.</summary>
        internal CarRegistry Registry => _registry;

        /// <summary>
        /// When false, every downgraded car is restored and none are downgraded again.
        /// The experiment harness flips this to produce A/B windows.
        /// </summary>
        internal bool Active { get; private set; }

        internal void SetActive(bool value)
        {
            if (Active == value)
            {
                return;
            }

            Active = value;

            if (!value)
            {
                RestoreAll();
            }
        }

        internal void Tick(float deltaTime)
        {
            _refreshTimer += deltaTime;
            _evalTimer += deltaTime;

            if (_refreshTimer >= Settings.Instance.RefreshIntervalSeconds)
            {
                _refreshTimer = 0f;
                _registry.Refresh();
            }

            if (_evalTimer >= Settings.Instance.EvaluateIntervalSeconds)
            {
                float dt = _evalTimer;
                _evalTimer = 0f;
                _evaluator.Evaluate(_registry.Cars, dt);
                Classify();
            }
        }

        // --- classification ---

        private void Classify()
        {
            IList<TrackedCar> cars = _registry.Cars;
            Settings s = Settings.Instance;

            int downgraded = 0;
            int eligible = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                TrackedCar car = cars[i];
                if (car?.Rigidbody == null || car.Car == null)
                {
                    continue;
                }

                // Distance and sustained calm are computed once by the Evaluator; here we
                // only apply the threshold.
                bool qualifies = Active
                                 && car.Facts.Distance > s.MinDistanceMeters
                                 && car.Facts.SteadySeconds >= s.RequiredSteadySeconds;

                if (qualifies)
                {
                    eligible++;
                    Downgrade(car);
                }
                else
                {
                    Restore(car);
                }

                if (car.IsDowngraded)
                {
                    downgraded++;
                }
            }

            DowngradedCount = downgraded;
            EligibleCount = eligible;
        }

        // --- apply / restore ---

        private void Downgrade(TrackedCar car)
        {
            if (car.IsDowngraded)
            {
                return;
            }

            try
            {
                car.Rigidbody.solverIterations = Settings.Instance.LowSolverIterations;
                car.IsDowngraded = true;
            }
            catch
            {
                // A destroyed rigidbody will be reaped on the next refresh.
            }
        }

        /// <summary>
        /// Restores a single car's solver iterations, if it was downgraded. Internal
        /// rather than private because CarRegistry's reaping loop needs to call back into
        /// it: a car leaving the world must be handed back before it is dropped, and
        /// CarRegistry has no feature state of its own to do that with.
        /// </summary>
        internal void Restore(TrackedCar car)
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
        }

        /// <summary>
        /// Unconditional restore. Called on toggle-off, on unload, and whenever the
        /// experiment switches to a baseline window.
        /// </summary>
        internal void RestoreAll()
        {
            IList<TrackedCar> cars = _registry.Cars;
            for (int i = 0; i < cars.Count; i++)
            {
                if (cars[i] != null)
                {
                    Restore(cars[i]);
                }
            }

            DowngradedCount = 0;
        }

        internal void Clear()
        {
            RestoreAll();
            _registry.Clear();
        }
    }
}
