using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Flips <c>Material.enableInstancing</c> on across the scene, so renderers that share
    /// a mesh and a material become eligible to draw as one instanced batch instead of one
    /// draw call each.
    ///
    /// Why this exists: the render census measured 17,676 car renderers over only 2,708
    /// unique materials (~6.5 renderers per material, similar per mesh) with instancing OFF
    /// on 99% of them — and the profiler counters show batches == draw_calls exactly, i.e.
    /// nothing in the scene batches at all. Real sharing plus a universally-unset opt-in
    /// flag is precisely the situation the flag exists for.
    ///
    /// KNOWN UNKNOWNS, deliberately left to measurement:
    /// - If a shader was not compiled with instancing variants, setting the flag on its
    ///   materials is inert. The counters will simply not move; that null result is the
    ///   experiment working, not failing.
    /// - Per-renderer MaterialPropertyBlocks can break batching eligibility object by
    ///   object even where the flag helps.
    /// - Success is judged in order: first the draw_calls vs batches telemetry columns
    ///   diverging, then a traffic-controlled fps A/B. An fps claim without the counter
    ///   movement is noise; counter movement without fps is another measurable-but-
    ///   worthless mechanism, which this project has already collected three of.
    ///
    /// Follows the TerrainLodFeature pattern: a global-state feature driven from Tick,
    /// never claiming cars. Everything is restored on toggle-off and unload. Runtime-only
    /// either way — material assets on disk cannot be modified from a player build, so
    /// nothing can persist across a game restart; the restore exists so a toggle-off within
    /// a session is a true revert.
    /// </summary>
    internal sealed class InstancingFeature : IFeature
    {
        /// <summary>Cheap pass cadence; matches TerrainLodFeature's rhythm.</summary>
        private const float TickIntervalSeconds = 5f;

        /// <summary>
        /// Full-scene sweep cadence. FindObjectsOfType over ~19k renderers costs enough
        /// that repeating it every tick would be this mod working against itself; newly
        /// spawned materials wait at most this long.
        /// </summary>
        private const float SweepIntervalSeconds = 30f;

        private readonly IList<TrackedCar> _cars;

        /// <summary>
        /// Every material ever examined this session, flipped or not, so repeat passes
        /// skip the (large) already-seen majority and only pay for genuinely new arrivals.
        /// </summary>
        private readonly HashSet<Material> _seen = new HashSet<Material>();

        /// <summary>
        /// Exactly the materials THIS feature turned on — the restore list. A material that
        /// already had instancing enabled (the census counted 23) is never recorded here,
        /// so ReleaseAll cannot turn off something the game itself turned on.
        /// </summary>
        private readonly List<Material> _flipped = new List<Material>();

        private float _tickTimer;
        private float _sweepTimer = SweepIntervalSeconds; // first pass sweeps immediately
        private int _failures;
        private bool _reported;

        internal InstancingFeature(IList<TrackedCar> cars)
        {
            _cars = cars;
        }

        public string Id { get { return "gpu_instancing"; } }
        public string DisplayName { get { return "GPU instancing"; } }
        public bool Enabled { get { return Settings.Instance.EnableGpuInstancing; } }

        public void Tick(float deltaTime)
        {
            _tickTimer += deltaTime;
            if (_tickTimer < TickIntervalSeconds)
            {
                return;
            }

            _sweepTimer += _tickTimer;
            _tickTimer = 0f;

            // The car walk is comparatively cheap and cars are the dominant material
            // population, so they refresh every tick; the whole-scene sweep is throttled
            // separately.
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
                        ProcessRenderer(renderers[r]);
                    }
                }
                catch
                {
                    // Car destroyed mid-walk; next tick reaps it.
                }
            }

            if (_sweepTimer >= SweepIntervalSeconds)
            {
                _sweepTimer = 0f;

                Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
                for (int i = 0; i < all.Length; i++)
                {
                    ProcessRenderer(all[i]);
                }
            }

            if (!_reported && _flipped.Count > 0)
            {
                _reported = true;
                Main.Log(string.Format(CultureInfo.InvariantCulture,
                    "Instancing: enableInstancing set on {0} materials ({1} failures). Judge by the " +
                    "draw_calls vs batches telemetry columns; if they do not diverge, the shaders " +
                    "lack instancing variants and this feature buys nothing.",
                    _flipped.Count, _failures));
            }
        }

        /// <summary>
        /// CRITICAL: reads ONLY Renderer.sharedMaterials — never .material, whose getter
        /// silently instantiates a per-object copy. Cloning here would not just leak: it
        /// would destroy the material sharing that instancing depends on, converting the
        /// feature into a generator of the exact pathology it exists to fix.
        /// </summary>
        private void ProcessRenderer(Renderer r)
        {
            if (r == null)
            {
                return;
            }

            Material[] mats;
            try
            {
                mats = r.sharedMaterials;
            }
            catch
            {
                return;
            }

            if (mats == null)
            {
                return;
            }

            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || !_seen.Add(m))
                {
                    continue;
                }

                try
                {
                    if (!m.enableInstancing)
                    {
                        m.enableInstancing = true;
                        _flipped.Add(m);
                    }
                }
                catch
                {
                    // Destroyed between the null check and the write, or the write itself
                    // refused; count it so a systematically-failing write is visible.
                    _failures++;
                }
            }
        }

        /// <summary>
        /// Turns the flag back off on every material this feature turned on. Unity
        /// fake-null: a destroyed Material compares equal to null, so dead entries are
        /// skipped rather than dereferenced.
        /// </summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < _flipped.Count; i++)
            {
                Material m = _flipped[i];
                if (m == null)
                {
                    continue;
                }

                try
                {
                    m.enableInstancing = false;
                }
                catch
                {
                    // Nothing useful to do.
                }
            }

            _flipped.Clear();
            _seen.Clear();
            _tickTimer = 0f;
            _sweepTimer = SweepIntervalSeconds;
            _failures = 0;

            // _reported intentionally survives: the summary line is once-per-session
            // orientation, and re-firing it on every toggle cycle would be noise.
        }

        public string[] TelemetryHeaders
        {
            get { return new[] { "materials_instanced", "flip_failures" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                return new[]
                {
                    _flipped.Count.ToString(CultureInfo.InvariantCulture),
                    _failures.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        internal string StatusLine()
        {
            if (_flipped.Count == 0)
            {
                return Enabled ? "scanning…" : "off";
            }

            return _flipped.Count.ToString(CultureInfo.InvariantCulture) + " flipped";
        }
    }
}
