using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Read-only. Splits the single biggest cost the frame budget probe found —
    /// `script_fixed_ms`, C# running in FixedUpdate, measured at 6.85 ms of a 21.88 ms
    /// frame and explaining 84% of frame-time variance — into a ranked list of who is
    /// actually spending it.
    ///
    /// Unity invokes every MonoBehaviour's FixedUpdate from native code, which is why they
    /// all collapse into one player-loop subsystem and why the frame budget probe cannot
    /// separate them. They are ordinary managed methods though, so Harmony can patch each
    /// one individually and time it. The report names the declaring assembly, so a mod's
    /// cost is distinguishable from the base game's without disabling anything — which
    /// matters here, where most of the ~45 installed mods are load-bearing.
    ///
    /// This patches hundreds of methods belonging to other people's code, which makes it
    /// the broadest thing in this mod by a distance. It ships off, patches under its own
    /// Harmony id so it can be removed without touching the preferences patch, and isolates
    /// every individual Patch call so one unpatchable method cannot abort the sweep.
    /// </summary>
    internal sealed class ScriptAttributionProbe : IFeature
    {
        private const string HarmonyId = "highball.scriptattrib";

        /// <summary>
        /// FixedUpdate is the bucket that dominates the frame; Update is included because it
        /// is the same machinery for another ~2 ms/frame and answering both at once costs
        /// only the extra patches.
        /// </summary>
        private static readonly string[] TargetMethods = { "FixedUpdate", "Update" };

        private sealed class Counter
        {
            internal string Label;
            internal long Ticks;
            internal long Calls;
        }

        // Static because the Harmony patches are static and have no other route back here.
        // Populated entirely at patch time, so the hot path only ever looks up and adds.
        private static readonly Dictionary<MethodBase, Counter> Counters =
            new Dictionary<MethodBase, Counter>();

        private static readonly List<Counter> All = new List<Counter>();

        /// <summary>
        /// Calls whose MethodBase did not resolve to a Counter. Should stay zero; if it does
        /// not, the dictionary key is not matching what Harmony passes back and the whole
        /// ranking is understated rather than merely incomplete. Better surfaced than silent.
        /// </summary>
        private static long _unattributed;

        private Harmony _harmony;
        private bool _installed;
        private bool _installFailed;
        private float _timer;
        private int _patched;
        private int _failed;

        public string Id { get { return "script_attrib"; } }
        public string DisplayName { get { return "Script attribution probe (read-only)"; } }
        public bool Enabled { get { return Settings.Instance.EnableScriptAttribution; } }

        public void Tick(float deltaTime)
        {
            if (!_installed)
            {
                if (!_installFailed)
                {
                    Install();
                }

                return;
            }

            _timer += deltaTime;

            // Same cadence as the telemetry rows, so a report lines up with a CSV window and
            // the two can be read side by side.
            if (_timer < Settings.Instance.TelemetryIntervalSeconds)
            {
                return;
            }

            Report(_timer);
            _timer = 0f;
        }

        private void Install()
        {
            var sw = Stopwatch.StartNew();

            try
            {
                _harmony = new Harmony(HarmonyId);

                var prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(ScriptAttributionProbe), nameof(TimingPrefix)));
                var postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(ScriptAttributionProbe), nameof(TimingPostfix)));

                Assembly self = typeof(ScriptAttributionProbe).Assembly;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

                for (int a = 0; a < assemblies.Length; a++)
                {
                    Assembly asm = assemblies[a];
                    if (ReferenceEquals(asm, self))
                    {
                        continue;
                    }

                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // Partially-loadable assemblies still yield their resolvable types.
                        types = ex.Types;
                    }
                    catch
                    {
                        continue;
                    }

                    string asmName = asm.GetName().Name;

                    for (int t = 0; t < types.Length; t++)
                    {
                        Type type = types[t];
                        if (type == null || type.IsAbstract || type.IsGenericTypeDefinition)
                        {
                            continue;
                        }

                        if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                        {
                            continue;
                        }

                        PatchTargets(type, asmName, prefix, postfix);
                    }
                }

                _installed = true;
                sw.Stop();

                Main.Log(string.Format(CultureInfo.InvariantCulture,
                    "ScriptAttrib: patched {0} methods across {1} assemblies in {2:F2}s ({3} failed). " +
                    "Timing adds roughly 75 ns per call; expect a few tenths of a ms per frame of " +
                    "overhead, enough to note but not enough to reorder the ranking.",
                    _patched, assemblies.Length, sw.Elapsed.TotalSeconds, _failed));
            }
            catch (Exception ex)
            {
                Main.Log("ScriptAttrib: install failed, removing any patches applied: " + ex);
                _installFailed = true;
                Uninstall();
            }
        }

        private void PatchTargets(Type type, string asmName, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            for (int m = 0; m < TargetMethods.Length; m++)
            {
                MethodInfo method;
                try
                {
                    // DeclaredOnly: a subclass that inherits FixedUpdate must not be patched
                    // a second time through its parent, which would double-count every call.
                    method = type.GetMethod(
                        TargetMethods[m],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly,
                        null, Type.EmptyTypes, null);
                }
                catch
                {
                    continue;
                }

                if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    _harmony.Patch(method, prefix, postfix);

                    var counter = new Counter
                    {
                        Label = asmName + ":" + type.Name + "." + TargetMethods[m]
                    };

                    Counters[method] = counter;
                    All.Add(counter);
                    _patched++;
                }
                catch
                {
                    // A method Harmony cannot patch (inlined, abstract in practice, already
                    // patched in an incompatible way) is skipped, not fatal. Counted so a
                    // sweep that silently patched almost nothing is visible in the log.
                    _failed++;
                }
            }
        }

        private static void TimingPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void TimingPostfix(MethodBase __originalMethod, long __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state;

            Counter counter;
            if (Counters.TryGetValue(__originalMethod, out counter))
            {
                counter.Ticks += elapsed;
                counter.Calls++;
            }
            else
            {
                _unattributed++;
            }
        }

        private void Report(float seconds)
        {
            if (seconds <= 0f)
            {
                return;
            }

            All.Sort((x, y) => y.Ticks.CompareTo(x.Ticks));

            double totalMs = 0;
            for (int i = 0; i < All.Count; i++)
            {
                totalMs += ToMs(All[i].Ticks);
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "ScriptAttrib: {0:F1}s window, {1:F1} ms/s across {2} patched methods{3}. Top 20:",
                seconds, totalMs / seconds, _patched,
                _unattributed > 0 ? ", " + _unattributed + " UNATTRIBUTED calls" : string.Empty));

            int shown = 0;
            for (int i = 0; i < All.Count && shown < 20; i++)
            {
                Counter c = All[i];
                if (c.Ticks <= 0)
                {
                    break;
                }

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,8:F2} ms/s  {1,9:F0} calls/s  {2}",
                    ToMs(c.Ticks) / seconds, c.Calls / seconds, c.Label));
                shown++;
            }

            // One multi-line call rather than 21 separate ones: UMM prefixes every Log with
            // the mod name, and 21 prefixed lines every window is noise in a log that has to
            // stay readable.
            Main.Log(sb.ToString());

            for (int i = 0; i < All.Count; i++)
            {
                All[i].Ticks = 0;
                All[i].Calls = 0;
            }

            _unattributed = 0;
        }

        private static double ToMs(long ticks)
        {
            return (ticks * 1000.0) / Stopwatch.Frequency;
        }

        public void ReleaseAll()
        {
            _installFailed = false;

            if (!_installed)
            {
                return;
            }

            Uninstall();
        }

        private void Uninstall()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
                Main.Log("ScriptAttrib: patches removed.");
            }
            catch (Exception ex)
            {
                Main.Log("ScriptAttrib: unpatch failed, timing patches may still be live: " + ex.Message);
            }

            _harmony = null;
            _installed = false;
            _patched = 0;
            _failed = 0;
            _timer = 0f;

            Counters.Clear();
            All.Clear();
            _unattributed = 0;
        }

        /// <summary>
        /// Reports nothing through the CSV: the interesting output is a ranked list whose
        /// rows change from session to session, which a fixed column set cannot express.
        /// It goes to the log instead.
        /// </summary>
        public string[] TelemetryHeaders { get { return new string[0]; } }

        public string[] TelemetryValues { get { return new string[0]; } }

        internal string StatusLine()
        {
            if (!_installed)
            {
                return _installFailed ? "install failed" : "off";
            }

            return _patched + " patched → log";
        }
    }
}
