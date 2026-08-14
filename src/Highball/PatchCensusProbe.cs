using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Highball
{
    /// <summary>
    /// Read-only. The script attribution numbers have a blind spot: a Harmony patch
    /// executes inside the patched method's time, so a mod's prefix/postfix on a game
    /// method masquerades as base-game cost in the attribution ranking.
    /// `TrainController.FixedUpdate`'s 122 ms/s — and the air path under it
    /// (`FixedUpdateAir`/`UpdateBrakeLine`/`UpdateAir`/`UpdateBrakingForce`) — may
    /// therefore include mod overhead that nothing measured so far could see. This census
    /// makes every patch in the process visible without disabling anything, which matters
    /// on an install where the mods are load-bearing.
    ///
    /// Transpilers deserve special attention in the output: they rewrite the target's IL
    /// in place, so their cost (and their bugs) are attributed to the *game's* method by
    /// every profiling tool, and unlike prefixes they do not even appear as a frame on any
    /// call path.
    ///
    /// Highball's own ids ("highball" for the preferences tab, "highball.scriptattrib" for
    /// the attribution probe when enabled) will appear in the census and are expected;
    /// they are tagged rather than filtered because completeness beats tidiness — a census
    /// that hides its own instrument invites the next reader to distrust the rest of it.
    ///
    /// One-shot, exactly like RenderInventoryProbe: run once when enabled, re-armed by
    /// toggling off and on. Patch tables change when mods load or patch lazily, so a
    /// re-run after suspicious activity is cheap and explicit rather than continuous.
    /// </summary>
    internal sealed class PatchCensusProbe : IFeature
    {
        /// <summary>Longest the by-owner section may grow, so one aggressively-patching
        /// mod cannot turn the census into a scroll. The hot-path section is printed
        /// completely on purpose — it is the headline — but is capped far above any
        /// realistic size as a backstop.</summary>
        private const int MaxOwnersShown = 20;
        private const int MaxHotShown = 25;
        private const int MaxExamplesPerOwner = 8;

        private bool _ran;
        private string _status = "off";

        public string Id { get { return "patch_census"; } }
        public string DisplayName { get { return "Harmony patch census (read-only)"; } }
        public bool Enabled { get { return Settings.Instance.EnablePatchCensus; } }

        public void Tick(float deltaTime)
        {
            if (_ran)
            {
                return;
            }

            _ran = true;
            _status = "walking…";

            try
            {
                RunCensus();
            }
            catch (Exception ex)
            {
                _status = "failed";
                Main.Log("PatchCensus: census failed: " + ex);
            }
        }

        private sealed class OwnerTally
        {
            internal readonly HashSet<MethodBase> Methods = new HashSet<MethodBase>();
            internal int Prefixes;
            internal int Postfixes;
            internal int Transpilers;
            internal int Finalizers;
            internal readonly List<string> Examples = new List<string>();

            internal void AddExample(string target)
            {
                if (Examples.Count < MaxExamplesPerOwner && !Examples.Contains(target))
                {
                    Examples.Add(target);
                }
            }
        }

        private void RunCensus()
        {
            var byOwner = new Dictionary<string, OwnerTally>();
            var hotLines = new List<string>();
            int methodsTotal = 0;
            int patchesTotal = 0;

            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                // Isolated per method: one unreadable entry (unloadable type, hostile
                // ToString) must not abort the census of everything else.
                try
                {
                    if (method == null)
                    {
                        continue;
                    }

                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null)
                    {
                        continue;
                    }

                    methodsTotal++;
                    string target = TargetName(method);

                    patchesTotal += Tally(byOwner, info.Prefixes, method, target, PatchKind.Prefix);
                    patchesTotal += Tally(byOwner, info.Postfixes, method, target, PatchKind.Postfix);
                    patchesTotal += Tally(byOwner, info.Transpilers, method, target, PatchKind.Transpiler);
                    patchesTotal += Tally(byOwner, info.Finalizers, method, target, PatchKind.Finalizer);

                    if (IsHotPath(method))
                    {
                        hotLines.Add(DescribeHot(target, info));
                    }
                }
                catch
                {
                    // Skip the entry; the totals will simply not include it.
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("PatchCensus: one-shot Harmony census (toggle off and on to re-run). " +
                          "Highball's own ids appear below and are expected, tagged \"(Highball itself)\".");

            // Section 1 — the hot path. Absence must be a visible result: a reader asking
            // "is a mod inside the air simulation?" needs "no" stated, not inferred from a
            // missing line.
            if (hotLines.Count == 0)
            {
                sb.AppendLine("  [hot path] no patches found on the air path " +
                              "(TrainController / Car / Hose / *Air* / *Brake*).");
            }
            else
            {
                sb.AppendLine("  [hot path] " + hotLines.Count +
                              " patched method(s) on TrainController / Car / Hose / *Air* / *Brake*:");
                for (int i = 0; i < hotLines.Count && i < MaxHotShown; i++)
                {
                    sb.AppendLine("    " + hotLines[i]);
                }

                if (hotLines.Count > MaxHotShown)
                {
                    sb.AppendLine("    … +" + (hotLines.Count - MaxHotShown) + " more");
                }
            }

            // Section 2 — by owner, heaviest first.
            var owners = new List<KeyValuePair<string, OwnerTally>>(byOwner);
            owners.Sort((x, y) => y.Value.Methods.Count.CompareTo(x.Value.Methods.Count));

            for (int i = 0; i < owners.Count && i < MaxOwnersShown; i++)
            {
                OwnerTally t = owners[i].Value;
                sb.Append("  [owner] ").Append(OwnerLabel(owners[i].Key))
                  .Append(": ").Append(t.Methods.Count).Append(" methods (");

                bool first = true;
                first = AppendKind(sb, "prefix", t.Prefixes, first);
                first = AppendKind(sb, "postfix", t.Postfixes, first);
                first = AppendKind(sb, "TRANSPILER", t.Transpilers, first);
                AppendKind(sb, "finalizer", t.Finalizers, first);

                sb.Append(") e.g. ").Append(string.Join(", ", t.Examples.ToArray()));
                sb.AppendLine();
            }

            if (owners.Count > MaxOwnersShown)
            {
                sb.AppendLine("  [owner] … +" + (owners.Count - MaxOwnersShown) + " more owners");
            }

            // Section 3 — totals.
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  [totals] {0} patched methods, {1} patches, {2} owners",
                methodsTotal, patchesTotal, byOwner.Count));

            Main.Log(sb.ToString());

            _status = string.Format(CultureInfo.InvariantCulture,
                "{0} methods · {1} owners", methodsTotal, byOwner.Count);
        }

        private enum PatchKind { Prefix, Postfix, Transpiler, Finalizer }

        /// <summary>
        /// Appends "kind N" to the by-owner line when N is non-zero, comma-separating after
        /// the first. Returns the updated first-item flag. Zero-count kinds are omitted so
        /// the common prefix-only owner reads "(prefix 12)" rather than carrying three
        /// noisy zeroes.
        /// </summary>
        private static bool AppendKind(StringBuilder sb, string kind, int count, bool first)
        {
            if (count == 0)
            {
                return first;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append(kind).Append(' ').Append(count.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        private static int Tally(
            Dictionary<string, OwnerTally> byOwner,
            IList<Patch> patches,
            MethodBase targetMethod,
            string target,
            PatchKind kind)
        {
            if (patches == null)
            {
                return 0;
            }

            int counted = 0;
            for (int i = 0; i < patches.Count; i++)
            {
                try
                {
                    Patch p = patches[i];
                    if (p == null)
                    {
                        continue;
                    }

                    string owner = string.IsNullOrEmpty(p.owner) ? "(no owner id)" : p.owner;

                    OwnerTally t;
                    if (!byOwner.TryGetValue(owner, out t))
                    {
                        t = new OwnerTally();
                        byOwner[owner] = t;
                    }

                    switch (kind)
                    {
                        case PatchKind.Prefix: t.Prefixes++; break;
                        case PatchKind.Postfix: t.Postfixes++; break;
                        case PatchKind.Transpiler: t.Transpilers++; break;
                        case PatchKind.Finalizer: t.Finalizers++; break;
                    }

                    // Methods is a set of the *targets* this owner touches — the same
                    // target with both a prefix and a postfix counts once. Deliberately NOT
                    // p.PatchMethod, which is the owner's own patch method and would count
                    // the owner's code rather than the game surface it covers.
                    t.Methods.Add(targetMethod);

                    t.AddExample(target);
                    counted++;
                }
                catch
                {
                }
            }

            return counted;
        }

        /// <summary>
        /// The hot-path filter: the types the decompile identified as the frame's dominant
        /// per-step cost, plus anything air- or brake-named anywhere — the air simulation
        /// is spread across more types than the three we know by name, and a name match is
        /// cheaper than being wrong about the type list.
        /// </summary>
        private static bool IsHotPath(MethodBase method)
        {
            Type t = method.DeclaringType;
            string typeName = t != null ? t.Name : string.Empty;

            if (typeName == "TrainController" || typeName == "Car" || typeName == "Hose")
            {
                return true;
            }

            if (typeName.IndexOf("Air", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            string name = method.Name;
            return name.IndexOf("Air", StringComparison.Ordinal) >= 0
                || name.IndexOf("Brake", StringComparison.Ordinal) >= 0;
        }

        private static string DescribeHot(string target, Patches info)
        {
            var sb = new StringBuilder(target);
            sb.Append(" —");

            AppendHotKind(sb, "prefix", info.Prefixes);
            AppendHotKind(sb, "postfix", info.Postfixes);
            AppendHotKind(sb, "TRANSPILER", info.Transpilers);
            AppendHotKind(sb, "finalizer", info.Finalizers);

            return sb.ToString();
        }

        private static void AppendHotKind(StringBuilder sb, string kind, IList<Patch> patches)
        {
            if (patches == null || patches.Count == 0)
            {
                return;
            }

            sb.Append(' ').Append(kind).Append(": ");
            for (int i = 0; i < patches.Count; i++)
            {
                try
                {
                    Patch p = patches[i];
                    if (p == null)
                    {
                        continue;
                    }

                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(OwnerLabel(string.IsNullOrEmpty(p.owner) ? "(no owner id)" : p.owner));
                    sb.Append(" [").Append(PatchAssembly(p)).Append(']');
                }
                catch
                {
                    sb.Append("(unreadable)");
                }
            }

            sb.Append(';');
        }

        private static string PatchAssembly(Patch p)
        {
            try
            {
                MethodInfo m = p.PatchMethod;
                Type t = m != null ? m.DeclaringType : null;
                return t != null ? t.Assembly.GetName().Name : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string OwnerLabel(string owner)
        {
            if (owner == "highball" || owner == "highball.scriptattrib")
            {
                return owner + " (Highball itself)";
            }

            return owner;
        }

        private static string TargetName(MethodBase method)
        {
            try
            {
                Type t = method.DeclaringType;
                return (t != null ? t.Name : "?") + "." + method.Name;
            }
            catch
            {
                return "(unreadable)";
            }
        }

        /// <summary>Nothing held, nothing to hand back — but a toggle-off re-arms the census.</summary>
        public void ReleaseAll()
        {
            _ran = false;
            _status = "off";
        }

        /// <summary>
        /// Reports nothing through the CSV: the census is a one-shot snapshot whose
        /// interesting output is owner ids and method names a fixed column set cannot
        /// express. It goes to the log instead.
        /// </summary>
        public string[] TelemetryHeaders { get { return new string[0]; } }

        public string[] TelemetryValues { get { return new string[0]; } }

        internal string StatusLine()
        {
            return _status;
        }
    }
}
