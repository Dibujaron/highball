using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Alternates every feature between baseline and active windows and records frame
    /// timings for each, so the effect can be measured without the player doing anything.
    ///
    /// Alternating rather than running one long A then one long B matters: it controls for
    /// whatever the player happens to be doing, which drifts over a session.
    ///
    /// This is a stopgap: it flips every feature's Active flag in lockstep and reports a
    /// single aggregate "downgraded" count. A later task rewrites this into a proper
    /// Telemetry component with a per-feature experiment target and per-feature columns.
    /// </summary>
    internal sealed class Experiment
    {
        /// <summary>
        /// Frames discarded after each mode switch. Solver iteration changes settle within
        /// a few physics steps, and including that transient would smear the comparison.
        /// </summary>
        private const float SettleSeconds = 2f;

        private readonly FeatureHost _host;
        private readonly CarRegistry _registry;
        private readonly Evaluator _evaluator;

        private bool _activeWindow;
        private float _windowElapsed;
        private float _settleRemaining;

        private int _frames;
        private float _frameSeconds;

        private StreamWriter _writer;
        private int _rowsWritten;

        internal string CsvPath { get; private set; }
        internal bool ActiveWindow => _activeWindow;
        internal int RowsWritten => _rowsWritten;

        internal Experiment(FeatureHost host, CarRegistry registry, Evaluator evaluator)
        {
            _host = host;
            _registry = registry;
            _evaluator = evaluator;
        }

        internal void Init()
        {
            try
            {
                CsvPath = Path.Combine(Application.persistentDataPath, "Highball.csv");
                bool isNew = !File.Exists(CsvPath);

                _writer = new StreamWriter(CsvPath, append: true) { AutoFlush = true };
                _writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));

                if (isNew)
                {
                    _writer.WriteLine("wall_clock,mode,window_s,frames,avg_frame_ms,fps,tracked,moving,downgraded");
                }

                Main.Log("Experiment log: " + CsvPath);
            }
            catch (Exception ex)
            {
                Main.Log("Could not open experiment log: " + ex.Message);
                _writer = null;
            }
        }

        internal void Shutdown()
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Nothing useful to do.
            }

            _writer = null;
        }

        internal void Tick(float deltaTime)
        {
            // Settling after a switch: drive the clock but do not count these frames.
            if (_settleRemaining > 0f)
            {
                _settleRemaining -= deltaTime;
                return;
            }

            _frames++;
            _frameSeconds += deltaTime;
            _windowElapsed += deltaTime;

            if (_windowElapsed < Settings.Instance.ExperimentWindowSeconds)
            {
                return;
            }

            FlushWindow();
            SwitchMode();
        }

        private void FlushWindow()
        {
            if (_frames > 0 && _windowElapsed > 0f)
            {
                double avgFrameMs = (_frameSeconds * 1000.0) / _frames;
                double fps = _frames / _windowElapsed;

                WriteRow(new[]
                {
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _activeWindow ? "ACTIVE" : "BASELINE",
                    _windowElapsed.ToString("F2", CultureInfo.InvariantCulture),
                    _frames.ToString(CultureInfo.InvariantCulture),
                    avgFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                    fps.ToString("F3", CultureInfo.InvariantCulture),
                    _registry.TrackedCount.ToString(CultureInfo.InvariantCulture),
                    _evaluator.MovingCount.ToString(CultureInfo.InvariantCulture),
                    CountDowngraded().ToString(CultureInfo.InvariantCulture)
                });
            }

            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
        }

        private void SwitchMode()
        {
            _activeWindow = !_activeWindow;
            SetActive(_activeWindow);
            _settleRemaining = SettleSeconds;
        }

        /// <summary>Used when the experiment is switched off: pin every feature on.</summary>
        internal void ForceActive(bool value)
        {
            _activeWindow = value;
            SetActive(value);
            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
            _settleRemaining = SettleSeconds;
        }

        /// <summary>
        /// Sets Active on every feature. A feature that is Enabled but not Active claims
        /// nothing and releases everything, so a BASELINE window is a true control.
        ///
        /// Switching to inactive releases synchronously rather than waiting for the next
        /// _host.Apply pass: a BASELINE window must not start counting frames while cars
        /// are still downgraded from the preceding ACTIVE window.
        /// </summary>
        private void SetActive(bool value)
        {
            IFeature[] features = _host.Features;
            for (int i = 0; i < features.Length; i++)
            {
                // Set the flag before attempting release, so a throwing ReleaseAll can
                // never prevent this or any later feature's Active flag from being set.
                features[i].Active = value;

                if (!value)
                {
                    try
                    {
                        features[i].ReleaseAll();
                    }
                    catch (Exception ex)
                    {
                        Main.Log("Feature '" + features[i].Id + "' threw from ReleaseAll(): " + ex);
                    }
                }
            }
        }

        private int CountDowngraded()
        {
            IList<TrackedCar> cars = _registry.Cars;
            int count = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                if (cars[i] != null && cars[i].IsDowngraded)
                {
                    count++;
                }
            }

            return count;
        }

        private void WriteRow(string[] cells)
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(string.Join(",", cells));
                _rowsWritten++;
            }
            catch (Exception ex)
            {
                Main.Log("Experiment log write failed, disabling: " + ex.Message);
                Shutdown();
            }
        }
    }
}
