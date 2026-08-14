using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Records frame timings every session, regardless of settings, so results can be
    /// read back later. By default every row is stamped LIVE: each feature simply holds
    /// whatever its own toggle says, with nothing alternated.
    ///
    /// When <see cref="Settings.RunExperiment"/> is turned on, it additionally alternates
    /// the single feature named by <see cref="Settings.ExperimentTarget"/> between
    /// baseline and active windows, stamping each ACTIVE/BASELINE, so that one feature's
    /// effect can be measured without the player running a protocol. Alternating rather
    /// than running one long A then one long B matters there: it controls for whatever
    /// the player happens to be doing, which drifts over a session. Every other feature
    /// still holds whatever its own toggle says, so an fps delta can always be attributed
    /// to the one feature under test rather than to all of them at once.
    ///
    /// The CSV columns are composed from whichever features are enabled: base columns
    /// first, then each enabled feature's own TelemetryHeaders/TelemetryValues, walked in
    /// the same FeatureHost.Features order so columns never silently misalign.
    ///
    /// Each session writes to its own uniquely-named file with exactly one banner and
    /// header, readable as plain CSV; a mid-session Enabled drift rolls over to a new file.
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

        // Tracks RunExperiment as of the last time we looked, purely to let
        // SettingsChanged() detect a false-to-true edge (see its comment).
        private bool _lastRunExperiment;

        private int _frames;
        private float _frameSeconds;

        private StreamWriter _writer;
        private int _rowsWritten;

        private string _fileStem;
        private int _rolloverCount;

        /// <summary>
        /// The drift key (joined Ids of the enabled feature set, plus ExperimentTarget) that
        /// the currently-open file's banner and header describe. FlushWindow compares against
        /// this every row so a mid-session Enabled toggle OR a mid-session ExperimentTarget
        /// edit rolls over to a new file instead of silently reinterpreting columns, or rows,
        /// under a stale banner. A target edit alone does not change any column, but it does
        /// change what ACTIVE/BASELINE mean for every row written after it, so it must roll
        /// over just as surely as a column change would.
        /// </summary>
        private string _headerDriftKey;

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
            // Millisecond resolution: two Init() calls in the same wall-clock second would
            // otherwise produce the same stem, and with append: false the second would
            // silently wipe the first session's file instead of harmlessly appending to it.
            _fileStem = "Highball-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            OpenNewFile();

            // Seed the edge-detector with whatever RunExperiment loaded as, so the first
            // settings-panel edit that turns it on from here is correctly seen as a
            // false-to-true edge rather than a false positive from an unset default.
            _lastRunExperiment = Settings.Instance.RunExperiment;

            // ResolveTarget() is not file I/O, but it is structurally throwable (it walks
            // an arbitrary IFeature's Id/DisplayName/Active accessors), and Main.Load has no
            // try/catch of its own around Telemetry.Init() — an uncaught throw here would
            // fail the entire mod load, not just degrade telemetry. Guard it the same way.
            try
            {
                ResolveTarget();
            }
            catch (Exception ex)
            {
                Main.Log("Telemetry: ResolveTarget failed: " + ex.Message);
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
            // been offered to a feature, so this flip has no observable side effect. The
            // restore runs in finally so a setter that throws mid-probe cannot leave Active
            // inverted.
            bool original = feature.Active;
            bool inert;
            try
            {
                feature.Active = !original;
                inert = feature.Active == original;
            }
            finally
            {
                feature.Active = original;
            }

            if (inert)
            {
                Main.Log("Telemetry: ExperimentTarget '" + target + "' (" + feature.DisplayName + ") has an " +
                         "inert Active setter; its value never changes. The A/B harness will alternate " +
                         "nothing; both arms will be identical.");
            }

            WarnIfStarvedByPriority(feature, target);
        }

        /// <summary>
        /// FeatureHost.Apply offers each car to ICarFeatures in _host.Features array order;
        /// the first enabled, active one to claim a car wins it, and every later feature
        /// never sees that car at all. If the experiment target sits behind another
        /// enabled ICarFeature in that order, the earlier feature can claim cars the target
        /// would otherwise have claimed — most obviously when the earlier feature's own
        /// distance threshold is shorter, but claim priority alone is enough regardless of
        /// settings, since which feature gets first refusal never depends on either
        /// feature's configured distance. Left as a warning only: this does not reorder or
        /// disable anything, it only makes a target that reads zero (or two identical A/B
        /// arms) traceable to a cause instead of a mystery.
        /// </summary>
        private void WarnIfStarvedByPriority(IFeature feature, string target)
        {
            if (!(feature is ICarFeature))
            {
                return;
            }

            IFeature[] features = _host.Features;
            int targetIndex = -1;
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i].Id == target)
                {
                    targetIndex = i;
                    break;
                }
            }

            if (targetIndex <= 0)
            {
                return;
            }

            for (int i = 0; i < targetIndex; i++)
            {
                if (features[i] is ICarFeature && features[i].Enabled)
                {
                    Main.Log("Telemetry: ExperimentTarget '" + target + "' (" + feature.DisplayName + ") sits " +
                             "behind enabled feature '" + features[i].Id + "' (" + features[i].DisplayName +
                             ") in claim priority order. '" + features[i].Id + "' may claim cars before the " +
                             "target ever sees them, so the target's telemetry may read zero and both A/B " +
                             "arms may be identical.");
                }
            }
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
                                  + " features=" + enabledJoin
                                  + " target=" + Settings.Instance.ExperimentTarget);
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
        /// present at Init().
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
            // Settling after a switch: drive the clock but do not count these frames. Only
            // meaningful while the A/B experiment is alternating arms — LIVE mode (the
            // default) never switches anything, so there is no transient to settle after
            // and frames are never discarded here regardless of what _settleRemaining was
            // last set to.
            if (Settings.Instance.RunExperiment && _settleRemaining > 0f)
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
        /// The value FlushWindow rolls over on: the enabled feature set (which columns
        /// depend on) plus ExperimentTarget (which what ACTIVE/BASELINE mean depends on),
        /// plus the tuning settings whose values determine what the data columns mean
        /// (most critically MovingSpeedThreshold). Any of these changing mid-session makes
        /// every later row incomparable with the rows already in the open file, so all
        /// belong in the same drift key.
        /// </summary>
        private string DriftKey(string enabledJoin)
        {
            return enabledJoin + "|target=" + Settings.Instance.ExperimentTarget + "|" + SettingsLine();
        }

        private string DriftKey()
        {
            return DriftKey(string.Join("|", EnabledFeatures()));
        }

        /// <summary>
        /// One extra header line recording every tunable that can change what the CSV's
        /// numbers mean — most importantly MovingSpeedThreshold, which determines the
        /// `moving` column and therefore the headroom verdict the whole harness exists to
        /// inform. Two files with different values here are not comparable, and without
        /// this line that difference would be invisible to a reader who flat-reads every
        /// CSV into one dataframe.
        /// </summary>
        private string SettingsLine()
        {
            Settings s = Settings.Instance;
            return string.Format(CultureInfo.InvariantCulture,
                "SETTINGS min_distance_m={0} steady_accel_threshold={1} required_steady_s={2} " +
                "moving_speed_threshold={3} low_solver_iterations={4} refresh_interval_s={5} " +
                "evaluate_interval_s={6} experiment_window_s={7}",
                s.MinDistanceMeters, s.SteadyAccelThreshold, s.RequiredSteadySeconds,
                s.MovingSpeedThreshold, s.LowSolverIterations, s.RefreshIntervalSeconds,
                s.EvaluateIntervalSeconds, s.ExperimentWindowSeconds);
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
        /// The `mode` column's value for the row about to be written: LIVE whenever
        /// RunExperiment is off — normal operation, where every feature simply follows
        /// its own Enabled toggle and there are no arms to distinguish. ACTIVE/BASELINE
        /// only mean something while the A/B harness is alternating the experiment
        /// target between windows. Internal rather than private so Main's GUI readout can
        /// show the same value FlushWindow is about to stamp, without duplicating the
        /// LIVE-vs-ACTIVE/BASELINE decision anywhere else.
        /// </summary>
        internal string ModeLabel()
        {
            if (!Settings.Instance.RunExperiment)
            {
                return "LIVE";
            }

            return _activeWindow ? "ACTIVE" : "BASELINE";
        }

        private void FlushWindow()
        {
            if (_frames > 0 && _windowElapsed > 0f)
            {
                // A mid-session Enabled toggle changes what FullHeader() and the cells below
                // would produce; a mid-session ExperimentTarget edit changes what ACTIVE and
                // BASELINE mean without changing a single column. Either drifts the open
                // file's banner out from under its own rows, so both roll over to a new file
                // rather than re-emit a second header into this one, or silently reinterpret
                // rows under the old one. Skipped once _writer is null: telemetry already
                // failed and stays permanently degraded.
                if (_writer != null && DriftKey() != _headerDriftKey)
                {
                    Main.Log("Telemetry: enabled feature set or experiment target changed; rolling over to a new file.");
                    OpenNewFile();
                }

                double avgFrameMs = (_frameSeconds * 1000.0) / _frames;
                double fps = _frames / _windowElapsed;

                var cells = new List<string>
                {
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    ModeLabel(),
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
            if (!Settings.Instance.RunExperiment)
            {
                // LIVE mode: nothing alternates, so there is no arm to switch to and no
                // settle delay to serve. ApplyMode already pins every feature to its own
                // Enabled toggle regardless of _activeWindow whenever RunExperiment is
                // off (see its `alternating` flag) — leaving _activeWindow untouched here
                // does not change what any feature does.
                return;
            }

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
        /// Called from Settings.OnChange, i.e. on every settings-panel edit, since UMM does
        /// not say which field changed. Any edit can invalidate the in-flight window — it may
        /// now span frames measured under two different configurations — so this discards
        /// the window's accumulated frames without flushing them, re-arms the settle timer as
        /// if a mode switch had just happened, and re-runs ApplyMode so the new settings
        /// (including a possible RunExperiment or ExperimentTarget change) take effect
        /// immediately rather than waiting for a natural window boundary that, if
        /// RunExperiment was just turned off, will never come.
        ///
        /// With LIVE as the default, turning RunExperiment on mid-session is the normal way
        /// a player starts an A/B run — the equivalent of Main.OnToggle's ForceActive(false)
        /// at mod-enable time, which starts every run on baseline so the first recorded arm
        /// is a control. This detects that same false-to-true edge (via _lastRunExperiment,
        /// since UMM does not tell us which field changed) and forces _activeWindow to false
        /// before ApplyMode runs, so a mid-session A/B start gets the same guarantee: its
        /// first recorded window is always BASELINE, never the treatment arm.
        /// </summary>
        internal void SettingsChanged()
        {
            bool runExperimentNow = Settings.Instance.RunExperiment;
            bool justTurnedOn = runExperimentNow && !_lastRunExperiment;

            _frames = 0;
            _frameSeconds = 0f;
            _windowElapsed = 0f;
            _settleRemaining = SettleSeconds;

            if (justTurnedOn)
            {
                _activeWindow = false;
            }

            ApplyMode();
            _lastRunExperiment = runExperimentNow;
        }

        /// <summary>
        /// Only the feature under test (Settings.ExperimentTarget) alternates with
        /// _activeWindow, and only while RunExperiment is on. Every other feature is pinned
        /// Active so its own Enabled toggle is the sole thing controlling it. Flipping all of
        /// them at once would confound the comparison, since an fps delta could not be
        /// attributed to any one of them.
        ///
        /// Checking RunExperiment here (rather than trusting _activeWindow alone) matters
        /// because _activeWindow goes stale the moment RunExperiment turns false: SwitchMode
        /// stops advancing it once the experiment is off (see SwitchMode's own guard), so it
        /// keeps whatever value A/B alternation last left it at. Without this check, turning
        /// the experiment off while the window happens to be BASELINE would leave the target
        /// pinned Active=false forever. With it, every feature takes the "pin active" branch
        /// once RunExperiment is off, regardless of whatever _activeWindow was last left at.
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
            bool alternating = Settings.Instance.RunExperiment;

            for (int i = 0; i < features.Length; i++)
            {
                bool value = (alternating && features[i].Id == target) ? _activeWindow : true;

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
