using Model;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// A rolling-stock car tracked by the LOD system. Carries the facts the Evaluator
    /// computes each pass, plus the feature-specific scratch fields that today's action
    /// logic (still in LodManager) uses to know what it has already done to the car.
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

        // --- feature scratch fields, still owned by LodManager until a later task moves
        // action/mutation logic out into features ---
        internal int OriginalSolverIterations;
        internal bool IsDowngraded;
    }
}
