using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace AEProbe
{
    /// <summary>
    /// Read-only instrumentation. Accumulates timing for AutoEngineerPlanner.UpdateTargets
    /// and the pathfinding it calls into, then flushes an aggregate row to CSV periodically.
    ///
    /// Deliberately makes zero writes to game state. If any part of this misbehaves the
    /// correct response is to delete the mod, not to debug it in place.
    /// </summary>
    internal static class Probe
    {
        // Flush cadence. 2s keeps a one-minute session at ~30 rows, which is enough
        // to see variance without drowning in noise.
        internal const float FlushIntervalSeconds = 2f;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // --- accumulators for the current window (Unity main thread only, no locking) ---
        private static long _utCalls;
        private static long _utTicks;
        private static long _tfdCalls;
        private static long _tfdTicks;
        private static int _frames;
        private static float _frameSeconds;
        private static readonly HashSet<int> _plannersSeen = new HashSet<int>();

        private static float _timer;
        private static StreamWriter _writer;
        private static bool _headerWritten;
        private static int _rowsWritten;

        internal static string CsvPath { get; private set; }
        internal static bool TfdPatched { get; private set; }

        internal static void Init()
        {
            try
            {
                CsvPath = Path.Combine(Application.persistentDataPath, "AEProbe.csv");
                _writer = new StreamWriter(CsvPath, append: true) { AutoFlush = true };

                // Session marker so multiple launches stay distinguishable in one file.
                _writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));

                if (!_headerWritten)
                {
                    _writer.WriteLine(string.Join(",", new[]
                    {
                        "wall_clock",
                        "window_s",
                        "frames",
                        "avg_frame_ms",
                        "fps",
                        "planners",
                        "ut_calls",
                        "ut_calls_per_s",
                        "ut_ms_per_s",
                        "ut_pct_frame",
                        "tfd_calls",
                        "tfd_calls_per_s",
                        "tfd_ms_per_s",
                        "tfd_pct_frame"
                    }));
                    _headerWritten = true;
                }

                Main.Log("Writing measurements to " + CsvPath);
            }
            catch (Exception ex)
            {
                Main.Log("Could not open CSV for writing: " + ex.Message);
                _writer = null;
            }
        }

        internal static void Shutdown()
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Nothing useful to do on shutdown failure.
            }
            _writer = null;
        }

        // --- recording hooks, called from the Harmony patches ---

        internal static void RecordUpdateTargets(long elapsedTicks, object planner)
        {
            _utCalls++;
            _utTicks += elapsedTicks;
            if (planner != null)
            {
                _plannersSeen.Add(RuntimeHelpers.GetHashCode(planner));
            }
        }

        internal static void RecordTryFindDistance(long elapsedTicks)
        {
            _tfdCalls++;
            _tfdTicks += elapsedTicks;
        }

        // --- per-frame driver ---

        internal static void Tick(float deltaTime)
        {
            _frames++;
            _frameSeconds += deltaTime;
            _timer += deltaTime;

            if (_timer < FlushIntervalSeconds)
            {
                return;
            }

            Flush();
        }

        private static void Flush()
        {
            float window = _timer;
            _timer = 0f;

            // A window with no frames tells us nothing and would divide by zero.
            if (_frames == 0 || window <= 0f)
            {
                ResetWindow();
                return;
            }

            double utMs = _utTicks * TicksToMs;
            double tfdMs = _tfdTicks * TicksToMs;
            double frameMs = (_frameSeconds * 1000.0) / _frames;
            double fps = _frames / window;

            // Share of total elapsed wall time spent inside the measured call. Note that
            // tfd time is nested INSIDE ut time, so these two percentages overlap and
            // must not be summed.
            double totalMs = window * 1000.0;
            double utPct = (utMs / totalMs) * 100.0;
            double tfdPct = (tfdMs / totalMs) * 100.0;

            WriteRow(new[]
            {
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                F(window),
                _frames.ToString(CultureInfo.InvariantCulture),
                F(frameMs),
                F(fps),
                _plannersSeen.Count.ToString(CultureInfo.InvariantCulture),
                _utCalls.ToString(CultureInfo.InvariantCulture),
                F(_utCalls / window),
                F(utMs / window),
                F(utPct),
                _tfdCalls.ToString(CultureInfo.InvariantCulture),
                F(_tfdCalls / window),
                F(tfdMs / window),
                F(tfdPct)
            });

            ResetWindow();
        }

        private static void ResetWindow()
        {
            _utCalls = 0;
            _utTicks = 0;
            _tfdCalls = 0;
            _tfdTicks = 0;
            _frames = 0;
            _frameSeconds = 0f;
            _plannersSeen.Clear();
        }

        private static void WriteRow(string[] cells)
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
                Main.Log("CSV write failed, disabling output: " + ex.Message);
                Shutdown();
            }
        }

        private static string F(double v)
        {
            return v.ToString("F3", CultureInfo.InvariantCulture);
        }

        internal static string StatusLine()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "rows={0} tfdPatched={1} csv={2}",
                _rowsWritten,
                TfdPatched,
                CsvPath ?? "(none)");
        }

        // --- patch installation ---

        /// <summary>
        /// TryFindDistance is a secondary measurement. It may have overloads, or may be
        /// renamed by a game update. Failing to patch it must not prevent the primary
        /// UpdateTargets measurement from running, so it is patched manually and any
        /// failure is logged rather than thrown.
        /// </summary>
        internal static void TryPatchPathfinding(Harmony harmony)
        {
            try
            {
                Type t = AccessTools.TypeByName("Track.Search.GraphRouteSearchExtension");
                if (t == null)
                {
                    Main.Log("Pathfinding type not found; skipping TryFindDistance measurement.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(Patch_TryFindDistance)
                    .GetMethod(nameof(Patch_TryFindDistance.Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                var postfix = new HarmonyMethod(typeof(Patch_TryFindDistance)
                    .GetMethod(nameof(Patch_TryFindDistance.Postfix), BindingFlags.Static | BindingFlags.NonPublic));

                int patched = 0;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "TryFindDistance" || m.IsAbstract || m.ContainsGenericParameters)
                    {
                        continue;
                    }

                    try
                    {
                        harmony.Patch(m, prefix, postfix);
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        Main.Log("Could not patch a TryFindDistance overload: " + ex.Message);
                    }
                }

                TfdPatched = patched > 0;
                Main.Log("Patched " + patched + " TryFindDistance overload(s).");
            }
            catch (Exception ex)
            {
                Main.Log("Pathfinding patch setup failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Primary measurement. __state carries the start timestamp so nested or re-entrant
    /// calls are timed correctly without a manual depth counter.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_UpdateTargets
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Model.AI.AutoEngineerPlanner), "UpdateTargets")]
        private static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Model.AI.AutoEngineerPlanner), "UpdateTargets")]
        private static void Postfix(long __state, Model.AI.AutoEngineerPlanner __instance)
        {
            if (!Main.Enabled)
            {
                return;
            }

            try
            {
                Probe.RecordUpdateTargets(Stopwatch.GetTimestamp() - __state, __instance);
            }
            catch
            {
                // Never let instrumentation throw into game code.
            }
        }
    }

    /// <summary>
    /// Secondary measurement, installed manually by Probe.TryPatchPathfinding.
    /// </summary>
    internal static class Patch_TryFindDistance
    {
        internal static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        internal static void Postfix(long __state)
        {
            if (!Main.Enabled)
            {
                return;
            }

            try
            {
                Probe.RecordTryFindDistance(Stopwatch.GetTimestamp() - __state);
            }
            catch
            {
                // Never let instrumentation throw into game code.
            }
        }
    }
}
