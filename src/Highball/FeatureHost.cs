using System.Collections.Generic;

namespace Highball
{
    /// <summary>
    /// Offers each car to features in priority order. Sleeping dominates solver LOD —
    /// a sleeping body is skipped by the solver entirely, so lowering its iteration count
    /// would be meaningless — hence sleep sits earlier in the array.
    /// </summary>
    internal sealed class FeatureHost
    {
        private readonly IFeature[] _features;

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

                string claimed = null;

                for (int f = 0; f < _features.Length; f++)
                {
                    IFeature feature = _features[f];

                    if (claimed != null || !feature.Enabled || !feature.Active)
                    {
                        // Either someone already owns this car, or this feature is off.
                        // Either way it must not be holding it.
                        feature.Release(car);
                        continue;
                    }

                    if (feature.TryClaim(car))
                    {
                        claimed = feature.Id;
                    }
                    else
                    {
                        feature.Release(car);
                    }
                }

                car.ClaimedBy = claimed;
            }
        }

        /// <summary>Every feature releases this one car. Used when a car leaves the world.</summary>
        internal void ReleaseAll(TrackedCar car)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                _features[i].Release(car);
            }

            car.ClaimedBy = null;
        }

        /// <summary>
        /// Every feature releases everything, regardless of its enabled state — a feature
        /// switched off at runtime must still hand back what it was holding.
        /// </summary>
        internal void ReleaseAll()
        {
            for (int i = 0; i < _features.Length; i++)
            {
                _features[i].ReleaseAll();
            }
        }
    }
}
