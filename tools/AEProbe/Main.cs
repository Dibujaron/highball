using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace AEProbe
{
    /// <summary>
    /// Throwaway diagnostic mod. Answers one question: how much frame time does
    /// AutoEngineerPlanner.UpdateTargets actually cost with many AI consists running?
    ///
    /// This is not the basis of the eventual mod. Delete it once the data is in.
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;

        // Default to on: this is a diagnostic and a session that silently records
        // nothing is worse than one that records slightly too much.
        public static bool Enabled = true;

        private static Harmony _harmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;

            try
            {
                _harmony = new Harmony(modEntry.Info.Id);
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                // Without the primary patch there is nothing to measure, so fail loudly.
                Log("FAILED to patch UpdateTargets: " + ex);
                return false;
            }

            Probe.TryPatchPathfinding(_harmony);
            Probe.Init();

            Log("Loaded. Recording every " + Probe.FlushIntervalSeconds + "s.");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            Log(value ? "Enabled." : "Disabled.");
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            if (!Enabled || !Application.isPlaying)
            {
                return;
            }

            try
            {
                Probe.Tick(deltaTime);
            }
            catch (Exception ex)
            {
                Log("Tick failed, disabling: " + ex.Message);
                Enabled = false;
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("AE Probe — read-only instrumentation");
            GUILayout.Label(Probe.StatusLine());
        }

        public static void Log(string msg)
        {
            ModEntry?.Logger.Log("[AEProbe] " + msg);
        }
    }
}
