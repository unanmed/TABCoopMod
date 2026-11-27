using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Forces the position-affecting DXHelper_Task.For loops inside DXPhysicWorld.UpdateDefaultTime
    /// to run sequentially while the deterministic sandbox is active. Those two loops apply forces and
    /// read back positions from multiple tasks in scheduler-dependent order — the only simulated state
    /// touched by parallelism once IsIADeterministic + IsMultiplayer are on. Patching
    /// UpdateDefaultTime itself is impossible: MonoMod refuses to rewrite its filtered-catch IL
    /// (HarmonyException), so we neutralize the parallelism at its source.
    ///
    /// Everything else (DXScene.UpdateObjects culling, texture baking, level-table loading) keeps its
    /// native parallelism; serializing those made large saves (6666, 33k entities) take minutes.
    /// </summary>
    /// <summary>
    /// Brackets ZX.GameSystems.ZXMapDrawer.#=zl2XxwLg=(DXLevel) so level/map RNG streams are
    /// seeded identically and DXHelper_Task.For runs sequentially during terrain/tree/grass generation.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicMapDrawerPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType("ZX.GameSystems.ZXMapDrawer");
            return type != null ? AccessTools.Method(type, "#=zl2XxwLg=", new[] { typeof(DXLevel) }) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXLevel __0, out bool __state)
        {
            __state = LockstepClock.InMapDrawer;
            LockstepClock.InMapDrawer = true;
            if (LockstepClock.IsDeterministicModeActive)
            {
                if (__0 != null)
                    __0.Random = new DXRandom(LockstepClock.EffectiveLoadSeed);
                LockstepClock.ResetSyncedRandomStreamsForInitLevel();
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            LockstepClock.InMapDrawer = __state;
        }
    }

    [HarmonyPatch]
    public static class DeterministicTaskPatch
    {
        // Serialize both physics loops on the physics thread:
        // - Loop 1 (StartUpdatePhysics) mutates Body.PhysicMass/Velocity/Force/Position via Chipmunk2D
        //   cpBodyActivate, which updates shared cpSpace active/sleeping lists and arbiter islands.
        //   Running Loop 1 across multiple threads causes wakeup-order races and wandering zombie drift.
        // - Loop 2 (OnNewPositionComputed) reads back positions and updates DXWorldGrid.
        private static bool Active => LockstepClock.IsDeterministicModeActive
            && ((LockstepClock.InPhysicsReadback && LockstepClock.IsPhysicsThreadActive())
                || LockstepClock.InMapDrawer);

        [HarmonyPatch]
        public static class ForIndex
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int> __2)
            {
                if (!Active) return true;
                for (int i = __0; i < __1; i++) __2(i);
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRange
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int> __2)
            {
                if (!LockstepClock.IsDeterministicModeActive) return true;

                bool inPhysThread = LockstepClock.InPhysicsStep && LockstepClock.IsPhysicsThreadActive();
                if (!inPhysThread && !LockstepClock.InMapDrawer)
                    return true;

                if (!LockstepClock.InPhysicsStep)
                {
                    __2(__0, __1);
                    return false;
                }

                bool samplePerf = DeterministicSandboxPatches.DiagPerfSamplingActive;
                long t0 = samplePerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (LockstepClock.InPhysicsReadback && LockstepClock.TryRunMergedPhysicsReadbackAndQuantize(__0, __1))
                {
                    if (samplePerf)
                        DeterministicSandboxPatches.DiagPhysFor2Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    return false;
                }

                // Physics Loop 1 (StartUpdatePhysics) invokes cpBodyActivate on Chipmunk2D cpSpace via
                // Body_SetMass/Velocity/Force/Position; execute sequentially on the physics thread to keep
                // island wakeups and body ordering strictly deterministic across Host and Client.
                __2(__0, __1);

                if (samplePerf)
                {
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (LockstepClock.InPhysicsReadback)
                        DeterministicSandboxPatches.DiagPhysFor2Ticks += dt;
                    else
                        DeterministicSandboxPatches.DiagPhysFor1Ticks += dt;
                }
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRangeWithThread
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int, int> __2)
            {
                if (!Active) return true;
                __2(__0, __1, 0);
                return false;
            }
        }
    }
}
