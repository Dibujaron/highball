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

        // ClampReduction: a feature may only ever reduce work, never raise it.
        CheckFloat(Decisions.ClampReduction(60f, 200f), 60f, "clamp keeps the lower configured value");
        CheckFloat(Decisions.ClampReduction(300f, 120f), 120f, "clamp keeps the original when configured is higher");
        CheckFloat(Decisions.ClampReduction(80f, 80f), 80f, "clamp is a no-op when equal");
        Check(Decisions.ClampReductionInt(50, 200) == 50, "int clamp keeps the lower configured value");
        Check(Decisions.ClampReductionInt(500, 50) == 50, "int clamp keeps the original when configured is higher");

        // Hysteresis: suppress past the threshold, but do not restore until well inside it,
        // so a car hovering at the boundary cannot thrash thousands of renderer writes.
        Check(Decisions.ShouldSuppressAtDistance(310f, 300f, 50f, false), "suppresses past the threshold");
        Check(!Decisions.ShouldSuppressAtDistance(290f, 300f, 50f, false), "does not suppress inside the threshold");
        Check(Decisions.ShouldSuppressAtDistance(280f, 300f, 50f, true), "stays suppressed inside the band");
        Check(!Decisions.ShouldSuppressAtDistance(240f, 300f, 50f, true), "restores below the band");
        Check(Decisions.ShouldSuppressAtDistance(250f, 300f, 50f, true), "band edge stays suppressed");
        Check(!Decisions.ShouldSuppressAtDistance(300f, 300f, 50f, false), "threshold itself does not suppress");

        // EffectiveHysteresis: the margin must never reach the threshold itself, or
        // ShouldSuppressAtDistance's restore test degenerates to "always suppressed".
        CheckFloat(Decisions.EffectiveHysteresis(300f, 50f), 50f, "hysteresis cap: preferred margin wins when small enough");
        CheckFloat(Decisions.EffectiveHysteresis(50f, 50f), 25f, "hysteresis cap: degenerate threshold==margin caps at half, not 50");
        CheckFloat(Decisions.EffectiveHysteresis(10f, 50f), 5f, "hysteresis cap: tiny threshold caps to half of itself");
        CheckFloat(Decisions.EffectiveHysteresis(0f, 50f), 0f, "hysteresis cap: zero threshold never goes negative");
        CheckFloat(Decisions.EffectiveHysteresis(-10f, 50f), 0f, "hysteresis cap: negative threshold never goes negative");

        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
