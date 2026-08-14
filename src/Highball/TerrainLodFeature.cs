using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Shortens the distance at which trees become batched billboards, caps how many trees
    /// render at full 3D LOD, and shortens ground-detail draw distance.
    ///
    /// Density is never touched. Neither is treeDistance, which is the cull distance —
    /// lowering that makes trees vanish rather than simplify, which is the outcome this
    /// feature exists to avoid.
    ///
    /// Unity draws 3D terrain trees individually but batches billboards into one mesh, so
    /// this is primarily a draw-call reduction, which is a CPU saving.
    /// </summary>
    internal sealed class TerrainLodFeature : IFeature
    {
        private sealed class TerrainState
        {
            public Terrain Terrain;

            public float OriginalBillboardDistance;
            public int OriginalMaxFullLod;
            public float OriginalCrossFade;
            public float OriginalDetailDistance;

            // What this feature last wrote. If the terrain no longer matches, something
            // outside changed it and the originals must be re-captured rather than
            // clobbering the player's new choice.
            public float WroteBillboardDistance;
            public int WroteMaxFullLod;
            public float WroteCrossFade;
            public float WroteDetailDistance;

            public bool Applied;
        }

        private readonly List<TerrainState> _terrains = new List<TerrainState>();
        private float _timer;
        private bool _reported;

        internal int TerrainCount { get { return _terrains.Count; } }

        public string Id { get { return "terrain_lod"; } }
        public string DisplayName { get { return "Tree & ground detail LOD"; } }
        public bool IsExperimental { get { return true; } }
        public bool Enabled { get { return Settings.Instance.EnableTerrainLod; } }
        public bool Active { get; set; }

        public void Tick(float deltaTime)
        {
            _timer += deltaTime;
            if (_timer < 2f && _terrains.Count > 0)
            {
                return;
            }

            _timer = 0f;
            Apply();
        }

        private void Apply()
        {
            Terrain[] active = Terrain.activeTerrains;

            // Drop terrains that have gone away.
            for (int i = _terrains.Count - 1; i >= 0; i--)
            {
                if (_terrains[i].Terrain == null)
                {
                    _terrains.RemoveAt(i);
                }
            }

            for (int i = 0; i < active.Length; i++)
            {
                Terrain t = active[i];
                if (t == null)
                {
                    continue;
                }

                TerrainState s = Find(t);
                if (s == null)
                {
                    s = Capture(t);
                    _terrains.Add(s);
                }
                else if (s.Applied)
                {
                    // Something outside changed these — the game re-applies its own values
                    // when graphics settings change. Yield to it rather than fight.
                    if (t.treeBillboardDistance != s.WroteBillboardDistance
                        || t.treeMaximumFullLODCount != s.WroteMaxFullLod
                        || t.treeCrossFadeLength != s.WroteCrossFade
                        || t.detailObjectDistance != s.WroteDetailDistance)
                    {
                        Recapture(s, t);
                    }
                }

                Settings cfg = Settings.Instance;

                s.WroteBillboardDistance = Decisions.ClampReduction(
                    cfg.TreeBillboardDistanceMeters, s.OriginalBillboardDistance);
                s.WroteMaxFullLod = Decisions.ClampReductionInt(
                    cfg.TreeMaxFullLodCount, s.OriginalMaxFullLod);
                s.WroteDetailDistance = Decisions.ClampReduction(
                    cfg.DetailObjectDistanceMeters, s.OriginalDetailDistance);
                s.WroteCrossFade = cfg.TreeCrossFadeLengthMeters;

                try
                {
                    // Set before the writes, not after: if this throws partway through, the
                    // terrain is already partially mutated, and ReleaseAll must still restore
                    // it. Restoring a terrain we never actually changed is harmless — it just
                    // writes back the values we captured from it — but failing to restore one
                    // we did change is not.
                    s.Applied = true;
                    t.treeBillboardDistance = s.WroteBillboardDistance;
                    t.treeMaximumFullLODCount = s.WroteMaxFullLod;
                    t.treeCrossFadeLength = s.WroteCrossFade;
                    t.detailObjectDistance = s.WroteDetailDistance;
                }
                catch (Exception ex)
                {
                    Main.Log("TerrainLod: write failed: " + ex.Message);
                }
            }

            if (!_reported && _terrains.Count > 0)
            {
                _reported = true;
                TerrainState f = _terrains[0];
                Main.Log(string.Format(CultureInfo.InvariantCulture,
                    "TerrainLod: {0} terrain(s). Game defaults: billboard={1} maxFullLOD={2} crossfade={3} detail={4}",
                    _terrains.Count, f.OriginalBillboardDistance, f.OriginalMaxFullLod,
                    f.OriginalCrossFade, f.OriginalDetailDistance));
            }
        }

        private TerrainState Find(Terrain t)
        {
            for (int i = 0; i < _terrains.Count; i++)
            {
                if (ReferenceEquals(_terrains[i].Terrain, t)) return _terrains[i];
            }

            return null;
        }

        private static TerrainState Capture(Terrain t)
        {
            var s = new TerrainState { Terrain = t };
            Recapture(s, t);
            return s;
        }

        private static void Recapture(TerrainState s, Terrain t)
        {
            s.OriginalBillboardDistance = t.treeBillboardDistance;
            s.OriginalMaxFullLod = t.treeMaximumFullLODCount;
            s.OriginalCrossFade = t.treeCrossFadeLength;
            s.OriginalDetailDistance = t.detailObjectDistance;
            s.Applied = false;
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < _terrains.Count; i++)
            {
                TerrainState s = _terrains[i];
                if (s.Terrain == null || !s.Applied)
                {
                    continue;
                }

                try
                {
                    s.Terrain.treeBillboardDistance = s.OriginalBillboardDistance;
                    s.Terrain.treeMaximumFullLODCount = s.OriginalMaxFullLod;
                    s.Terrain.treeCrossFadeLength = s.OriginalCrossFade;
                    s.Terrain.detailObjectDistance = s.OriginalDetailDistance;
                }
                catch
                {
                    // Destroyed terrain; nothing to restore to.
                }

                s.Applied = false;
            }
        }

        public string[] TelemetryHeaders
        {
            get { return new[] { "terrains", "tree_billboard_distance", "detail_object_distance" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                float bb = _terrains.Count > 0 ? _terrains[0].WroteBillboardDistance : 0f;
                float dd = _terrains.Count > 0 ? _terrains[0].WroteDetailDistance : 0f;
                return new[]
                {
                    _terrains.Count.ToString(CultureInfo.InvariantCulture),
                    bb.ToString("F1", CultureInfo.InvariantCulture),
                    dd.ToString("F1", CultureInfo.InvariantCulture)
                };
            }
        }
    }
}
