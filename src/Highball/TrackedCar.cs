using Model;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// A rolling-stock car tracked by the LOD system. Carries the facts the Evaluator
    /// computes each pass, plus the feature-specific scratch fields that today's action
    /// logic (in SolverLodFeature) uses to know what it has already done to the car.
    /// </summary>
    internal sealed class TrackedCar
    {
        internal string Id;
        internal Car Car;
        internal Rigidbody Rigidbody;

        /// <summary>Facts computed once per pass by the Evaluator. Shared by every feature.</summary>
        internal CarFacts Facts;

        /// <summary>Name of the feature currently holding this car at reduced fidelity, if any.</summary>
        internal string ClaimedBy;

        // --- feature scratch fields, owned by SolverLodFeature. General rather than
        // feature-private because more than one feature may need to remember an
        // original solver-iteration count if a later feature also touches it. ---
        internal int OriginalSolverIterations;
        internal bool IsDowngraded;
    }
}
