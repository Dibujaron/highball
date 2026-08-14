using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Profiling;
using UnityEngine.LowLevel;

namespace Highball
{
    /// <summary>
    /// Read-only. Answers the question four bespoke probes each answered a sliver of: where
    /// does the frame actually go? Rendering, physics, or scripts?
    ///
    /// Unity's main-thread frame is a tree of player-loop subsystems, and
    /// <see cref="PlayerLoop"/> exposes it. A native subsystem has no managed delegate to
    /// wrap — it is a raw function pointer — so this does not replace anything. It inserts
    /// its own marker systems immediately before and after each subsystem of interest and
    /// times the gap. Two <see cref="Stopwatch"/> timestamps per measured subsystem per
    /// frame, roughly half a microsecond against a 20 ms frame.
    ///
    /// Columns are cumulative, like `renderers_touched`: differencing two telemetry rows
    /// gives the ms-per-frame spent in each subsystem over that window
    /// (`delta_ms / delta_budget_frames`). Cumulative rather than per-window averages
    /// because Telemetry reads TelemetryValues more than once per row on a file roll-over,
    /// so a getter that reset its own accumulators would silently report zeros for exactly
    /// the row that rolled over.
    ///
    /// This is the most invasive thing in the mod: it rewrites the game's update loop. It
    /// therefore only ever INSERTS markers, never removes or reorders an existing entry,
    /// never mutates an existing array in place (every modified level is copied), and
    /// restores by stripping its own markers out of whatever the loop looks like at the
    /// time — so another mod that changed the loop after us does not get clobbered.
    /// </summary>
    internal sealed class FrameBudgetProbe : IFeature
    {
        /// <summary>
        /// The <c>type</c> stamped on every marker system, and the only thing Restore keys
        /// off. Unity uses this field for naming only and tolerates duplicates — its own
        /// tree has several (`ClearLines` appears under both FixedUpdate and EarlyUpdate) —
        /// so one shared type for all markers is both legal and what makes removal
        /// exhaustive regardless of how many were inserted.
        /// </summary>
        private struct HighballFrameMarker
        {
        }

        private sealed class Bucket
        {
            internal readonly string Column;
            internal readonly string Parent;
            internal readonly string Child;

            /// <summary>Timestamp taken by the "before" marker, read by the "after" marker.</summary>
            internal long Start;

            /// <summary>Cumulative ticks spent in this subsystem since install.</summary>
            internal long Accum;

            internal bool Installed;

            internal Bucket(string column, string parent, string child)
            {
                Column = column;
                Parent = parent;
                Child = child;
            }
        }

        /// <summary>
        /// The subsystems worth timing, grouped so the three-way question can be answered by
        /// summing. Everything here runs on the main thread; gfx jobs are enabled on this
        /// install (`gfx-enable-native-gfx-jobs=1` in boot.config), so worker-thread render
        /// work is NOT captured — if the main thread is not the bottleneck, that shows up
        /// here as time unaccounted for rather than as a bucket.
        /// </summary>
        private static Bucket[] MakeBuckets()
        {
            return new[]
            {
                new Bucket("phys_fixed_ms", "FixedUpdate", "PhysicsFixedUpdate"),
                new Bucket("phys_update_ms", "PreUpdate", "PhysicsUpdate"),
                new Bucket("phys_late_ms", "PreLateUpdate", "PhysicsLateUpdate"),

                new Bucket("script_update_ms", "Update", "ScriptRunBehaviourUpdate"),
                new Bucket("script_fixed_ms", "FixedUpdate", "ScriptRunBehaviourFixedUpdate"),
                new Bucket("script_late_ms", "PreLateUpdate", "ScriptRunBehaviourLateUpdate"),

                new Bucket("rend_renderers_ms", "PostLateUpdate", "UpdateAllRenderers"),
                new Bucket("rend_skinned_ms", "PostLateUpdate", "UpdateAllSkinnedMeshes"),
                new Bucket("rend_finish_ms", "PostLateUpdate", "FinishFrameRendering"),
                new Bucket("rend_particles_ms", "PostLateUpdate", "ParticleSystemEndUpdateAll"),
                new Bucket("rend_canvas_ms", "PostLateUpdate", "PlayerUpdateCanvases"),
                new Bucket("cull_notify_ms", "EarlyUpdate", "RendererNotifyInvisible"),

                // Where an uncapped-but-GPU-bound frame parks itself. Separating it matters:
                // without it, waiting on the GPU would be invisible and the CPU would look
                // slower than it is.
                new Bucket("present_ms", "PostLateUpdate", "PresentAfterDraw"),
            };
        }

