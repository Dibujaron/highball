using System;
using Highball;

internal static class Tests
{
    private static int _failed;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
        if (!condition) _failed++;
    }

    private static void CheckFloat(float actual, float expected, string name)
    {
        Check(Math.Abs(actual - expected) < 0.0001f, name + " (got " + actual + ", want " + expected + ")");
    }

    private static int Main()
    {
        // AccumulateCalm: a value at or under threshold accrues time.
        CheckFloat(Decisions.AccumulateCalm(1.0f, 0.2f, 0.5f, 0.25f), 1.25f, "calm accrues below threshold");
        CheckFloat(Decisions.AccumulateCalm(1.0f, 0.5f, 0.5f, 0.25f), 1.25f, "calm accrues at exactly threshold");

        // AccumulateCalm: any excursion above threshold resets the clock to zero.
        CheckFloat(Decisions.AccumulateCalm(9.0f, 0.51f, 0.5f, 0.25f), 0f, "jolt resets calm clock");

        // Solver LOD: needs BOTH distance and sustained calm.
        Check(Decisions.QualifiesForSolverLod(600f, 3f, 500f, 3f), "solver qualifies when far and steady");
        Check(!Decisions.QualifiesForSolverLod(400f, 9f, 500f, 3f), "solver rejects near cars however steady");
        Check(!Decisions.QualifiesForSolverLod(600f, 2.9f, 500f, 3f), "solver rejects not-yet-steady cars");
        Check(!Decisions.QualifiesForSolverLod(500f, 3f, 500f, 3f), "solver distance gate is strictly greater");

        // Sleep: same gates, plus never touch an already-asleep car, plus consist guard.
        Check(Decisions.QualifiesForSleep(600f, 5f, false, false, 500f, 5f), "sleep qualifies when far, parked, awake");
        Check(!Decisions.QualifiesForSleep(600f, 5f, true, false, 500f, 5f), "sleep skips already-asleep cars");
        Check(!Decisions.QualifiesForSleep(600f, 5f, false, true, 500f, 5f), "sleep refuses when consist is moving");
        Check(!Decisions.QualifiesForSleep(400f, 5f, false, false, 500f, 5f), "sleep rejects near cars");
        Check(!Decisions.QualifiesForSleep(600f, 4.9f, false, false, 500f, 5f), "sleep rejects not-yet-stationary cars");

        // Headroom classification against the spec's 10% / 30% thresholds.
        Check(Decisions.ClassifyHeadroom(51, 519) == "none", "9.8% classifies as none");
        Check(Decisions.ClassifyHeadroom(52, 519) == "marginal", "10.0% classifies as marginal");
        Check(Decisions.ClassifyHeadroom(155, 519) == "marginal", "29.9% classifies as marginal");
        Check(Decisions.ClassifyHeadroom(156, 519) == "real", "30.1% classifies as real");
        Check(Decisions.ClassifyHeadroom(0, 0) == "none", "zero tracked does not divide by zero");

        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
