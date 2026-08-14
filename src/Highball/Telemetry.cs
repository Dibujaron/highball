using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Optionally records frame timings to a CSV, so a feature's effect can be read back
    /// later instead of guessed at from the fps counter. Off by default
    /// (<see cref="Settings.EnableTelemetry"/>): recording is a measurement activity, and
    /// normal play should write nothing.
    ///
    /// The CSV columns are composed from whichever features are enabled: base columns
    /// first, then each enabled feature's own TelemetryHeaders/TelemetryValues, walked in
    /// the same FeatureHost.Features order so columns never silently misalign.
    ///
    /// Each recording session writes to its own uniquely-named file with exactly one banner
    /// and header, readable as plain CSV; a mid-session settings drift rolls over to a new
    /// file rather than reinterpreting later rows under a stale banner.
    /// </summary>
    internal sealed class Telemetry
    {
        private readonly FeatureHost _host;
        private readonly CarRegistry _registry;
        private readonly Evaluator _evaluator;

        private float _windowElapsed;
        private int _frames;
        private float _frameSeconds;

        private StreamWriter _writer;
        private int _rowsWritten;

        private string _fileStem;
        private int _rolloverCount;

        /// <summary>
        /// Tracks EnableTelemetry as of the last time we looked, so SettingsChanged() can
        /// tell an off->on edge (start a new recording) from an on->off edge (close the
        /// current one) from an ordinary edit to some unrelated slider. UMM does not tell
        /// us which field changed, so the edge has to be detected rather than observed.
        /// </summary>
        private bool _lastEnabled;

        /// <summary>
        /// The drift key (joined Ids of the enabled feature set, plus every tunable on the
        /// SETTINGS line) that the currently-open file's banner and header describe.
        /// FlushWindow compares against this every row so a mid-session change rolls over to
        /// a new file instead of silently reinterpreting rows under a stale banner.
        /// </summary>
        private string _headerDriftKey;

        internal string CsvPath { get; private set; }
        internal int RowsWritten => _rowsWritten;

        internal Telemetry(FeatureHost host, CarRegistry registry, Evaluator evaluator)
        {
            _host = host;
            _registry = registry;
            _evaluator = evaluator;
        }

        internal void Init()
        {
            _lastEnabled = Settings.Instance.EnableTelemetry;

            if (_lastEnabled)
            {
                StartRecording();
            }
        }

        /// <summary>
        /// Opens a fresh file and starts a fresh window. Called at Init when telemetry is
        /// already on, and from SettingsChanged on an off->on edge — toggling recording off
        /// and on again is a new measurement, not a continuation, so it gets its own file
        /// rather than appending rows under the old banner.
        /// </summary>
        private void StartRecording()
        {
            // Millisecond resolution: two StartRecording() calls in the same wall-clock
            // second would otherwise produce the same stem, and with append: false the
            // second would silently wipe the first file instead of harmlessly appending.
            _fileStem = "Highball-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            _rolloverCount = 0;
            OpenNewFile();
            ResetWindow();
        }

        /// <summary>
        /// Closes whatever file is currently open, if any, then opens the next one — the
        /// first file on the initial call, a uniquely-suffixed roll-over file on every call
        /// after — and writes its single banner + header pair. Guarded on its own: a
        /// failure here (disk full, permission revoked mid-session, file locked) must
        /// degrade telemetry alone rather than propagate through Tick to Main.OnUpdate's
        /// catch-all, which would disable the entire mod and release every car.
        /// </summary>
        private void OpenNewFile()
        {
            Shutdown();

            // Only the local `attempt` (and, on success below, the fields it seeds) records
            // this attempt — CsvPath and _rolloverCount are left untouched on failure, so a
            // failed open never claims a roll-over index it didn't use and never reports a
            // path that was never actually opened.
            int attempt = _rolloverCount + 1;

            try
            {
                string fileName = attempt == 1
                    ? _fileStem + ".csv"
                    : _fileStem + "-" + attempt.ToString(CultureInfo.InvariantCulture) + ".csv";
                string path = Path.Combine(Application.persistentDataPath, fileName);

                _writer = new StreamWriter(path, append: false) { AutoFlush = true };

                string enabledJoin = string.Join("|", EnabledFeatures());
                _writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
                                  + " features=" + enabledJoin);
                _writer.WriteLine("# " + SettingsLine());
                _writer.WriteLine(string.Join(",", FullHeader()));

                ValidateFeatureTelemetryLengths();

                _headerDriftKey = DriftKey(enabledJoin);
                CsvPath = path;
                _rolloverCount = attempt;

                Main.Log("Telemetry log: " + CsvPath);
            }
            catch (Exception ex)
            {
                Main.Log("Could not open telemetry log: " + ex.Message);
                // Shutdown(), not a bare null-assign: the StreamWriter may already be open
                // (construction succeeded but a later WriteLine/validate threw), and an
                // undisposed handle here is a leaked, permanently-locked file for the rest
                // of the process. Shutdown() disposes under its own catch and nulls _writer
                // unconditionally, so this is safe even if construction itself is what threw.
                Shutdown();
            }
        }

        /// <summary>
        /// A feature whose TelemetryHeaders and TelemetryValues lengths disagree shifts
        /// every column to its right with no diagnostic. Checked whenever a file's header
        /// is written so a newly-enabled feature is validated too, not just the set
        /// present when recording started.
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
            // Checked on every tick rather than trusted from the last edge: OnToggle and
            // OnUnload can close the writer without any settings change at all.
            if (!Settings.Instance.EnableTelemetry || _writer == null)
            {
                return;
            }

            _frames++;
            _frameSeconds += deltaTime;
            _windowElapsed += deltaTime;

            if (_windowElapsed < Settings.Instance.TelemetryIntervalSeconds)
            {
                return;
            }

            FlushWindow();
        }

        /// <summary>
        /// Called from Settings.OnChange, i.e. on every settings-panel edit, since UMM does
        /// not say which field changed. Starts recording on an off->on edge and stops on an
        /// on->off edge. Any other edit discards the in-flight window without flushing it:
        /// those frames may now span two different configurations, so averaging them into
        /// one row would report a number produced by neither.
        /// </summary>
        internal void SettingsChanged()
        {
            bool enabledNow = Settings.Instance.EnableTelemetry;

            if (enabledNow && !_lastEnabled)
            {
                StartRecording();
            }
            else if (!enabledNow && _lastEnabled)
            {
                Shutdown();
                CsvPath = null;
                ResetWindow();
            }
            else
            {
                ResetWindow();
            }

            _lastEnabled = enabledNow;
        }

        private void ResetWindow()
        {
            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
        }

        private string[] BaseHeaders()
        {
            return new[] { "wall_clock", "window_s", "frames", "avg_frame_ms", "fps", "tracked", "moving" };
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
        /// The value FlushWindow rolls over on: the enabled feature set (which columns
        /// depend on) plus the tuning settings whose values determine what the data columns
        /// mean. Any of these changing mid-session makes every later row incomparable with
        /// the rows already in the open file, so all belong in the same drift key.
        /// </summary>
        private string DriftKey(string enabledJoin)
        {
            return enabledJoin + "|" + SettingsLine();
        }

        private string DriftKey()
        {
            return DriftKey(string.Join("|", EnabledFeatures()));
        }

        /// <summary>
        /// One extra header line recording every tunable that can change what the CSV's
        /// numbers mean. Two files with different values here are not comparable, and
        /// without this line that difference would be invisible to a reader who flat-reads
        /// every CSV into one dataframe.
        /// </summary>
        private string SettingsLine()
        {
            Settings s = Settings.Instance;
            return string.Format(CultureInfo.InvariantCulture,
                "SETTINGS refresh_interval_s={0} evaluate_interval_s={1} telemetry_interval_s={2} " +
                "car_shadow_distance_m={3} tree_billboard_distance_m={4} tree_max_full_lod_count={5} " +
                "tree_crossfade_length_m={6} detail_object_distance_m={7}",
                s.RefreshIntervalSeconds, s.EvaluateIntervalSeconds, s.TelemetryIntervalSeconds,
                s.CarShadowDistanceMeters, s.TreeBillboardDistanceMeters, s.TreeMaxFullLodCount,
                s.TreeCrossFadeLengthMeters, s.DetailObjectDistanceMeters);
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

        /// <summary>
        /// Short status for the settings panels, so both the UMM panel and the in-game tab
        /// can show whether anything is being written without either one duplicating the
        /// decision.
        /// </summary>
        internal string StatusLabel()
        {
            if (!Settings.Instance.EnableTelemetry)
            {
                return "off";
            }

            return _writer != null ? "recording" : "failed";
        }

        private void FlushWindow()
        {
            if (_frames > 0 && _windowElapsed > 0f)
            {
                // A mid-session Enabled toggle changes what FullHeader() and the cells below
                // would produce, and a tuning edit changes what the numbers mean. Either
                // drifts the open file's banner out from under its own rows, so both roll
                // over to a new file rather than re-emit a second header into this one.
                // Skipped once _writer is null: telemetry already failed and stays degraded.
                if (_writer != null && DriftKey() != _headerDriftKey)
                {
                    Main.Log("Telemetry: enabled feature set or settings changed; rolling over to a new file.");
                    OpenNewFile();
                }

                double avgFrameMs = (_frameSeconds * 1000.0) / _frames;
                double fps = _frames / _windowElapsed;

                var cells = new List<string>
                {
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
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

            ResetWindow();
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
