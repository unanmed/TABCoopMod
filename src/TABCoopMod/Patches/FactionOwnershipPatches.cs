using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;
using ZX;

namespace TABCoopMod.Patches
{
    internal static class FactionSpawnContext
    {
        [ThreadStatic]
        public static byte ActiveSpawningOwner;
    }

    // ========================================================================
    // 1. Selection & Pre-Selection Filtering (CSelectable)
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_CSelectable_FilterByOwner
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type t = GameBridge.GameAssembly?.GetType("ZX.Components.CSelectable");
            if (t == null) yield break;

            var mSel = AccessTools.Method(t, ObfuscatedSymbols.Method_CSelectable_SetSelectedInternal, new[] { typeof(bool) });
            if (mSel != null) yield return mSel;

            var mPreSel = AccessTools.Method(t, "#=z$sYd05nFYjxhpJKILQ==", new[] { typeof(bool) });
            if (mPreSel != null) yield return mPreSel;
        }

        [HarmonyPrefix]
        public static bool Prefix(DXComponent __instance, bool __0)
        {
            if (!__0 || !CoopSessionManager.IsMultiFactionActive)
                return true;

            var ent = __instance?.Entity;
            if (ent == null) return true;

            if (!FactionOwnershipTracker.CanLocalPlayerSelect(ent))
                return false;

            return true;
        }
    }

    // ========================================================================
    // 2. Ownership Propagation: Build, Train, Bonus, Upgrade, NeutralJoinable, Dispose
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_Build_PropagateOwner
    {
        public static MethodBase TargetMethod()
        {
            Type tBuild = GameBridge.GameAssembly?.GetType("ZX.Commands.Build");
            if (tBuild == null || GameBridge.TypeZXEntity == null || GameBridge.TypeZXCommandTarget == null)
                return null;
            return AccessTools.Method(
                tBuild,
                ObfuscatedSymbols.Method_Build_PlaceBuilding,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget, typeof(Type), typeof(bool), typeof(bool), typeof(DXEntityTemplate) });
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out byte __state)
        {
            __state = FactionSpawnContext.ActiveSpawningOwner;
            if (!CoopSessionManager.IsMultiFactionActive) return;

            byte owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner == 0 && __0 is DXEntity execEnt)
                owner = FactionOwnershipTracker.ResolveOrAssignOwner(execEnt);
            if (owner != 0)
                FactionSpawnContext.ActiveSpawningOwner = owner;
        }

        [HarmonyPostfix]
        public static void Postfix(DXEntity __result, object __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __result == null)
                return;

            // Prefer authoritative lockstep command PlayerId first so a missing or fallback
            // executor owner can never misattribute a newly built structure to another faction.
            byte owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner == 0) owner = FactionOwnershipTracker.ResolveOrAssignOwner(__0 as DXEntity);
            if (owner != 0)
            {
                FactionOwnershipTracker.SetOwner(__result.ID, owner);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(byte __state)
        {
            FactionSpawnContext.ActiveSpawningOwner = __state;
        }
    }

    [HarmonyPatch]
    public static class Patch_Train_PropagateOwner
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeTrain != null && GameBridge.TypeZXEntity != null
                ? AccessTools.Method(GameBridge.TypeTrain, ObfuscatedSymbols.Method_Command_OnComplete, new[] { GameBridge.TypeZXEntity })
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out byte __state)
        {
            __state = FactionSpawnContext.ActiveSpawningOwner;
            if (!CoopSessionManager.IsMultiFactionActive) return;

            byte owner = FactionOwnershipTracker.GetOwner(__0 as DXEntity);
            if (owner == 0) owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner != 0)
                FactionSpawnContext.ActiveSpawningOwner = owner;
        }

        [HarmonyFinalizer]
        public static void Finalizer(byte __state)
        {
            FactionSpawnContext.ActiveSpawningOwner = __state;
        }
    }

    [HarmonyPatch]
    public static class Patch_EntityBonusCreation_PropagateOwner
    {
        public struct BonusContextState
        {
            public byte PrevOwner;
            public bool RestoredCell;
            public Point? PrevCcCell;
        }

        public static MethodBase TargetMethod()
        {
            Type tBonus = GameBridge.GameAssembly?.GetType("ZX.Commands.EntityBonusCreation");
            if (tBonus == null || GameBridge.TypeZXEntity == null || GameBridge.TypeZXCommandTarget == null)
                return null;
            return AccessTools.Method(
                tBonus,
                ObfuscatedSymbols.Method_Command_ExecuteImpl,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget });
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out BonusContextState __state)
        {
            __state = new BonusContextState
            {
                PrevOwner = FactionSpawnContext.ActiveSpawningOwner,
                RestoredCell = false,
                PrevCcCell = null
            };
            if (!CoopSessionManager.IsMultiFactionActive) return;

            byte owner = FactionOwnershipTracker.GetOwner(__0 as DXEntity);
            if (owner == 0) owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner != 0)
            {
                FactionSpawnContext.ActiveSpawningOwner = owner;
                DXEntity ownerCc = FactionOwnershipTracker.GetPrimaryCommandCenterForPlayer(owner);
                if (ownerCc != null && GameBridge.SetLevelStateCurrentCommandCenterCell(ownerCc.Cell, out Point? prev))
                {
                    __state.RestoredCell = true;
                    __state.PrevCcCell = prev;
                }
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(BonusContextState __state)
        {
            FactionSpawnContext.ActiveSpawningOwner = __state.PrevOwner;
            if (__state.RestoredCell)
            {
                GameBridge.SetLevelStateCurrentCommandCenterCell(__state.PrevCcCell, out _);
            }
        }
    }

    /// <summary>
    /// Scopes ContractMercenary.Inn to the target executor TheInn (fixing Multi-Faction mode where
    /// vanilla FirstOrDefault() always resolved P1's Inn), prevents List[-1] / out-of-range
    /// ArgumentOutOfRangeException on ContractMercenary.EntityTemplate, and propagates faction
    /// ownership to hired mercenary units.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ContractMercenary_InnScope
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type tMerc = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ContractMercenary);
            Type tEnt = GameBridge.TypeZXEntity;
            Type tTarget = GameBridge.TypeZXCommandTarget;
            if (tMerc != null && tEnt != null)
            {
                string[] oneArgMethods =
                {
                    ObfuscatedSymbols.Method_ZXCommand_IsVisible,
                    ObfuscatedSymbols.Method_ZXCommand_CanExecute,
                    ObfuscatedSymbols.Method_ZXCommand_GetTitle,
                    ObfuscatedSymbols.Method_ZXCommand_GetDescription,
                    ObfuscatedSymbols.Method_ZXCommand_GetIcon,
                    ObfuscatedSymbols.Method_ZXCommand_GetCost
                };
                foreach (string name in oneArgMethods)
                {
                    var m = AccessTools.Method(tMerc, name, new[] { tEnt });
                    if (m != null) yield return m;
                }
                if (tTarget != null)
                {
                    var mExec = AccessTools.Method(tMerc, ObfuscatedSymbols.Method_Command_ExecuteImpl, new[] { tEnt, tTarget });
                    if (mExec != null) yield return mExec;
                }
            }

            if (GameBridge.TypeZXCommand != null && tEnt != null)
            {
                var mHelp = AccessTools.Method(GameBridge.TypeZXCommand, ObfuscatedSymbols.Method_ZXCommand_GetHelp, new[] { tEnt });
                if (mHelp != null) yield return mHelp;
            }
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out DXEntity __state)
        {
            __state = GameBridge.ActiveContractMercenaryInn;
            if (__0 is DXEntity ent && GameBridge.IsTheInnEntity(ent))
                GameBridge.ActiveContractMercenaryInn = ent;
        }

        [HarmonyFinalizer]
        public static void Finalizer(DXEntity __state)
        {
            GameBridge.ActiveContractMercenaryInn = __state;
        }
    }

    [HarmonyPatch]
    public static class Patch_ContractMercenary_GetInn
    {
        public static MethodBase TargetMethod()
        {
            Type tMerc = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ContractMercenary);
            return tMerc != null ? AccessTools.PropertyGetter(tMerc, "Inn") : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(ref DXEntity __result)
        {
            DXEntity scoped = GameBridge.ActiveContractMercenaryInn;
            if (scoped != null && !scoped.Disposed)
            {
                __result = scoped;
                return false;
            }

            if (CoopSessionManager.IsMultiFactionActive
                && !LockstepClock.IsSimulationThreadActive
                && !CommandExecutor.IsReplayingCommand)
            {
                DXEntity selectedInn = GameBridge.GetLocalSelectedTheInn();
                if (selectedInn != null)
                {
                    __result = selectedInn;
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_ContractMercenary_SafeEntityTemplate
    {
        public static MethodBase TargetMethod()
        {
            Type tMerc = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ContractMercenary);
            return tMerc != null ? AccessTools.PropertyGetter(tMerc, "EntityTemplate") : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, ref DXEntityTemplate __result)
        {
            if (GameBridge.TryResolveSafeContractMercenaryTemplate(__instance, out DXEntityTemplate safeTemplate))
            {
                __result = safeTemplate;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_ContractMercenary_PropagateOwner
    {
        public static MethodBase TargetMethod()
        {
            Type tMerc = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ContractMercenary);
            if (tMerc == null || GameBridge.TypeZXEntity == null || GameBridge.TypeZXCommandTarget == null)
                return null;
            return AccessTools.Method(
                tMerc,
                ObfuscatedSymbols.Method_Command_ExecuteImpl,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget });
        }

        [HarmonyPrefix]
        public static void Prefix(object __0, out byte __state)
        {
            __state = FactionSpawnContext.ActiveSpawningOwner;
            if (!CoopSessionManager.IsMultiFactionActive) return;

            byte owner = FactionOwnershipTracker.GetOwner(__0 as DXEntity);
            if (owner == 0) owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner != 0)
                FactionSpawnContext.ActiveSpawningOwner = owner;
        }

        [HarmonyFinalizer]
        public static void Finalizer(byte __state)
        {
            FactionSpawnContext.ActiveSpawningOwner = __state;
        }
    }

    [HarmonyPatch(typeof(DXEntity), "InvokeAddToScene", new[] { typeof(DXObject) })]
    public static class Patch_DXEntity_InvokeAddToScene_AssignOwner
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __instance == null)
                return;
            byte owner = FactionSpawnContext.ActiveSpawningOwner;
            if (owner != 0 && GameBridge.IsPlayerEntity(__instance))
            {
                FactionOwnershipTracker.SetOwner(__instance.ID, owner);
            }
            if (GameBridge.IsCommandCenter(__instance))
                GameBridge.InvalidateCommandCenterCache();
        }
    }

    [HarmonyPatch]
    public static class Patch_ZXEntity_TransferUpgradeOwner
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type tEntity = GameBridge.TypeZXEntity;
            if (tEntity == null || GameBridge.GameAssembly == null) yield break;

            var baseMethod = AccessTools.Method(tEntity, ObfuscatedSymbols.Method_ZXEntity_TransferUpgradeState, new[] { tEntity });
            if (baseMethod != null) yield return baseMethod;

            foreach (Type t in GameBridge.GameAssembly.GetTypes())
            {
                if (t == null || t == tEntity || !t.IsSubclassOf(tEntity)) continue;
                var m = t.GetMethod(
                    ObfuscatedSymbols.Method_ZXEntity_TransferUpgradeState,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null,
                    new[] { tEntity },
                    null);
                if (m != null && !m.IsAbstract)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance, DXEntity __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __instance == null || __0 == null)
                return;

            byte owner = FactionOwnershipTracker.GetOwner(__instance.ID);
            if (owner == 0) owner = CommandExecutor.ActiveCommandPlayerId;
            if (owner != 0)
            {
                FactionOwnershipTracker.SetOwner(__0.ID, owner);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_CNeutralJoinable_PropagateOwner
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeCNeutralJoinable != null
                ? AccessTools.Method(GameBridge.TypeCNeutralJoinable, ObfuscatedSymbols.Method_CNeutralJoinable_Update, Type.EmptyTypes)
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance, out byte __state)
        {
            __state = 0;
            if (!CoopSessionManager.IsMultiFactionActive || __instance?.Entity == null)
                return;
            var ent = __instance.Entity;
            if (GameBridge.IsPlayerEntity(ent))
                return;

            // Deterministically find the lowest-ID owned unit touching the neutral entity.
            var nearby = DXWorldGrid.EntitiesAtCellArea(ent.CellArea.Enlarge(1), 32u);
            if (nearby == null || nearby.Count == 0) return;

            ulong bestId = ulong.MaxValue;
            byte bestOwner = 0;
            for (int i = 0; i < nearby.Count; i++)
            {
                var u = nearby[i];
                if (u == null || u.Disposed) continue;
                byte owner = FactionOwnershipTracker.GetOwner(u.ID);
                if (owner != 0 && u.ID < bestId)
                {
                    bestId = u.ID;
                    bestOwner = owner;
                }
            }
            __state = bestOwner;
        }

        [HarmonyPostfix]
        public static void Postfix(DXComponent __instance, byte __state)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __state == 0 || __instance?.Entity == null)
                return;

            var ent = __instance.Entity;
            if (GameBridge.IsPlayerEntity(ent))
            {
                FactionOwnershipTracker.SetOwner(ent.ID, __state);
                ModLogger.Info($"[FactionOwnership] Neutral entity {ent.GetType().Name} (ID={ent.ID}) joined P{__state}.");
            }
        }
    }

    [HarmonyPatch(typeof(DXEntity), "Dispose", new Type[0])]
    public static class Patch_DXEntity_Dispose_CleanupOwner
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance)
        {
            if (__instance == null || __instance.Disposed || LockstepClock.LoadingLevel)
                return;
            if (!CoopSessionManager.IsMultiFactionActive)
                return;

            // DXEntity.~DXEntity() invokes Dispose() without GC.SuppressFinalize or checking Disposed.
            // After a snapshot reload, stale pre-reload DXEntity instances share identical IDs with the
            // live entities; never erase ownership when a different live instance owns this ID.
            var game = DXGame.Current;
            if (game != null)
            {
                var live = game.GetEntityByID(__instance.ID);
                if (live != null && !object.ReferenceEquals(live, __instance))
                    return;
            }

            FactionOwnershipTracker.RemoveEntity(__instance.ID);
            if (GameBridge.IsCommandCenter(__instance))
                GameBridge.InvalidateCommandCenterCache();
        }
    }

    // ========================================================================
    // 3. CommandCenter Death / Exile Handover (ZXLevelState.RecordEntityDead)
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_LevelState_HandleCommandCenterDeath
    {
        public static MethodBase TargetMethod()
        {
            Type lsType = GameBridge.LevelStateType ?? GameBridge.GameAssembly?.GetType("ZX.ZXLevelState");
            return lsType != null
                ? AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_RecordEntityDead, new[] { typeof(DXEntity) })
                : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXEntity __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __0 == null)
                return;
            if (!GameBridge.IsCommandCenter(__0))
                return;

            GameBridge.InvalidateCommandCenterCache();
            FactionOwnershipTracker.HandleCommandCenterDestroyed(__0);
        }
    }

    // ========================================================================
    // 4. Multi-Base Zombie AI Routing & Roaming Exclusion (ZXBehaviour)
    // ========================================================================

    [HarmonyPatch]
    public static class Patch_ZXBehaviour_SetInfectedDestinyMultiBase
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type tBeh = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBeh == null) yield break;

            var f1 = tBeh.GetField(ObfuscatedSymbols.Field_ZXBehaviour_SetInfectedDestiny, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object n1 = f1?.GetValue(null);
            var d1 = n1 != null ? AccessTools.Property(n1.GetType(), "OnExecute")?.GetValue(n1) as Delegate : null;
            if (d1?.Method != null) yield return d1.Method;

            var f2 = tBeh.GetField(ObfuscatedSymbols.Field_ZXBehaviour_SetInfectedDestiny2, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object n2 = f2?.GetValue(null);
            var d2 = n2 != null ? AccessTools.Property(n2.GetType(), "OnExecute")?.GetValue(n2) as Delegate : null;
            if (d2?.Method != null) yield return d2.Method;
        }

        [HarmonyPostfix]
        public static void Postfix(object __0, ref IARes __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __0 == null)
                return;

            DXEntity owner = GameBridge.GetBDataOwner(__0);
            DXEntity targetCc = FactionOwnershipTracker.PickTargetCommandCenterForZombie(owner, splitDistantById: true);
            if (targetCc != null)
            {
                GameBridge.SetBDataTargetPosition(__0, (PointF)targetCc.Cell);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_ZXBehaviour_TargetCommandCenterMultiBase
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type tBeh = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBeh == null) yield break;

            var fTargetCc = tBeh.GetField(ObfuscatedSymbols.Field_ZXBehaviour_TargetCurrentCC, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object nTargetCc = fTargetCc?.GetValue(null);
            var dTargetCc = nTargetCc != null ? AccessTools.Property(nTargetCc.GetType(), "OnExecute")?.GetValue(nTargetCc) as Delegate : null;
            if (dTargetCc?.Method != null) yield return dTargetCc.Method;

            var mFind1 = AccessTools.Method(tBeh, ObfuscatedSymbols.Method_ZXBehaviour_FindBuildingOrCC, new[] { typeof(int) });
            if (mFind1 != null)
            {
                object n1 = mFind1.Invoke(null, new object[] { 10 });
                var d1 = n1 != null ? AccessTools.Property(n1.GetType(), "OnExecute")?.GetValue(n1) as Delegate : null;
                if (d1?.Method != null) yield return d1.Method;
            }

            var mFind2 = AccessTools.Method(tBeh, ObfuscatedSymbols.Method_ZXBehaviour_FindEnabledBldgOrCC, new[] { typeof(int) });
            if (mFind2 != null)
            {
                object n2 = mFind2.Invoke(null, new object[] { 10 });
                var d2 = n2 != null ? AccessTools.Property(n2.GetType(), "OnExecute")?.GetValue(n2) as Delegate : null;
                if (d2?.Method != null) yield return d2.Method;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __0, ref IARes __result)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __0 == null)
                return;

            DXEntity currentTarget = GameBridge.GetBDataTargetEntity(__0);
            if (currentTarget != null && !GameBridge.IsCommandCenter(currentTarget))
                return;

            DXEntity owner = GameBridge.GetBDataOwner(__0);
            DXEntity targetCc = FactionOwnershipTracker.PickTargetCommandCenterForZombie(owner, splitDistantById: true);
            if (targetCc != null)
            {
                GameBridge.SetBDataTargetEntity(__0, targetCc);
                __result = IARes.Success;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_ZXBehaviour_RoamingMultiBaseClamp
    {
        private static PropertyInfo _propCellsAwayCC;
        private static Func<object, int> _fastGetCellsAwayCC;
        private static bool _cellsAwayCompiled;

        private static int GetCellsAwayCCFast(object owner)
        {
            if (owner == null) return 0;
            if (_propCellsAwayCC == null)
                _propCellsAwayCC = owner.GetType().GetProperty("CellsAwayCommandCenterWhenRoaming", BindingFlags.Public | BindingFlags.Instance);
            if (_propCellsAwayCC == null) return 0;

            if (!_cellsAwayCompiled)
            {
                _cellsAwayCompiled = true;
                try
                {
                    var getter = _propCellsAwayCC.GetGetMethod(true);
                    var declType = _propCellsAwayCC.DeclaringType;
                    if (getter != null && declType != null)
                    {
                        var dm = new System.Reflection.Emit.DynamicMethod(
                            "TABCoop_GetCellsAwayCC",
                            typeof(int),
                            new[] { typeof(object) },
                            typeof(Patch_ZXBehaviour_RoamingMultiBaseClamp).Module,
                            skipVisibility: true);
                        var il = dm.GetILGenerator();
                        var lblBad = il.DefineLabel();
                        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                        il.Emit(System.Reflection.Emit.OpCodes.Isinst, declType);
                        il.Emit(System.Reflection.Emit.OpCodes.Dup);
                        il.Emit(System.Reflection.Emit.OpCodes.Brfalse_S, lblBad);
                        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, getter);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        il.MarkLabel(lblBad);
                        il.Emit(System.Reflection.Emit.OpCodes.Pop);
                        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_M1);
                        il.Emit(System.Reflection.Emit.OpCodes.Ret);
                        _fastGetCellsAwayCC = (Func<object, int>)dm.CreateDelegate(typeof(Func<object, int>));
                    }
                }
                catch
                {
                    _fastGetCellsAwayCC = null;
                }
            }

            if (_fastGetCellsAwayCC != null)
            {
                int val = _fastGetCellsAwayCC(owner);
                return val >= 0 ? val : 0;
            }
            if (!_propCellsAwayCC.DeclaringType.IsInstanceOfType(owner))
                return 0;
            return Convert.ToInt32(_propCellsAwayCC.GetValue(owner));
        }

        public static MethodBase TargetMethod()
        {
            Type tBeh = GameBridge.GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBeh == null) return null;

            var fRoam = tBeh.GetField(ObfuscatedSymbols.Field_ZXBehaviour_PickRoamingCell, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object nRoam = fRoam?.GetValue(null);
            var dOpen = nRoam != null ? AccessTools.Property(nRoam.GetType(), "OnOpen")?.GetValue(nRoam) as Delegate : null;
            return dOpen?.Method;
        }

        [HarmonyPostfix]
        public static void Postfix(object __0)
        {
            if (!CoopSessionManager.IsMultiFactionActive || __0 == null)
                return;

            PointF? targetPos = GameBridge.GetBDataTargetPosition(__0);
            if (!targetPos.HasValue) return;

            DXEntity owner = GameBridge.GetBDataOwner(__0);
            if (owner == null) return;

            int minCells = GetCellsAwayCCFast(owner);
            if (minCells <= 0) return;

            var ccs = GameBridge.GetAliveCommandCentersCached();
            if (ccs.Count <= 1) return;

            Point targetCell = Point.Truncate(targetPos.Value);
            for (int i = 0; i < ccs.Count; i++)
            {
                Point ccCell = ccs[i].Cell;
                if (!owner.Cell.InRange(ccCell, minCells) && targetCell.InRange(ccCell, minCells))
                {
                    GameBridge.SetBDataTargetPosition(__0, null);
                    return;
                }
            }
        }
    }
}
