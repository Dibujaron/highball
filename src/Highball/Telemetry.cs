using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Alternates a single targeted feature between baseline and active windows and
    /// records frame timings for each, so the effect can be measured without the player
    /// doing anything.
    ///
    /// Alternating rather than running one long A then one long B matters: it controls for
    /// whatever the player happens to be doing, which drifts over a session.
    ///
    /// Only <see cref="Settings.ExperimentTarget"/> flips between windows. Every other
    /// feature holds whatever its own toggle says, so an fps delta can always be
    /// attributed to the one feature under test rather than to all of them at once.
    /// The CSV columns are composed from whichever features are enabled: base columns
    /// first, then each enabled feature's own TelemetryHeaders/TelemetryValues, walked in
    /// the same FeatureHost.Features order so columns never silently misalign.
    /// </summary>
    internal sealed class Telemetry
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

        internal Telemetry(FeatureHost host, CarRegistry registry, Evaluator evaluator)
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

                _writer = new StreamWriter(CsvPath, append: true) { AutoFlush = true };
                _writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
                                  + " features=" + string.Join("|", EnabledFeatures()));
                _writer.WriteLine(string.Join(",", FullHeader()));

                Main.Log("Telemetry log: " + CsvPath);
            }
            catch (Exception ex)
            {
                Main.Log("Could not open telemetry log: " + ex.Message);
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

        private string[] BaseHeaders()
        {
            return new[] { "wall_clock", "mode", "window_s", "frames", "avg_frame_ms", "fps", "tracked", "moving" };
        }

        private string[] EnabledFeatures()
        {
            var ids = new List<string>();
            IFeature[] features = _host.Features;
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i].Enabled) ids.Add(features[i].Id);
            }

            return ids.ToArray();
        }

        /// <summary>
        /// Walks _host.Features in order, appending each enabled feature's headers. The row
        /// builder in FlushWindow must walk the same array with the same Enabled filter, or
        /// columns silently misalign with values.
        /// </summary>
        private string[] FullHeader()
        {
            var cells = new List<string>(BaseHeaders());
            IFeature[] features = _host.Features;
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i].Enabled) cells.AddRange(features[i].TelemetryHeaders);
            }

            return cells.ToArray();
        }

        private void FlushWindow()
        {
            if (_frames > 0 && _windowElapsed > 0f)
            {
                double avgFrameMs = (_frameSeconds * 1000.0) / _frames;
                double fps = _frames / _windowElapsed;

                var cells = new List<string>
                {
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    _activeWindow ? "ACTIVE" : "BASELINE",
                    _windowElapsed.ToString("F2", CultureInfo.InvariantCulture),
                    _frames.ToString(CultureInfo.InvariantCulture),
                    avgFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                    fps.ToString("F3", CultureInfo.InvariantCulture),
                    _registry.TrackedCount.ToString(CultureInfo.InvariantCulture),
                    _evaluator.MovingCount.ToString(CultureInfo.InvariantCulture)
                };

                // Same array, same order, same filter as FullHeader() above.
                IFeature[] features = _host.Features;
                for (int i = 0; i < features.Length; i++)
                {
                    if (features[i].Enabled) cells.AddRange(features[i].TelemetryValues);
                }

                WriteRow(cells.ToArray());
            }

            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
        }

        private void SwitchMode()
        {
            _activeWindow = !_activeWindow;
            ApplyMode();
            _settleRemaining = SettleSeconds;
        }

        /// <summary>Used when the experiment is switched off: pin every feature on.</summary>
        internal void ForceActive(bool value)
        {
            _activeWindow = value;
            ApplyMode();
            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
            _settleRemaining = SettleSeconds;
        }

        /// <summary>
        /// Only the feature under test (Settings.ExperimentTarget) alternates with
        /// _activeWindow. Every other feature is pinned Active so its own Enabled toggle is
        /// the sole thing controlling it. Flipping all of them at once would confound the
        /// comparison, since an fps delta could not be attributed to any one of them.
        ///
        /// A feature that is Enabled but not Active must claim nothing and release
        /// everything, so a BASELINE window is a true control. Switching a feature to
        /// inactive releases synchronously here rather than waiting for the next
        /// _host.Apply pass: a BASELINE window must not start counting frames while cars
        /// are still downgraded from the preceding ACTIVE window.
        /// </summary>
        private void ApplyMode()
        {
            IFeature[] features = _host.Features;
            string target = Settings.Instance.ExperimentTarget;

            for (int i = 0; i < features.Length; i++)
            {
                bool value = features[i].Id == target ? _activeWindow : true;

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
                Main.Log("Telemetry log write failed, disabling: " + ex.Message);
                Shutdown();
            }
        }
    }
}
