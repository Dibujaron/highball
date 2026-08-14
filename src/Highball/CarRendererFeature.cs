using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace Highball
{
    /// <summary>
    /// Stops distant rolling stock from casting shadows. A shadow-casting renderer is
    /// drawn again per shadow-casting light, so suppressing it removes draw calls; a
    /// distant car's own shadow is a few pixels and its loss is not visible in practice.
    ///
    /// Never disables a renderer. That would make cars vanish, which is the mistake this
    /// project already refused to make with tree cull distance.
    ///
    /// Edge-triggered: renderer arrays are gathered lazily on first suppression and writes
    /// happen only on a threshold transition, with a hysteresis band. Walking 519 cars'
    /// worth of renderers every pass would cost more than it saves.
    ///
    /// Holds its own list of suppressed cars rather than the "nothing held globally"
    /// design first sketched for this feature: FeatureHost.ReleaseAll(IList&lt;TrackedCar&gt;)
    /// — the path used on whole-mod disable (Main.OnToggle) and OnUnload — calls each
    /// feature's parameterless ReleaseAll() WITHOUT ever passing it the car list or going
    /// through Apply/Release(car). A ReleaseAll() with no held list of its own would leave
    /// every currently-suppressed car's shadowCastingMode stuck at Off forever: Apply()
    /// never runs again after that path (the registry is cleared right after), and a car
    /// rediscovered later gets a brand-new TrackedCar with ShadowsSuppressed defaulting to
    /// false, so nothing would ever know the real Renderer components still need restoring.
    /// Tracking held cars here closes that gap.
    /// </summary>
    internal sealed class CarRendererFeature : ICarFeature
    {
        private const float HysteresisMeters = 50f;

        private readonly List<TrackedCar> _held = new List<TrackedCar>();

        private int _renderersTouched;

        public string Id { get { return "car_renderer_lod"; } }
        public string DisplayName { get { return "Car renderer LOD"; } }
        public bool Enabled { get { return Settings.Instance.EnableCarRendererLod; } }

        public void Tick(float deltaTime)
        {
            // Per-car work happens in TryClaim; nothing global to do.
        }

        public bool TryClaim(TrackedCar car)
        {
            float threshold = Settings.Instance.CarShadowDistanceMeters;

            // The slider's own minimum (50) equals the preferred hysteresis margin, which
            // would make ShouldSuppressAtDistance's restore test true at every distance
            // once suppressed — a car could never regain its shadow by moving closer, no
            // matter how close. EffectiveHysteresis caps the margin so it can never reach
            // the threshold itself.
            float margin = Decisions.EffectiveHysteresis(threshold, HysteresisMeters);

            bool want = Decisions.ShouldSuppressAtDistance(
                car.Facts.Distance,
                threshold,
                margin,
                car.ShadowsSuppressed);

            if (!want)
            {
                Release(car);
                return false;
            }

            if (car.ShadowsSuppressed)
            {
                // Already in the desired state; claim without touching anything.
                return true;
            }

            if (!Gather(car))
            {
                return false;
            }

            for (int i = 0; i < car.Renderers.Length; i++)
            {
                Renderer r = car.Renderers[i];
                if (r == null)
                {
                    continue;
                }

                try
                {
                    car.OriginalShadowModes[i] = r.shadowCastingMode;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    _renderersTouched++;
                }
                catch
                {
                    // Destroyed mid-write; the array is re-gathered next transition.
                }
            }

            car.ShadowsSuppressed = true;
            _held.Add(car);
            return true;
        }

        public void Release(TrackedCar car)
        {
            if (!car.ShadowsSuppressed)
            {
                return;
            }

            if (car.Renderers != null && car.OriginalShadowModes != null)
            {
                for (int i = 0; i < car.Renderers.Length; i++)
                {
                    Renderer r = car.Renderers[i];
                    if (r == null)
                    {
                        continue;
                    }

                    try
                    {
                        r.shadowCastingMode = car.OriginalShadowModes[i];
                        _renderersTouched++;
                    }
                    catch
                    {
                    }
                }
            }

            car.ShadowsSuppressed = false;
            _held.Remove(car);
        }

        /// <summary>
        /// Gathers a car's renderers on first need. Re-gathers if any cached entry has
        /// died, since Railroader can add or remove child objects after we cached them.
        /// Also re-gathers a cached zero-length array: without that, a car whose renderers
        /// were not yet spawned on the first attempt would cache an empty array forever,
        /// and a later stale-check that only looks for dead entries inside a non-empty
        /// array would never notice it should try again.
        /// </summary>
        private static bool Gather(TrackedCar car)
        {
            bool stale = car.Renderers == null || car.Renderers.Length == 0;

            if (!stale)
            {
                for (int i = 0; i < car.Renderers.Length; i++)
                {
                    if (car.Renderers[i] == null) { stale = true; break; }
                }
            }

            if (!stale)
            {
                return true;
            }

            GameObject go = car.Car != null ? car.Car.gameObject : null;
            if (go == null)
            {
                return false;
            }

            car.Renderers = go.GetComponentsInChildren<Renderer>(true);
            car.OriginalShadowModes = new ShadowCastingMode[car.Renderers.Length];
            return car.Renderers.Length > 0;
        }

        /// <summary>
        /// Unconditional restore, used on whole-mod disable (Main.OnToggle) and OnUnload,
        /// neither of which goes through Apply/Release(car) or passes us a car list. Walks
        /// our own held list, so a car that was suppressed does not keep its shadows off
        /// forever just because the mod (rather than only this feature) was switched off.
        /// </summary>
        public void ReleaseAll()
        {
            // Each car's Release is isolated in its own try/catch, matching the plan's
            // global fan-out constraint: if one throws, the loop must still reach every
            // other car and still reach _held.Clear() below, rather than aborting partway
            // and leaving the remaining cars' shadows suppressed for the rest of the
            // session.
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                try
                {
                    Release(_held[i]);
                }
                catch (Exception ex)
                {
                    Main.Log("CarRendererFeature: Release() threw from ReleaseAll(): " + ex.Message);
                }
            }

            _held.Clear();
        }

        public string[] TelemetryHeaders
        {
            get { return new[] { "cars_shadows_off", "renderers_touched" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                return new[]
                {
                    _held.Count.ToString(CultureInfo.InvariantCulture),
                    _renderersTouched.ToString(CultureInfo.InvariantCulture)
                };
            }
        }
    }
}
