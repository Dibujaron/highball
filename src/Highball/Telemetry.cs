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

        /// <summary>
        /// The joined Ids of the feature set the most recently written header describes.
        /// FlushWindow compares against this every row so a mid-session Enabled toggle gets
        /// a fresh banner+header pair instead of silently shifting columns under a stale one.
        /// </summary>
        private string _headerFeatureIds;

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
                WriteSessionHeader();
                ResolveTarget();

                Main.Log("Telemetry log: " + CsvPath);
            }
            catch (Exception ex)
            {
                Main.Log("Could not open telemetry log: " + ex.Message);
                _writer = null;
            }
        }

        /// <summary>
        /// Validates Settings.ExperimentTarget once at startup and logs loudly if it cannot
        /// possibly produce two distinguishable arms. This does not correct anything or
        /// change ApplyMode's behaviour — ApplyMode still resolves the target by Id on every
        /// call, since a feature's Enabled state can change at runtime. The point is to make
        /// a silently-null experiment visible in the log instead of invisible in the CSV.
        /// </summary>
        private void ResolveTarget()
        {
            string target = Settings.Instance.ExperimentTarget;
            IFeature feature = _host.Find(target);

            if (feature == null)
            {
                Main.Log("Telemetry: ExperimentTarget '" + target + "' matches no feature Id. " +
                         "The A/B harness will alternate nothing; both arms will be identical.");
                return;
            }

            if (!feature.Enabled)
            {
                Main.Log("Telemetry: ExperimentTarget '" + target + "' (" + feature.DisplayName + ") is not " +
                         "Enabled. FeatureHost.Apply never claims for a disabled feature, so alternating its " +
                         "Active flag changes nothing; both arms will be identical.");
            }

            // Probe for an inert Active setter (e.g. a read-only probe) without disturbing
            // the feature's actual state. Safe to do here: Init() runs before any car has
            // been offered to a feature, so this flip has no observable side effect.
            bool original = feature.Active;
            feature.Active = !original;
            bool inert = feature.Active == original;
            feature.Active = original;

            if (inert)
            {
                Main.Log("Telemetry: ExperimentTarget '" + target + "' (" + feature.DisplayName + ") has an " +
                         "inert Active setter; its value never changes. The A/B harness will alternate " +
                         "nothing; both arms will be identical.");
            }
        }

        /// <summary>
        /// Writes a fresh "# SESSION" banner (naming the enabled feature set and the
        /// experiment target) followed by the matching header row, and records which
        /// feature set it describes. Called once from Init() and again from FlushWindow
        /// whenever the enabled set has drifted since the last header was written.
        ///
        /// Guards its own writes the same way WriteRow does: FlushWindow calls this outside
        /// of any try/catch of its own, so an unguarded write failure here (disk full,
        /// permission revoked mid-session, file locked) would propagate through Tick to
        /// Main.OnUpdate's catch-all and disable the entire mod over a telemetry I/O error.
        /// Catching here keeps a CSV failure confined to telemetry, same as WriteRow.
        /// </summary>
        private void WriteSessionHeader()
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                string enabledJoin = string.Join("|", EnabledFeatures());

                _writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
                                  + " features=" + enabledJoin
                                  + " target=" + Settings.Instance.ExperimentTarget);
                _writer.WriteLine(string.Join(",", FullHeader()));

                ValidateFeatureTelemetryLengths();

                _headerFeatureIds = enabledJoin;
            }
            catch (Exception ex)
            {
                Main.Log("Telemetry log write failed, disabling: " + ex.Message);
                Shutdown();
            }
        }

        /// <summary>
        /// A feature whose TelemetryHeaders and TelemetryValues lengths disagree shifts
        /// every column to its right with no diagnostic. Checked whenever the header is
        /// (re)written so a newly-enabled feature is validated too, not just the set present
        /// at Init().
        /// </summary>
        private void ValidateFeatureTelemetryLengths()
        {
            IFeature[] features = _host.Features;
            for (int i = 0; i < features.Length; i++)
            {
                if (!features[i].Enabled) continue;

                int headerCount = features[i].TelemetryHeaders.Length;
                int valueCount = features[i].TelemetryValues.Length;

                if (headerCount != valueCount)
                {
                    Main.Log("Telemetry: feature '" + features[i].Id + "' returned " + headerCount +
                             " TelemetryHeaders but " + valueCount + " TelemetryValues; every later column " +
                             "will be shifted. Fix the feature so the two arrays have equal length.");
                }
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
                // A mid-session Enabled toggle (Task 7 adds a live per-feature toggle)
                // changes what FullHeader() and the cells below would produce. The header
                // is only written once per WriteSessionHeader call, so if the enabled set
                // has drifted since then, re-emit a fresh banner+header pair now rather than
                // let this row's columns silently shift under the stale header.
                string enabledJoin = string.Join("|", EnabledFeatures());
                if (enabledJoin != _headerFeatureIds)
                {
                    WriteSessionHeader();
                }

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

        /// <summary>
        /// Sets the window state directly and applies it immediately, bypassing
        /// SwitchMode's alternation. Used both when the experiment is off
        /// (ForceActive(true) pins every feature on) and when it is on
        /// (ForceActive(false) starts the session on baseline, pinning every feature on
        /// except the experiment target).
        /// </summary>
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