        private Bucket[] _buckets;
        private bool _installed;
        private long _frames;

        /// <summary>
        /// Set once Install has failed, so Tick stops retrying. Without it a failure would
        /// re-attempt (and re-log) at the evaluate cadence for the rest of the session —
        /// four log lines a second, in the one channel that has to stay readable. Cleared
        /// only by a toggle-off/on cycle, which is the natural way to ask for a retry.
        /// </summary>
        private bool _installFailed;

        /// <summary>
        /// The loop as it was before Install, kept only as a fallback for the case where
        /// stripping markers throws. Restore normally strips instead, so a loop another mod
        /// modified after us is not reverted along with our own changes. Safe as a shallow
        /// copy because Install never writes into an existing subSystemList array.
        /// </summary>
        private PlayerLoopSystem _backup;

        private ProfilerRecorder _drawCalls;
        private ProfilerRecorder _setPass;
        private ProfilerRecorder _batches;
        private ProfilerRecorder _triangles;
        private bool _recordersStarted;
        private bool _recorderVerdictLogged;

        public string Id { get { return "frame_budget"; } }
        public string DisplayName { get { return "Frame budget probe (read-only)"; } }
        public bool Enabled { get { return Settings.Instance.EnableFrameBudgetProbe; } }

        public void Tick(float deltaTime)
        {
            if (!_installed && !_installFailed)
            {
                Install();
            }
        }

        // --- install / restore ---

        private void Install()
        {
            try
            {
                _buckets = MakeBuckets();

                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                _backup = root;

                PlayerLoopSystem[] rootList = root.subSystemList;
                if (rootList == null || rootList.Length == 0)
                {
                    Main.Log("FrameBudget: player loop has no subsystems; not installing.");
                    _installFailed = true;
                    return;
                }

                var byParent = new Dictionary<string, List<Bucket>>();
                for (int i = 0; i < _buckets.Length; i++)
                {
                    Bucket b = _buckets[i];
                    List<Bucket> list;
                    if (!byParent.TryGetValue(b.Parent, out list))
                    {
                        list = new List<Bucket>();
                        byParent[b.Parent] = list;
                    }

                    list.Add(b);
                }

                // One extra slot for the frame counter, appended at root level so it runs
                // exactly once per frame after everything else.
                var newRoot = new PlayerLoopSystem[rootList.Length + 1];

                for (int i = 0; i < rootList.Length; i++)
                {
                    // A struct copy: assigning to sys.subSystemList below rewrites this
                    // local, never the element inside the original array.
                    PlayerLoopSystem sys = rootList[i];

                    List<Bucket> wanted;
                    if (sys.type != null
                        && sys.subSystemList != null
                        && byParent.TryGetValue(sys.type.Name, out wanted))
                    {
                        sys.subSystemList = InsertMarkers(sys.subSystemList, wanted);
                    }

                    newRoot[i] = sys;
                }

                newRoot[rootList.Length] = new PlayerLoopSystem
                {
                    type = typeof(HighballFrameMarker),
                    updateDelegate = CountFrame
                };

                PlayerLoopSystem modified = root;
                modified.subSystemList = newRoot;
                PlayerLoop.SetPlayerLoop(modified);

                _installed = true;
                StartRecorders();
                ReportInstall();
            }
            catch (Exception ex)
            {
                Main.Log("FrameBudget: install failed, restoring the original loop: " + ex);
                try
                {
                    PlayerLoop.SetPlayerLoop(_backup);
                }
                catch (Exception restoreEx)
                {
                    Main.Log("FrameBudget: restore after failed install ALSO failed: " + restoreEx.Message);
                }

                _installed = false;
                _installFailed = true;
            }
        }

