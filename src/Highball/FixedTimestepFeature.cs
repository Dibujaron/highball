using System;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Runs the physics loop less often.
    ///
    /// This is the one lever the measurements actually predict. The FixedUpdate phase is 46%
    /// of the frame on the reference save, and the single biggest item inside it —
    /// `TrainController.FixedUpdate` at 2.46 ms per call, 12% of the whole frame — runs once
    /// per fixed STEP, not once per frame. At the game's own 50 Hz that is 1.21 steps per
    /// frame, so the entire bucket scales directly with the tick rate: 40 Hz predicts ~44 fps
    /// against 41 today, 33 Hz predicts ~48.
    ///
    /// Predicted, not measured — every confident prediction this project has made so far was
    /// wrong, so it ships off and gets the same A/B treatment as everything else.
    ///
    /// It is also a genuine fidelity trade rather than a free win, which the others were not.
    /// Coupler slack, braking and the train sim all live in this loop; a coarser step means
    /// coarser handling, and derailment behaviour is downstream of it. That is the reason for
    /// the conservative slider floor and for keeping it off by default.
    /// </summary>
    internal sealed class FixedTimestepFeature : IFeature
    {
        /// <summary>
        /// Below this the write is treated as "someone else changed it", so the original is
        /// re-captured rather than clobbered. Timesteps are small numbers and exact float
        /// equality on a value we computed by division is not reliable.
        /// </summary>
        private const float Epsilon = 0.0000005f;

        private bool _applied;
        private float _original;
        private float _wrote;
        private bool _reported;

        public string Id { get { return "fixed_timestep"; } }
        public string DisplayName { get { return "Physics tick rate"; } }
        public bool Enabled { get { return Settings.Instance.EnableFixedTimestep; } }

        public void Tick(float deltaTime)
        {
            float current;
            try
            {
                current = Time.fixedDeltaTime;
            }
            catch (Exception ex)
            {
                Main.Log("FixedTimestep: could not read Time.fixedDeltaTime: " + ex.Message);
                return;
            }

            if (!_applied)
            {
                _original = current;
            }
            else if (Mathf.Abs(current - _wrote) > Epsilon)
            {
                // Something outside changed it — the game re-applies its own value on load
                // and on some settings changes. Yield to it and re-capture rather than
                // fight, exactly as TerrainLodFeature does for terrain LOD.
                _original = current;
                _applied = false;
            }

            float requestedHz = Settings.Instance.PhysicsTickRateHz;
            if (requestedHz < 1f)
            {
                // A degenerate rate would divide into an absurd timestep and effectively
                // stop the simulation. Refuse rather than write it.
                return;
            }

            // ClampRelaxation, not ClampReduction: a LONGER step is less work, so the
            // configured value only wins when it is longer than the game's own. Setting a
            // rate above the game's does nothing rather than making physics run more often.
            float desired = Decisions.ClampRelaxation(1f / requestedHz, _original);

            try
            {
                // Set the flag before the write, matching TerrainLodFeature: if the write
                // throws partway the value may already have changed, and ReleaseAll must
                // still restore it. Restoring something we never changed is harmless;
                // failing to restore something we did is not.
                _applied = true;
                _wrote = desired;
                Time.fixedDeltaTime = desired;
            }
            catch (Exception ex)
            {
                Main.Log("FixedTimestep: write failed: " + ex.Message);
                return;
            }

            if (!_reported)
            {
                _reported = true;
                Main.Log(string.Format(CultureInfo.InvariantCulture,
                    "FixedTimestep: game default {0:F1} Hz ({1:F2} ms); now running {2:F1} Hz ({3:F2} ms).",
                    1f / _original, _original * 1000f, 1f / _wrote, _wrote * 1000f));
            }
        }

        /// <summary>
        /// Hands the timestep back. Runtime-only state that never persists to the save, but
        /// leaving the simulation rate altered after the feature is switched off would be a
        /// silent, session-long change to how the player's trains behave.
        /// </summary>
        public void ReleaseAll()
        {
            if (!_applied)
            {
                return;
            }

            try
            {
                Time.fixedDeltaTime = _original;
            }
            catch (Exception ex)
            {
                Main.Log("FixedTimestep: restore failed, the timestep may still be modified: " + ex.Message);
            }

            _applied = false;

            // Deliberately not resetting _reported: the "game default X, now running Y" line
            // is a once-per-session orientation message, not an event log, and re-firing it
            // on every enable/disable cycle would be noise.
        }

        public string[] TelemetryHeaders { get { return new[] { "fixed_dt_ms" }; } }

        public string[] TelemetryValues
        {
            get
            {
                float dt = _applied ? _wrote : 0f;
                return new[] { (dt * 1000f).ToString("F2", CultureInfo.InvariantCulture) };
            }
        }

        internal string StatusLine()
        {
            if (!_applied)
            {
                return "off";
            }

            return string.Format(CultureInfo.InvariantCulture,
                "{0:F0} Hz (was {1:F0})", 1f / _wrote, 1f / _original);
        }
    }
}
