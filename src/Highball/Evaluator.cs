using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Walks the car table once per interval and fills in CarFacts. Knows nothing about
    /// what any feature intends to do with them.
    /// </summary>
    internal sealed class Evaluator
    {
        internal int MovingCount { get; private set; }

        internal void Evaluate(System.Collections.Generic.IList<TrackedCar> cars, float deltaTime)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            Settings s = Settings.Instance;
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
                float accel = Mathf.Abs(speed - car.Facts.Speed) / Mathf.Max(deltaTime, 0.0001f);

                if (speed > s.MovingSpeedThreshold)
                {
                    moving++;
                }

                CarFacts facts;
                facts.Distance = Vector3.Distance(rb.position, eye);
                facts.Speed = speed;
                facts.Acceleration = accel;
                facts.SteadySeconds = Decisions.AccumulateCalm(
                    car.Facts.SteadySeconds, accel, s.SteadyAccelThreshold, deltaTime);
                facts.StationarySeconds = Decisions.AccumulateCalm(
                    car.Facts.StationarySeconds, speed, s.MovingSpeedThreshold, deltaTime);
                facts.IsAsleep = rb.IsSleeping();

                car.Facts = facts;
            }

            MovingCount = moving;
        }
    }
}
