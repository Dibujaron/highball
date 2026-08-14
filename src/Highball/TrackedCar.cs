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

        // --- feature scratch fields, private to SolverLodFeature. Claim arbitration only
        // guarantees that one feature ACTS on a given car in a given pass; it says nothing
        // about these fields, which persist across passes and are read by Release, which
        // every feature is asked to call on every car regardless of who claimed it. A second
        // mutating feature must NOT reuse OriginalSolverIterations or IsDowngraded — doing so
        // would let it clear SolverLodFeature's flag while SolverLodFeature still lists the
        // car in its own _held list, desyncing that invariant permanently. A second mutating
        // feature needs its own state, keyed by car (e.g. its own dictionary or held-set),
        // not these fields. ---
        internal int OriginalSolverIterations;
        internal bool IsDowngraded;
    }
}
