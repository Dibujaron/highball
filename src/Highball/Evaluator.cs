using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Walks the car table once per interval and fills in CarFacts. Knows nothing about
    /// what any feature intends to do with them.
    /// </summary>
    internal sealed class Evaluator
    {
        /// <summary>
        /// Speed (m/s) above which a car counts as moving. Reporting only — it decides the
        /// `moving` telemetry column, which exists so two telemetry windows can be checked
        /// for comparable workload. No feature acts on it, so it is a constant rather than
        /// another slider in the panel.
        /// </summary>
        private const float MovingSpeedThreshold = 0.1f;

        internal int MovingCount { get; private set; }

        internal void Evaluate(System.Collections.Generic.IList<TrackedCar> cars)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            int moving = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                TrackedCar car = cars[i];
                if (car?.Rigidbody == null || car.Car == null)
                {
                    continue;
                }

                Rigidbody rb = car.Rigidbody;
                float speed = rb.velocity.magnitude;

                if (speed > MovingSpeedThreshold)
                {
                    moving++;
                }

                CarFacts facts;
                facts.Distance = Vector3.Distance(rb.position, eye);
                facts.Speed = speed;

                car.Facts = facts;
            }

            MovingCount = moving;
        }
    }
}