        /// <summary>
        /// Rebuilds one parent's subsystem list with a start marker before, and a stop
        /// marker after, each subsystem we want to time. Returns a new array; the input is
        /// never written to.
        /// </summary>
        private static PlayerLoopSystem[] InsertMarkers(PlayerLoopSystem[] list, List<Bucket> wanted)
        {
            var result = new List<PlayerLoopSystem>(list.Length + (wanted.Count * 2));

            for (int i = 0; i < list.Length; i++)
            {
                Bucket bucket = null;
                if (list[i].type != null)
                {
                    for (int w = 0; w < wanted.Count; w++)
                    {
                        if (wanted[w].Child == list[i].type.Name)
                        {
                            bucket = wanted[w];
                            break;
                        }
                    }
                }

                if (bucket == null)
                {
                    result.Add(list[i]);
                    continue;
                }

                // Captured by the closures below; each bucket gets its own pair.
                Bucket b = bucket;

                result.Add(new PlayerLoopSystem
                {
                    type = typeof(HighballFrameMarker),
                    updateDelegate = () => b.Start = Stopwatch.GetTimestamp()
                });

                result.Add(list[i]);

                result.Add(new PlayerLoopSystem
                {
                    type = typeof(HighballFrameMarker),
                    updateDelegate = () => b.Accum += Stopwatch.GetTimestamp() - b.Start
                });

                b.Installed = true;
            }

            return result.ToArray();
        }

        private void CountFrame()
        {
            _frames++;
        }

        /// <summary>
        /// Logs which buckets actually attached. A subsystem that does not exist in this
        /// Unity version silently contributes a permanently-zero column otherwise, which
        /// reads in the CSV exactly like a subsystem that costs nothing.
        /// </summary>
        private void ReportInstall()
        {
            var missing = new List<string>();
            for (int i = 0; i < _buckets.Length; i++)
            {
                if (!_buckets[i].Installed)
                {
                    missing.Add(_buckets[i].Parent + "." + _buckets[i].Child);
                }
            }

            if (missing.Count == 0)
            {
                Main.Log("FrameBudget: installed, timing " + _buckets.Length + " subsystems.");
                return;
            }

            Main.Log("FrameBudget: installed, timing " + (_buckets.Length - missing.Count) + " of " +
                     _buckets.Length + " subsystems. Not found in this player loop (their columns " +
                     "will read zero): " + string.Join(", ", missing.ToArray()));
        }

        public void ReleaseAll()
        {
            StopRecorders();

            // Cleared here rather than in Install so that toggling the probe off and on is
            // the documented way to retry after a failed install.
            _installFailed = false;

            if (!_installed)
            {
                return;
            }

            // Strip rather than restore the backup: another mod may have changed the loop
            // after we installed, and reverting wholesale would silently undo its work.
            try
            {
                PlayerLoopSystem current = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoop.SetPlayerLoop(StripMarkers(current));
                Main.Log("FrameBudget: markers removed, player loop restored.");
            }
            catch (Exception ex)
            {
                Main.Log("FrameBudget: strip failed (" + ex.Message + "); restoring the pre-install loop.");
                try
                {
                    PlayerLoop.SetPlayerLoop(_backup);
                }
                catch (Exception restoreEx)
                {
                    Main.Log("FrameBudget: fallback restore ALSO failed, the loop may still carry " +
                             "our markers: " + restoreEx.Message);
                }
            }

            _installed = false;
        }

        /// <summary>
        /// Recursively rebuilds the tree without any system stamped with our marker type.
        /// Every level is rebuilt into a new array, so nothing existing is mutated.
        /// </summary>
        private static PlayerLoopSystem StripMarkers(PlayerLoopSystem system)
        {
            if (system.subSystemList == null)
            {
                return system;
            }

            var kept = new List<PlayerLoopSystem>(system.subSystemList.Length);
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                PlayerLoopSystem child = system.subSystemList[i];
                if (child.type == typeof(HighballFrameMarker))
                {
                    continue;
                }

                kept.Add(StripMarkers(child));
            }

            system.subSystemList = kept.ToArray();
            return system;
        }

        // --- profiler counters ---

