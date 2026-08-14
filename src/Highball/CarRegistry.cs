using System;
using System.Collections.Generic;
using System.Reflection;
using Model;
using RollingStock;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Discovers rolling stock in the scene and keeps the tracked-car table current.
    /// Pure discovery: computes no facts and makes no fidelity decisions. Those are the
    /// Evaluator's and the features' jobs, respectively.
    /// </summary>
    internal sealed class CarRegistry
    {
        private readonly Dictionary<string, TrackedCar> _byId = new Dictionary<string, TrackedCar>();
        private readonly List<TrackedCar> _cars = new List<TrackedCar>();

        private FieldInfo _recordsField;
        private FieldInfo _recordCarField;

        // Discovery diagnostics. The first version of this class reported zero tracked
        // cars and logged nothing at all, which made the failure impossible to locate.
        // Every discovery path now accounts for itself.
        private bool _discoveryReported;
        private int _diagRecords;
        private int _diagRbOnRoot;
        private int _diagRbInChildren;
        private int _diagNoRigidbody;

        private readonly HashSet<string> _reported = new HashSet<string>();

        internal IList<TrackedCar> Cars => _cars;
        internal int TrackedCount => _cars.Count;

        /// <summary>
        /// Invoked once per car dropped during reaping, before it leaves the table.
        /// CarRegistry holds no feature state and cannot undo whatever a feature did to
        /// the car, so it hands the car back to whoever does.
        /// </summary>
        internal Action<TrackedCar> OnCarRemoved;

        /// <summary>
        /// Enumerates rolling stock via CarCuller's private record list. The game exposes
        /// no public equivalent, so reflection over that list is the only route in.
        /// </summary>
        internal void Refresh()
        {
            try
            {
                CarCuller culler = UnityEngine.Object.FindObjectOfType<CarCuller>();
                if (culler == null)
                {
                    // Expected before the world finishes loading, but if it never resolves
                    // we need to know rather than silently track nothing forever.
                    ReportOnce("CarCuller not found in scene.");
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
                    ReportOnce("CarCuller._records was null or not an IList.");
                    return;
                }

                var seen = new HashSet<string>();

                _diagRecords = records.Count;
                _diagRbOnRoot = 0;
                _diagRbInChildren = 0;
                _diagNoRigidbody = 0;

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
                            ReportOnce("Culler record type " + record.GetType().FullName
                                       + " has no public 'Car' field.");
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

                    // The body is not reliably on the car's root object. Fall back to a
                    // child search before giving up, and record which path succeeded.
                    Rigidbody rb = go != null ? go.GetComponent<Rigidbody>() : null;
                    if (rb != null)
                    {
                        _diagRbOnRoot++;
                    }
                    else if (go != null)
                    {
                        rb = go.GetComponentInChildren<Rigidbody>(true);
                        if (rb != null)
                        {
                            _diagRbInChildren++;
                        }
                    }

                    if (rb == null)
                    {
                        _diagNoRigidbody++;
                        continue;
                    }

                    // Facts are left at their default and filled in by the Evaluator's next
                    // pass. CarRendererFeature's scratch fields (see TrackedCar) are left
                    // default too: it gathers renderers and captures their original shadow
                    // modes itself, on its first suppression, so a capture here would never
                    // be what gets restored — only a dead write inviting the next reader to
                    // trust it.
                    var tracked = new TrackedCar
                    {
                        Id = car.id,
                        Car = car,
                        Rigidbody = rb
                    };

                    _cars.Add(tracked);
                    _byId[car.id] = tracked;
                }

                // Drop cars that have left the world. CarRegistry has no feature state to
                // undo, so it hands the car to OnCarRemoved before dropping it.
                for (int i = _cars.Count - 1; i >= 0; i--)
                {
                    TrackedCar tracked = _cars[i];
                    bool gone = tracked == null
                                || tracked.Car == null
                                || tracked.Rigidbody == null
                                || !seen.Contains(tracked.Id);

                    if (!gone)
                    {
                        continue;
                    }

                    if (tracked != null)
                    {
                        if (OnCarRemoved != null)
                        {
                            OnCarRemoved(tracked);
                        }

                        _byId.Remove(tracked.Id);
                    }

                    _cars.RemoveAt(i);
                }

                if (!_discoveryReported && _diagRecords > 0)
                {
                    _discoveryReported = true;
                    Main.Log(string.Format(
                        "Discovery: {0} culler records -> {1} tracked (rb on root: {2}, rb in children: {3}, no rigidbody: {4}).",
                        _diagRecords, _cars.Count, _diagRbOnRoot, _diagRbInChildren, _diagNoRigidbody));
                }
            }
            catch (Exception ex)
            {
                Main.Log("RefreshCars failed: " + ex);
            }
        }

        /// <summary>Logs a given message at most once per session, to keep hot paths quiet.</summary>
        private void ReportOnce(string message)
        {
            if (_reported.Add(message))
            {
                Main.Log(message);
            }
        }

        /// <summary>
        /// Drops every tracked car without notifying OnCarRemoved. Callers that need cars
        /// restored first must do that themselves before calling Clear.
        /// </summary>
        internal void Clear()
        {
            _cars.Clear();
            _byId.Clear();
        }
    }
}
