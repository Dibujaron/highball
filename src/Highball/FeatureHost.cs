using System;
using System.Collections.Generic;

namespace Highball
{
    /// <summary>
    /// Offers each car to features in priority order. The first enabled ICarFeature to
    /// claim a car acts on it and every later feature is asked to release it, so two
    /// features can never mutate the same car in the same pass.
    /// </summary>
    internal sealed class FeatureHost
    {
        private readonly IFeature[] _features;

        // Feature ids that have already logged a full exception from Apply()'s per-car,
        // per-feature try/catch below. On a 519-car save with two ICarFeatures ticking at
        // the evaluate cadence, an unthrottled per-occurrence log there is up to ~4,000
        // full stack traces per second for the rest of the session. This bounds it to one
        // full trace per feature id, ever, while still catching (and isolating) every
        // single throw.
        private readonly HashSet<string> _reportedApplyFailures = new HashSet<string>();

        internal FeatureHost(IFeature[] featuresInPriorityOrder)
        {
            _features = featuresInPriorityOrder;
        }

        internal IFeature[] Features { get { return _features; } }

        internal IFeature Find(string id)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                if (_features[i].Id == id) return _features[i];
            }

            return null;
        }

        internal void Apply(IList<TrackedCar> cars)
        {
            for (int c = 0; c < cars.Count; c++)
            {
                TrackedCar car = cars[c];
                if (car?.Rigidbody == null)
                {
                    continue;
                }

                bool claimed = false;

                for (int f = 0; f < _features.Length; f++)
                {
                    ICarFeature feature = _features[f] as ICarFeature;
                    if (feature == null)
                    {
                        continue;
                    }

                    // Isolated per feature, matching Tick/ReleaseAll below: a throwing
                    // TryClaim or Release must not abort the pass for the remaining cars or
                    // features. Without this, one bad feature mid-array could leave every
                    // later car unprocessed for the rest of that Apply() call.
                    try
                    {
                        if (claimed || !feature.Enabled)
                        {
                            feature.Release(car);
                            continue;
                        }

                        if (feature.TryClaim(car))
                        {
                            claimed = true;
                        }
                        else
                        {
                            feature.Release(car);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Throttled, not silenced: this runs per car per feature, so a
                        // feature that throws every pass would otherwise flood the log —
                        // up to ~4,000 lines/sec on a 519-car save. Log the full exception
                        // once per feature id, then suppress; the isolation above still
                        // catches and skips every single throw regardless.
                        if (_reportedApplyFailures.Add(feature.Id))
                        {
                            Main.Log("Feature '" + feature.Id + "' threw from Apply(); further Apply() " +
                                     "failures for this feature will be suppressed this session: " + ex);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Drives features that act on their own schedule. A disabled feature is released
        /// here rather than merely skipped, so a runtime toggle-off restores immediately
        /// rather than waiting for a tick.
        /// </summary>
        internal void Tick(float deltaTime)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                IFeature f = _features[i];

                try
                {
                    if (f.Enabled)
                    {
                        f.Tick(deltaTime);
                    }
                    else
                    {
                        f.ReleaseAll();
                    }
                }
                catch (Exception ex)
                {
                    Main.Log("Feature '" + f.Id + "' threw from Tick/ReleaseAll: " + ex);
                }
            }
        }

        /// <summary>
        /// Every feature releases this one car. Used when a car leaves the world, from
        /// CarRegistry.OnCarRemoved. Each feature's Release(car) is isolated in its own
        /// try/catch, matching the other ReleaseAll overload and Telemetry.ApplyMode: a throw
        /// from one feature must not stop a later feature from releasing, and must not
        /// propagate into CarRegistry.Refresh's outer catch and abort the whole reaping pass
        /// — which would leave every car after the throwing one, in every remaining pass,
        /// still claimed and still at reduced fidelity.
        /// </summary>
        internal void ReleaseAll(TrackedCar car)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                ICarFeature feature = _features[i] as ICarFeature;
                if (feature == null)
                {
                    continue;
                }

                try
                {
                    feature.Release(car);
                }
                catch (Exception ex)
                {
                    Main.Log("Feature '" + feature.Id + "' threw from Release(): " + ex);
                }
            }
        }

        /// <summary>
        /// Every feature releases everything, regardless of its enabled state — a feature
        /// switched off at runtime must still hand back what it was holding. This is the
        /// mod's only unconditional-restore path, so one feature throwing must not stop
        /// the rest from releasing.
        /// </summary>
        internal void ReleaseAll(IList<TrackedCar> cars)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                try
                {
                    _features[i].ReleaseAll();
                }
                catch (Exception ex)
                {
                    Main.Log("Feature '" + _features[i].Id + "' threw from ReleaseAll(): " + ex);
                }
            }
        }
    }
}