        /// <summary>
        /// Draw-call and batch counters are normally stripped from non-development players,
        /// so these may simply never come back valid. Started anyway because the cost of
        /// asking is four struct allocations and the answer is worth having: if they DO
        /// work, the mechanism the rendering features target becomes directly measurable
        /// instead of inferred from fps.
        /// </summary>
        private void StartRecorders()
        {
            if (_recordersStarted)
            {
                return;
            }

            try
            {
                _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
                _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
                _recordersStarted = true;
            }
            catch (Exception ex)
            {
                Main.Log("FrameBudget: profiler counters unavailable: " + ex.Message);
                return;
            }

            if (!_recorderVerdictLogged)
            {
                _recorderVerdictLogged = true;
                Main.Log(string.Format(
                    "FrameBudget: profiler counters valid? draw_calls={0} setpass={1} batches={2} triangles={3}" +
                    " (all False is the expected result in a non-development player).",
                    _drawCalls.Valid, _setPass.Valid, _batches.Valid, _triangles.Valid));
            }
        }

        private void StopRecorders()
        {
            if (!_recordersStarted)
            {
                return;
            }

            try
            {
                _drawCalls.Dispose();
                _setPass.Dispose();
                _batches.Dispose();
                _triangles.Dispose();
            }
            catch
            {
                // Nothing useful to do.
            }

            _recordersStarted = false;
        }

        private static string RecorderValue(ProfilerRecorder recorder)
        {
            return recorder.Valid
                ? recorder.LastValue.ToString(CultureInfo.InvariantCulture)
                : "na";
        }

        // --- telemetry ---

        private static double ToMs(long ticks)
        {
            return (ticks * 1000.0) / Stopwatch.Frequency;
        }

        public string[] TelemetryHeaders
        {
            get
            {
                Bucket[] buckets = _buckets ?? MakeBuckets();
                var cells = new List<string>(buckets.Length + 5) { "budget_frames" };
                for (int i = 0; i < buckets.Length; i++)
                {
                    cells.Add(buckets[i].Column);
                }

                cells.Add("draw_calls");
                cells.Add("setpass_calls");
                cells.Add("batches");
                cells.Add("triangles");
                return cells.ToArray();
            }
        }

        public string[] TelemetryValues
        {
            get
            {
                Bucket[] buckets = _buckets ?? MakeBuckets();
                var cells = new List<string>(buckets.Length + 5)
                {
                    _frames.ToString(CultureInfo.InvariantCulture)
                };

                for (int i = 0; i < buckets.Length; i++)
                {
                    cells.Add(ToMs(buckets[i].Accum).ToString("F2", CultureInfo.InvariantCulture));
                }

                cells.Add(RecorderValue(_drawCalls));
                cells.Add(RecorderValue(_setPass));
                cells.Add(RecorderValue(_batches));
                cells.Add(RecorderValue(_triangles));
                return cells.ToArray();
            }
        }

        /// <summary>
        /// Session-average ms per frame for the three groups the whole exercise exists to
        /// tell apart. Shown live in the panels so the answer is visible without waiting for
        /// a CSV, though the CSV's per-window differences are the number to trust.
        /// </summary>
        internal string StatusLine()
        {
            if (!_installed)
            {
                return _installFailed ? "install failed — see the log" : "not installed";
            }

            if (_frames == 0)
            {
                return "installed, waiting for the first frame";
            }

            double physics = 0, scripts = 0, render = 0;
            for (int i = 0; i < _buckets.Length; i++)
            {
                Bucket b = _buckets[i];
                double ms = ToMs(b.Accum);

                if (b.Column.StartsWith("phys_")) physics += ms;
                else if (b.Column.StartsWith("script_")) scripts += ms;
                else if (b.Column.StartsWith("rend_") || b.Column.StartsWith("cull_")) render += ms;
            }

            return string.Format(CultureInfo.InvariantCulture,
                "physics {0:F2}   render {1:F2}   scripts {2:F2}   present {3:F2}  (ms/frame)",
                physics / _frames, render / _frames, scripts / _frames,
                ToMs(PresentTicks()) / _frames);
        }

        private long PresentTicks()
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                if (_buckets[i].Column == "present_ms") return _buckets[i].Accum;
            }

            return 0;
        }
    }
}
