using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Isolates gameplay simulation from local Fog-of-War state and render-frame timing skew:
    /// 1. CMapWatcher.OnSceneAdded runs immediate fog reveal in simulation context instead of
    ///    deferring via DXGame.InvokeOnFinishFrame (which depends on render FPS).
    /// 2. ZXBehaviour's entity watch interval gate (#=z5O$3wVJdMoVTAfFU1A==) replaces local
    ///    FogEnabled / VisibleCells / ExploredCells checks with deterministic ShowFullMap +
    ///    ZombieWatcherCoarseGrid checks so peers never desync aggro cadence even if one peer
    ///    runs an external reveal-fog trainer or differs on fog backbuffer timing.
    /// 3. Zombie.OnUpdate's InfectedDisabledUntilWatched check ignores local FogEnabled=false
    ///    overrides in deterministic mode.
    /// </summary>
    [HarmonyPatch]
    public static class CMapWatcherOnSceneAddedPatch
    {
        private static bool _metaInit;
        private static Func<object> _getMapLayersCurrent;
        private static Func<object, Point> _getLastCellUpdatedFog;
        private static Action<object, Point> _setLastCellUpdatedFog;
        private static Func<object, object> _getCompParams;
        private static Func<object, int> _getWatchRange;
        private static Func<object, bool> _getWatchThroughOpaque;
        private static Func<DXEntity, int> _getRangeBonus;
        private static Func<int> _getExtraBonusWatchRadius;
        private static Action<object, Point, int, bool> _callRevealCellRadius;

        public static MethodBase TargetMethod()
        {
            Type tCw = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_CMapWatcher);
            return tCw != null ? AccessTools.DeclaredMethod(tCw, "OnSceneAdded", Type.EmptyTypes) : null;
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                var asm = GameBridge.GameAssembly;
                if (asm == null) return;
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                Type tCw = asm.GetType(ObfuscatedSymbols.Type_CMapWatcher);
                Type tMapLayers = asm.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
                Type tZxEnt = GameBridge.TypeZXEntity;
                Type tLevelExt = asm.GetType(ObfuscatedSymbols.Type_ZXLevelExtension);
                if (tCw == null || tMapLayers == null || tZxEnt == null || tLevelExt == null) return;

                var fCur = AccessTools.Field(tMapLayers, ObfuscatedSymbols.Field_MapLayers_Current);
                var pLastCell = AccessTools.Property(tCw, "LastCellUpdatedFog");
                var pParams = AccessTools.Property(tCw, "Params");
                var pRangeBonus = AccessTools.Property(tZxEnt, "RangeBonus");
                var mExtCur = AccessTools.Method(tLevelExt, "#=zuartwoo=", Type.EmptyTypes);
                var pExtraRad = AccessTools.Property(tLevelExt, "ExtraBonusWatchRadius");
                var mReveal = AccessTools.Method(tMapLayers, ObfuscatedSymbols.Method_MapLayers_RevealCellRadius,
                    new[] { typeof(Point), typeof(int), typeof(bool) });

                if (fCur == null || pLastCell == null || pParams == null || pRangeBonus == null ||
                    mExtCur == null || pExtraRad == null || mReveal == null)
                    return;

                Type tParams = pParams.PropertyType;
                var pWatchRange = tParams.GetProperty("WatchRange", F);
                var pThruOpaque = tParams.GetProperty("WatchThroughOpaque", F);
                if (pWatchRange == null || pThruOpaque == null) return;

                _getMapLayersCurrent = CompileStaticFieldGet<object>(fCur, "TABCoop_Fog_GetMapLayers");
                _getLastCellUpdatedFog = CompilePropGet<Point>(pLastCell.GetGetMethod(true), tCw, "TABCoop_Fog_GetLastCell");
                _setLastCellUpdatedFog = CompilePropSet<Point>(pLastCell.GetSetMethod(true), tCw, "TABCoop_Fog_SetLastCell");
                _getCompParams = CompilePropGet<object>(pParams.GetGetMethod(true), tCw, "TABCoop_Fog_GetParams");
                _getWatchRange = CompilePropGet<int>(pWatchRange.GetGetMethod(true), tParams, "TABCoop_Fog_GetWatchRange");
                _getWatchThroughOpaque = CompilePropGet<bool>(pThruOpaque.GetGetMethod(true), tParams, "TABCoop_Fog_GetWatchOpaque");
                _getRangeBonus = CompilePropGetFromEntity<int>(pRangeBonus.GetGetMethod(true), tZxEnt, "TABCoop_Fog_GetRangeBonus");

                {
                    var dm = new DynamicMethod("TABCoop_Fog_GetExtraWatchRad", typeof(int), Type.EmptyTypes,
                        typeof(CMapWatcherOnSceneAddedPatch).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Call, mExtCur);
                    var lblNull = il.DefineLabel();
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Brfalse_S, lblNull);
                    il.Emit(OpCodes.Callvirt, pExtraRad.GetGetMethod(true));
                    il.Emit(OpCodes.Ret);
                    il.MarkLabel(lblNull);
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ret);
                    _getExtraBonusWatchRadius = (Func<int>)dm.CreateDelegate(typeof(Func<int>));
                }

                {
                    var dm = new DynamicMethod("TABCoop_Fog_RevealCellRadius", typeof(void),
                        new[] { typeof(object), typeof(Point), typeof(int), typeof(bool) },
                        typeof(CMapWatcherOnSceneAddedPatch).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tMapLayers);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, mReveal);
                    il.Emit(OpCodes.Ret);
                    _callRevealCellRadius = (Action<object, Point, int, bool>)dm.CreateDelegate(typeof(Action<object, Point, int, bool>));
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] CMapWatcherOnSceneAddedPatch meta init failed: {ex.Message}");
            }
        }

        private static Func<T> CompileStaticFieldGet<T>(FieldInfo field, string name)
        {
            var dm = new DynamicMethod(name, typeof(T), Type.EmptyTypes, typeof(CMapWatcherOnSceneAddedPatch).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<T>)dm.CreateDelegate(typeof(Func<T>));
        }

        private static Func<object, T> CompilePropGet<T>(MethodInfo getter, Type ownerType, string name)
        {
            var dm = new DynamicMethod(name, typeof(T), new[] { typeof(object) }, typeof(CMapWatcherOnSceneAddedPatch).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<object, T>)dm.CreateDelegate(typeof(Func<object, T>));
        }

        private static Func<DXEntity, T> CompilePropGetFromEntity<T>(MethodInfo getter, Type ownerType, string name)
        {
            var dm = new DynamicMethod(name, typeof(T), new[] { typeof(DXEntity) }, typeof(CMapWatcherOnSceneAddedPatch).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (Func<DXEntity, T>)dm.CreateDelegate(typeof(Func<DXEntity, T>));
        }

        private static Action<object, T> CompilePropSet<T>(MethodInfo setter, Type ownerType, string name)
        {
            var dm = new DynamicMethod(name, typeof(void), new[] { typeof(object), typeof(T) }, typeof(CMapWatcherOnSceneAddedPatch).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, ownerType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, setter);
            il.Emit(OpCodes.Ret);
            return (Action<object, T>)dm.CreateDelegate(typeof(Action<object, T>));
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel || LockstepClock.InVisualVfxContext || __instance == null)
                return;

            var comp = __instance as DXComponent;
            var ent = comp?.Entity;
            if (ent == null || ent.Disposed) return;

            EnsureMeta();
            if (_callRevealCellRadius == null || _getLastCellUpdatedFog == null || _setLastCellUpdatedFog == null)
                return;

            if (!GameBridge.IsEntityEnabled(ent) || !GameBridge.IsPlayerEntity(ent))
                return;

            Point cell = ent.Cell;
            if (_getLastCellUpdatedFog(__instance) == cell)
                return;

            object mapLayers = _getMapLayersCurrent();
            if (mapLayers == null) return;

            object prms = _getCompParams(__instance);
            if (prms == null) return;

            int range = _getWatchRange(prms) + _getRangeBonus(ent) + _getExtraBonusWatchRadius();
            bool thruOpaque = _getWatchThroughOpaque(prms);
            _callRevealCellRadius(mapLayers, cell, range, thruOpaque);
            _setLastCellUpdatedFog(__instance, cell);
        }
    }

    internal static class DeterministicFogWatchHelpers
    {
        private static bool _init;
        private static MethodInfo _mIsExploredPoint;
        private static MethodInfo _mIsWatchedPoint;
        private static MethodInfo _mIsNearExplored;
        private static Func<object, DXEntity> _getBDataOwner;
        private static Func<object, Point, bool> _origIsExplored;
        private static Func<object, Point, bool> _origIsWatched;
        private static Func<object, Point, int, bool> _origIsNearExplored;
        private static Func<object, bool> _getFogEnabled;
        private static Func<object, int[]> _getExploredCells;

        internal static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            try
            {
                var asm = GameBridge.GameAssembly;
                if (asm == null) return;

                Type tMapLayers = asm.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
                Type tBData = asm.GetType("ZX.Behaviours.BData");
                if (tMapLayers == null || tBData == null) return;

                _mIsExploredPoint = AccessTools.Method(tMapLayers, ObfuscatedSymbols.Method_MapLayers_IsExploredPoint, new[] { typeof(Point) });
                _mIsWatchedPoint = AccessTools.Method(tMapLayers, ObfuscatedSymbols.Method_MapLayers_IsWatchedPoint, new[] { typeof(Point) });
                _mIsNearExplored = AccessTools.Method(tMapLayers, ObfuscatedSymbols.Method_MapLayers_IsNearExplored, new[] { typeof(Point), typeof(int) });

                var mBDataOwner = AccessTools.Method(tBData, ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                if (mBDataOwner != null)
                {
                    var dm = new DynamicMethod("TABCoop_FogWatch_BDataOwner", typeof(DXEntity), new[] { typeof(object) },
                        typeof(DeterministicFogWatchHelpers).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tBData);
                    il.Emit(OpCodes.Callvirt, mBDataOwner);
                    il.Emit(OpCodes.Ret);
                    _getBDataOwner = (Func<object, DXEntity>)dm.CreateDelegate(typeof(Func<object, DXEntity>));
                }

                if (_mIsExploredPoint != null)
                    _origIsExplored = CompileTwoArgBool<Point>(_mIsExploredPoint, tMapLayers, "TABCoop_Orig_IsExplored");
                if (_mIsWatchedPoint != null)
                    _origIsWatched = CompileTwoArgBool<Point>(_mIsWatchedPoint, tMapLayers, "TABCoop_Orig_IsWatched");

                if (_mIsNearExplored != null)
                {
                    var dm = new DynamicMethod("TABCoop_Orig_IsNearExplored", typeof(bool),
                        new[] { typeof(object), typeof(Point), typeof(int) },
                        typeof(DeterministicFogWatchHelpers).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tMapLayers);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Callvirt, _mIsNearExplored);
                    il.Emit(OpCodes.Ret);
                    _origIsNearExplored = (Func<object, Point, int, bool>)dm.CreateDelegate(typeof(Func<object, Point, int, bool>));
                }

                var fFogEnabled = AccessTools.Field(tMapLayers, ObfuscatedSymbols.Field_MapLayers_FogEnabled);
                if (fFogEnabled != null)
                {
                    var dm = new DynamicMethod("TABCoop_FogWatch_GetFog", typeof(bool), new[] { typeof(object) },
                        typeof(DeterministicFogWatchHelpers).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tMapLayers);
                    il.Emit(OpCodes.Ldfld, fFogEnabled);
                    il.Emit(OpCodes.Ret);
                    _getFogEnabled = (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));
                }

                var fExplored = AccessTools.Field(tMapLayers, ObfuscatedSymbols.Field_MapLayers_ExploredCells);
                if (fExplored != null)
                {
                    var dm = new DynamicMethod("TABCoop_FogWatch_GetExplored", typeof(int[]), new[] { typeof(object) },
                        typeof(DeterministicFogWatchHelpers).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, tMapLayers);
                    il.Emit(OpCodes.Ldfld, fExplored);
                    il.Emit(OpCodes.Ret);
                    _getExploredCells = (Func<object, int[]>)dm.CreateDelegate(typeof(Func<object, int[]>));
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] DeterministicFogWatchHelpers init failed: {ex.Message}");
            }
        }

        private static Func<object, T, bool> CompileTwoArgBool<T>(MethodInfo m, Type thisType, string name)
        {
            var dm = new DynamicMethod(name, typeof(bool), new[] { typeof(object), typeof(T) },
                typeof(DeterministicFogWatchHelpers).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, thisType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, m);
            il.Emit(OpCodes.Ret);
            return (Func<object, T, bool>)dm.CreateDelegate(typeof(Func<object, T, bool>));
        }

        internal static MethodInfo MethodIsExploredPoint { get { EnsureInit(); return _mIsExploredPoint; } }
        internal static MethodInfo MethodIsWatchedPoint { get { EnsureInit(); return _mIsWatchedPoint; } }
        internal static MethodInfo MethodIsNearExplored { get { EnsureInit(); return _mIsNearExplored; } }

        public static bool DeterministicIsExploredForWatch(object mapLayers, Point cell, object bData)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return _origIsExplored == null || mapLayers == null || _origIsExplored(mapLayers, cell);

            if (GameBridge.GetLevelStateShowFullMap())
                return true;

            DXEntity owner = _getBDataOwner != null && bData != null ? _getBDataOwner(bData) : null;
            if (owner == null || !GameBridge.IsZombieType(owner.GetType()))
                return true;

            if (!ZombieWatcherCoarseGrid.IsReadyForCurrentTick())
                return true;

            // Keep shrouded zombies at the 5x cadence identically on all peers, and step up to 1x
            // whenever any player unit/building is within coarse watch proximity (~16-24 cells).
            return !ZombieWatcherCoarseGrid.IsAreaEmptyAroundZombie(cell.X, cell.Y, 15.0f);
        }

        public static bool DeterministicIsWatchedForWatch(object mapLayers, Point cell, object bData)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return _origIsWatched == null || mapLayers == null || _origIsWatched(mapLayers, cell);

            return DeterministicIsExploredForWatch(mapLayers, cell, bData);
        }

        public static bool DeterministicIsNearExploredForZombie(object mapLayers, Point cell, int radius)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return _origIsNearExplored == null || mapLayers == null || _origIsNearExplored(mapLayers, cell, radius);

            if (GameBridge.GetLevelStateShowFullMap())
                return true;

            if (mapLayers != null && _getFogEnabled != null && _getFogEnabled(mapLayers))
                return _origIsNearExplored != null ? _origIsNearExplored(mapLayers, cell, radius) : true;

            // If a local trainer/cheat turned off FogEnabled, fall back to deterministic proximity.
            if (ZombieWatcherCoarseGrid.IsReadyForCurrentTick())
                return !ZombieWatcherCoarseGrid.IsAreaEmptyAroundZombie(cell.X, cell.Y, 15.0f);

            int[] explored = mapLayers != null && _getExploredCells != null ? _getExploredCells(mapLayers) : null;
            if (explored == null) return true;
            int nCells = DXWorldGrid.NCells;
            int x = cell.X, y = cell.Y;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int idx = (y + dy) * nCells + x + dx;
                    if (idx >= 0 && idx < explored.Length && explored[idx] != -16777216)
                        return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch]
    public static class DeterministicZombieWatchCadencePatch
    {
        public static MethodBase TargetMethod()
        {
            Type tBeh = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBeh == null) return null;
            MethodInfo factory = AccessTools.Method(tBeh, ObfuscatedSymbols.Method_ZXBehaviour_WatchIntervalGate,
                new[] { typeof(int), typeof(bool) });
            if (factory == null) return null;

            foreach (var ins in PatchProcessor.GetOriginalInstructions(factory))
            {
                if (ins.opcode == OpCodes.Ldftn && ins.operand is MethodInfo mi)
                    return mi;
            }
            return null;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            DeterministicFogWatchHelpers.EnsureInit();
            MethodInfo mExp = DeterministicFogWatchHelpers.MethodIsExploredPoint;
            MethodInfo mWatch = DeterministicFogWatchHelpers.MethodIsWatchedPoint;
            MethodInfo repExp = AccessTools.Method(typeof(DeterministicFogWatchHelpers), nameof(DeterministicFogWatchHelpers.DeterministicIsExploredForWatch));
            MethodInfo repWatch = AccessTools.Method(typeof(DeterministicFogWatchHelpers), nameof(DeterministicFogWatchHelpers.DeterministicIsWatchedForWatch));

            if (mExp == null || mWatch == null || repExp == null || repWatch == null)
                return instructions;

            OpCode loadBData = (__originalMethod == null || !__originalMethod.IsStatic) ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0;
            var list = new List<CodeInstruction>(instructions);
            int replaced = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo target)
                {
                    MethodInfo replacement = null;
                    if (target == mExp) replacement = repExp;
                    else if (target == mWatch) replacement = repWatch;

                    if (replacement != null)
                    {
                        var ld = new CodeInstruction(loadBData);
                        ins.MoveLabelsTo(ld);
                        ins.MoveBlocksTo(ld);
                        list.Insert(i, ld);
                        i++;
                        ins.opcode = OpCodes.Call;
                        ins.operand = replacement;
                        replaced++;
                    }
                }
            }

            if (replaced != 2)
                ModLogger.Warn($"[LockstepClock] DeterministicZombieWatchCadencePatch expected 2 replacements, got {replaced}.");
            else
                ModLogger.Info("[LockstepClock] Transpiled ZXBehaviour watch cadence gate for fog/trainer independence.");

            return list;
        }
    }

    [HarmonyPatch]
    public static class ZombieOnUpdateFogPatch
    {
        public static MethodBase TargetMethod()
        {
            Type tZombie = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_Zombie);
            return tZombie != null ? AccessTools.DeclaredMethod(tZombie, "OnUpdate", Type.EmptyTypes) : null;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            DeterministicFogWatchHelpers.EnsureInit();
            MethodInfo mNear = DeterministicFogWatchHelpers.MethodIsNearExplored;
            MethodInfo repNear = AccessTools.Method(typeof(DeterministicFogWatchHelpers), nameof(DeterministicFogWatchHelpers.DeterministicIsNearExploredForZombie));
            if (mNear == null || repNear == null) return instructions;

            var list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && Equals(ins.operand, mNear))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = repNear;
                    replaced++;
                }
            }
            if (replaced > 0)
                ModLogger.Info($"[LockstepClock] Transpiled Zombie.OnUpdate fog activation check (replaced={replaced}).");
            return list;
        }
    }
}
