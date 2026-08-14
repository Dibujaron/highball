using Model;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// A rolling-stock car tracked by the LOD system. Carries the facts the Evaluator
    /// computes each pass, plus the feature-specific scratch fields that today's action
    /// logic (in CarRendererFeature) uses to know what it has already done to the car.
    /// </summary>
    internal sealed class TrackedCar
    {
        internal string Id;
        internal Car Car;
        internal Rigidbody Rigidbody;

        /// <summary>Facts computed once per pass by the Evaluator. Shared by every feature.</summary>
        internal CarFacts Facts;

        // --- scratch fields private to CarRendererFeature. Claim arbitration only guarantees
        // that one feature ACTS on a given car in a given pass; it says nothing about these
        // fields, which persist across passes and are read by Release, which every feature is
        // asked to call on every car regardless of who claimed it. A second mutating feature
        // must NOT reuse these — doing so would let it clear CarRendererFeature's flag while
        // CarRendererFeature still lists the car in its own _held list, desyncing that
        // invariant permanently. Keep your own state, keyed by car. ---
        internal Renderer[] Renderers;
        internal UnityEngine.Rendering.ShadowCastingMode[] OriginalShadowModes;
        internal bool ShadowsSuppressed;
    }
}
