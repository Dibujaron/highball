using System;
using System.Collections.Generic;
using System.Reflection;
using Model;
using RollingStock;
using UnityEngine;

namespace StockPhysicsLOD
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
    /// </summary>
    internal sealed class LodManager
    {
        private sealed class CarState
        {
            public string Id;
            public Car Car;
            public Rigidbody Rigidbody;

            public int OriginalSolverIterations;
            public bool IsDowngraded;

            public float LastSpeed;
            public float SteadySeconds;
        }

        private readonly Dictionary<string, CarState> _byId = new Dictionary<string, CarState>();
        private readonly List<CarState> _cars = new List<CarState>();

        private FieldInfo _recordsField;
        private FieldInfo _recordCarField;

        private float _refreshTimer;
        private float _evalTimer;

        // Live counters, surfaced in the UMM panel and the experiment log.
        internal int TrackedCount => _cars.Count;
        internal int DowngradedCount { get; private set; }
        internal int MovingCount { get; private set; }
        internal int EligibleCount { get; private set; }

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
                RefreshCars();
            }

            if (_evalTimer >= Settings.Instance.EvaluateIntervalSeconds)
            {
                float dt = _evalTimer;
                _evalTimer = 0f;
                Evaluate(dt);
            }
        }

        // --- discovery ---

        /// <summary>
        /// Enumerates rolling stock via CarCuller's private record list. This mirrors how
        /// RollingStock Optimizer finds cars; the game exposes no public equivalent.
        /// </summary>
        private void RefreshCars()
        {
            try
            {
                CarCuller culler = UnityEngine.Object.FindObjectOfType<CarCuller>();
                if (culler == null)
                {
                    return;
                }

                if (_recordsField == null)
                {
                    _recordsField = typeof(CarCuller).GetField(
                        "_records",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    if (_recordsField == null)
                    {
                        Main.Log("CarCuller._records not found; this game version is unsupported.");
                        return;
                    }
                }

                var records = _recordsField.GetValue(culler) as System.Collections.IList;
                if (records == null)
                {
                    return;
                }

                var seen = new HashSet<string>();

                foreach (object record in records)
                {
                    if (record == null)
                    {
                        continue;
                    }

                    if (_recordCarField == null)
                    {
                        _recordCarField = record.GetType().GetField(
                            "Car", BindingFlags.Public | BindingFlags.Instance);

                        if (_recordCarField == null)
                        {
                            return;
                        }
                    }

                    Car car = _recordCarField.GetValue(record) as Car;
                    if (car == null || string.IsNullOrEmpty(car.id))
                    {
                        continue;
                    }

                    seen.Add(car.id);

                    if (_byId.ContainsKey(car.id))
                    {
                        continue;
                    }

                    GameObject go = car.gameObject;
                    Rigidbody rb = go != null ? go.GetComponent<Rigidbody>() : null;
                    if (rb == null)
                    {
                        continue;
                    }

                    var state = new CarState
                    {
                        Id = car.id,
                        Car = car,
                        Rigidbody = rb,
                        OriginalSolverIterations = rb.solverIterations,
                        IsDowngraded = false,
                        LastSpeed = rb.velocity.magnitude,
                        SteadySeconds = 0f
                    };

                    _cars.Add(state);
                    _byId[car.id] = state;
                }

                // Drop cars that have left the world, restoring them first if we touched them.
                for (int i = _cars.Count - 1; i >= 0; i--)
                {
                    CarState state = _cars[i];
                    bool gone = state == null
                                || state.Car == null
                                || state.Rigidbody == null
                                || !seen.Contains(state.Id);

                    if (!gone)
                    {
                        continue;
                    }

                    if (state != null)
                    {
                        Restore(state);
                        _byId.Remove(state.Id);
                    }

                    _cars.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                Main.Log("RefreshCars failed: " + ex.Message);
            }
        }

        // --- classification ---

        private void Evaluate(float deltaTime)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            Settings s = Settings.Instance;

            float minDistSq = s.MinDistanceMeters * s.MinDistanceMeters;

            int downgraded = 0;
            int moving = 0;
            int eligible = 0;

            for (int i = 0; i < _cars.Count; i++)
            {
                CarState state = _cars[i];
                if (state?.Rigidbody == null || state.Car == null)
                {
                    continue;
                }

                Rigidbody rb = state.Rigidbody;

                float speed = rb.velocity.magnitude;
                float accel = Mathf.Abs(speed - state.LastSpeed) / Mathf.Max(deltaTime, 0.0001f);
                state.LastSpeed = speed;

                if (speed > s.MovingSpeedThreshold)
                {
                    moving++;
                }

                // Sustained calm is required before a downgrade; any jolt resets the clock.
                // Coupling, braking and slack action all show up here as acceleration.
                if (accel <= s.SteadyAccelThreshold)
                {
                    state.SteadySeconds += deltaTime;
                }
                else
                {
                    state.SteadySeconds = 0f;
                }

                float distSq = (rb.position - eye).sqrMagnitude;

                bool qualifies = Active
                                 && distSq > minDistSq
                                 && state.SteadySeconds >= s.RequiredSteadySeconds;

                if (qualifies)
                {
                    eligible++;
                    Downgrade(state);
                }
                else
                {
                    Restore(state);
                }

                if (state.IsDowngraded)
                {
                    downgraded++;
                }
            }

            DowngradedCount = downgraded;
            MovingCount = moving;
            EligibleCount = eligible;
        }

        // --- apply / restore ---

        private void Downgrade(CarState state)
        {
            if (state.IsDowngraded)
            {
                return;
            }

            try
            {
                state.Rigidbody.solverIterations = Settings.Instance.LowSolverIterations;
                state.IsDowngraded = true;
            }
            catch
            {
                // A destroyed rigidbody will be reaped on the next refresh.
            }
        }

        private void Restore(CarState state)
        {
            if (!state.IsDowngraded)
            {
                return;
            }

            try
            {
                state.Rigidbody.solverIterations = state.OriginalSolverIterations;
            }
            catch
            {
                // Same as above; nothing useful to do.
            }

            state.IsDowngraded = false;
        }

        /// <summary>
        /// Unconditional restore. Called on toggle-off, on unload, and whenever the
        /// experiment switches to a baseline window.
        /// </summary>
        internal void RestoreAll()
        {
            for (int i = 0; i < _cars.Count; i++)
            {
                if (_cars[i] != null)
                {
                    Restore(_cars[i]);
                }
            }

            DowngradedCount = 0;
        }

        internal void Clear()
        {
            RestoreAll();
            _cars.Clear();
            _byId.Clear();
        }
    }
}
