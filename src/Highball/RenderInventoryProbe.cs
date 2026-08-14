using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Read-only. The profiler counters show `batches == draw_calls` exactly (7056 = 7056
    /// against only ~450 SetPass calls) — nothing in the scene batches or instances, while
    /// thousands of objects share few distinct materials, which is precisely the situation
    /// GPU instancing exists for. This probe establishes WHY, so the decision to build (or
    /// not build) an instancing feature rests on a measurement rather than a hunch — the
    /// same discipline that avoided forking PassengerHelper.
    ///
    /// The distinguishing question is what the unique-material count looks like relative to
    /// the renderer count, and whether those materials have `enableInstancing` set. Few
    /// shared materials with instancing off is fixable from a mod (flip the flag); one
    /// unique material per renderer (e.g. a decal system instantiating per car) is a much
    /// deeper problem that a mod cannot batch away.
    ///
    /// One-shot rather than per-frame: FindObjectsOfType over a full scene plus a walk of
    /// 519 cars' renderer hierarchies costs tens of milliseconds, far too much to repeat at
    /// the evaluate cadence for data that changes slowly. Toggling the feature off and on
    /// re-arms it for another pass.
    /// </summary>
    internal sealed class RenderInventoryProbe : IFeature
    {
        private readonly IList<TrackedCar> _cars;

        private bool _ran;
        private string _status = "off";

        internal RenderInventoryProbe(IList<TrackedCar> cars)
        {
            _cars = cars;
        }

        public string Id { get { return "render_inventory"; } }
        public string DisplayName { get { return "Render inventory probe (read-only)"; } }
        public bool Enabled { get { return Settings.Instance.EnableRenderInventory; } }

        public void Tick(float deltaTime)
        {
            if (_ran)
            {
                return;
            }

            // Wait until discovery has found the cars, or the per-car half of the report
            // would silently read zero and look like an answer instead of a race.
            if (_cars.Count == 0)
            {
                _status = "waiting for cars…";
                return;
            }

            _ran = true;
            _status = "walking…";

            try
            {
                RunInventory();
            }
            catch (Exception ex)
            {
                _status = "failed";
                Main.Log("RenderInventory: walk failed: " + ex);
            }
        }

        /// <summary>
        /// Accumulates one scope's counts. CRITICAL: everything here reads ONLY
        /// Renderer.sharedMaterials and MeshFilter.sharedMesh — never .material or .mesh,
        /// whose getters silently INSTANTIATE a per-object copy on first access. A probe
        /// that claims to be read-only would otherwise mutate the scene it is measuring,
        /// leak materials, and (worse) break the very sharing it exists to count.
        /// </summary>
        private sealed class ScopeTally
        {
            internal int MeshRenderers;
            internal int SkinnedRenderers;
            internal int OtherRenderers;

            internal readonly HashSet<Material> Materials = new HashSet<Material>();
            internal readonly HashSet<Mesh> Meshes = new HashSet<Mesh>();

            internal void Add(Renderer r)
            {
                if (r == null)
                {
                    return;
                }

                if (r is MeshRenderer) MeshRenderers++;
                else if (r is SkinnedMeshRenderer) SkinnedRenderers++;
                else OtherRenderers++;

                Material[] mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null)
                        {
                            Materials.Add(mats[i]);
                        }
                    }
                }

                var skinned = r as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    if (skinned.sharedMesh != null)
                    {
                        Meshes.Add(skinned.sharedMesh);
                    }
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        Meshes.Add(mf.sharedMesh);
                    }
                }
            }

            internal int Total { get { return MeshRenderers + SkinnedRenderers + OtherRenderers; } }
        }

        private void RunInventory()
        {
            // Scope (a): the tracked rolling stock. Each car isolated, so one destroyed
            // hierarchy mid-walk cannot abort the census of the other 518.
            var carTally = new ScopeTally();
            int carsWalked = 0;

            for (int i = 0; i < _cars.Count; i++)
            {
                TrackedCar car = _cars[i];
                if (car?.Car == null)
                {
                    continue;
                }

                try
                {
                    GameObject go = car.Car.gameObject;
                    if (go == null)
                    {
                        continue;
                    }

                    Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < renderers.Length; r++)
                    {
                        carTally.Add(renderers[r]);
                    }

                    carsWalked++;
                }
                catch
                {
                    // A car destroyed mid-walk; skip it.
                }
            }

            // Scope (b): everything. Includes the cars again by design — the point of this
            // scope is the whole scene the draw-call counter sees, and subtracting the cars
            // would misstate that.
            var sceneTally = new ScopeTally();
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < all.Length; i++)
            {
                sceneTally.Add(all[i]);
            }

            var sb = new StringBuilder();
            sb.AppendLine("RenderInventory: one-shot census (toggle off and on to re-run).");
            Describe(sb, "cars (" + carsWalked + " walked)", carTally);
            Describe(sb, "scene-wide", sceneTally);
            Main.Log(sb.ToString());

            int instancing = CountInstancing(sceneTally.Materials);
            int pct = sceneTally.Materials.Count > 0
                ? (int)Math.Round(100.0 * instancing / sceneTally.Materials.Count)
                : 0;
            _status = string.Format(CultureInfo.InvariantCulture,
                "{0} mats · {1}% inst", sceneTally.Materials.Count, pct);
        }

        private static int CountInstancing(HashSet<Material> materials)
        {
            int on = 0;
            foreach (Material m in materials)
            {
                try
                {
                    if (m != null && m.enableInstancing)
                    {
                        on++;
                    }
                }
                catch
                {
                    // Destroyed between collection and read; skip.
                }
            }

            return on;
        }

        private static void Describe(StringBuilder sb, string scope, ScopeTally t)
        {
            int instancing = CountInstancing(t.Materials);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  [{0}] renderers {1} (mesh {2}, skinned {3}, other {4})  unique meshes {5}",
                scope, t.Total, t.MeshRenderers, t.SkinnedRenderers, t.OtherRenderers, t.Meshes.Count));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  [{0}] unique materials {1}: instancing ON {2}, OFF {3}",
                scope, t.Materials.Count, instancing, t.Materials.Count - instancing));

            // Shader census: many unique materials on few shaders is the instancing-friendly
            // shape; a material-per-shader spread is not.
            var byShader = new Dictionary<Shader, int>();
            foreach (Material m in t.Materials)
            {
                try
                {
                    Shader s = m != null ? m.shader : null;
                    if (s == null)
                    {
                        continue;
                    }

                    int count;
                    byShader.TryGetValue(s, out count);
                    byShader[s] = count + 1;
                }
                catch
                {
                }
            }

            var top = new List<KeyValuePair<Shader, int>>(byShader);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));

            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "  [{0}] unique shaders {1}; top by material count:", scope, byShader.Count));
            for (int i = 0; i < top.Count && i < 8; i++)
            {
                string name;
                try
                {
                    name = top[i].Key != null ? top[i].Key.name : "(destroyed)";
                }
                catch
                {
                    name = "(unreadable)";
                }

                sb.Append(i == 0 ? " " : ", ");
                sb.Append(name).Append(" ×").Append(top[i].Value.ToString(CultureInfo.InvariantCulture));
            }

            sb.AppendLine();
        }

        /// <summary>Nothing held, nothing to hand back — but a toggle-off re-arms the walk.</summary>
        public void ReleaseAll()
        {
            _ran = false;
            _status = "off";
        }

        /// <summary>
        /// Reports nothing through the CSV: the census is a one-shot snapshot, not a
        /// time series, and its interesting output is shader names a fixed column set
        /// cannot express. It goes to the log instead.
        /// </summary>
        public string[] TelemetryHeaders { get { return new string[0]; } }

        public string[] TelemetryValues { get { return new string[0]; } }

        internal string StatusLine()
        {
            return _status;
        }
    }
}
