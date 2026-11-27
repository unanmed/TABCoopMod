using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.UI;
using ZX;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Intercepts local player gameplay commands when Lockstep / Recording mode is active,
    /// converts them into NetCommand packets, and routes them through the active CommandRouter.
    /// Carefully ignores AI/internal command invocations (e.g., zombie attacks, production queue auto-advance).
    /// </summary>
    public static class CommandInterceptor
    {
        public static bool IsInterceptionEnabled { get; set; }
        public static byte LocalPlayerId { get; set; } = 1;

        /// <summary>
        /// Callback invoked whenever a local player command is intercepted.
        /// In single-player replay verification, this records the command and executes it at the current tick;
        /// in multiplayer Lockstep, this queues the command into the current TurnInputBatch.
        /// </summary>
        public static Action<NetCommand> OnLocalCommandCaptured;

        /// <summary>
        /// Routes a command through the session (or executes it directly when no session is active).
        /// Use this for commands generated outside the Harmony capture points (pause/speed keys).
        /// </summary>
        public static void Dispatch(NetCommand cmd)
        {
            if (cmd == null) return;
            var callback = OnLocalCommandCaptured;
            if (callback != null) callback(cmd);
            else CommandExecutor.Execute(cmd);
        }

        public static void ConfirmLocalPendingBuild(string typeName, Rectangle footprint)
        {
            Patch_ZXCommand_Execute.ConfirmPendingBuild(typeName, footprint);
        }

        public static void ClearPendingLocalBuilds()
        {
            Patch_ZXCommand_Execute.ClearPendingBuilds();
        }

        /// <summary>
        /// Internal flag set while CCommandable.#=zrz2EI1A= (production queue auto-advance) is running,
        /// so auto-popped queued orders are NOT mistaken for new player clicks!
        /// </summary>
        [ThreadStatic]
        internal static bool InQueueAutoAdvance;

        /// <summary>
        /// Internal flag set while ExecuteGroupMoveCommand is running, so the per-unit Travel.#=zfmgzkuw=
        /// loop in ZXSystemGameLevel right-click/left-click is swallowed after GroupMoveOrPatrol is already captured.
        /// </summary>
        [ThreadStatic]
        internal static bool SuppressIndividualMoveExecution;
    }

    // NOTE: ZXCommand, ZXEntity, ZXCommandTarget, CCommandable and CSelectable are cross-assembly
    // internal types. Desktop CLR 4.6 throws TypeAccessException when a patch signature or body
    // references them directly, so all 5 patches use TargetMethod() + object / DXEntity / DXComponent.

    // 1. Track CCommandable.#=zrz2EI1A= (Queue auto-advance) so we don't intercept internal queue transitions
    [HarmonyPatch]
    public static class Patch_CCommandable_AdvanceQueue
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_AdvanceQueue);

        [HarmonyPrefix]
        public static void Prefix() => CommandInterceptor.InQueueAutoAdvance = true;

        [HarmonyPostfix]
        public static void Postfix() => CommandInterceptor.InQueueAutoAdvance = false;
    }

    // 2. Intercept Group Move / Distributed Target Assignment (#=zQp2M56bpPd9jmiNkVA==)
    [HarmonyPatch]
    public static class Patch_LevelSys_GroupMoveSelected
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                GameBridge.TypeZXSystemGameLevel,
                ObfuscatedSymbols.Method_LevelSys_GroupMoveSelected,
                new[] { GameBridge.TypeZXCommand, typeof(PointF) });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0, PointF __1)
        {
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.IsSimulationThreadActive)
                return true;

            ulong[] unitIds = GameBridge.GetSelectedPlayerEntityIds();

            // Nothing movable selected: let vanilla handle the click (e.g. right-clicking with a
            // building selected). Swallowing it here would silently eat legitimate input.
            if (unitIds == null || unitIds.Length == 0)
                return true;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                IsShiftQueued = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false,
                CommandId = GameBridge.GetCommandId(__0),
                ExecutorEntityIds = unitIds,
                TargetPosition = __1
            };

            // Show local ground click indicator and immediate unit confirmation voice at 0ms.
            GameBridge.ShowGroundIndicatorForCommand(__0, __1, 1f);
            DXEntity leadUnit = DXGame.Current?.GetEntityByID(unitIds[0]);
            if (leadUnit != null)
                GameBridge.PlayUnitCommandVoice(leadUnit, GameBridge.IsAttackCommand(__0));

            CommandInterceptor.SuppressIndividualMoveExecution = true;
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 3. Intercept Generic ZXCommand.#=zfmgzkuw=(ZXEntity executor, ZXCommandTarget target)
    [HarmonyPatch]
    public static class Patch_ZXCommand_Execute
    {
        private struct PendingBuildFootprint
        {
            public long GhostId;
            public int Tick;
            public double TimestampMs;
            public Rectangle Area;
            public string TypeName;
            public GameBridge.PendingResourceCost Cost;
        }

        private const double PendingBuildTimeoutMs = 1500.0;
        private const int PendingBuildTimeoutTicks = 45;
        private static readonly List<PendingBuildFootprint> _recentLocalBuilds = new List<PendingBuildFootprint>();
        private static readonly object _pendingBuildsSync = new object();
        private static long _nextGhostId;

        internal static void ConfirmPendingBuild(string typeName, Rectangle footprint)
        {
            long removedGhostId = 0;
            lock (_pendingBuildsSync)
            {
                for (int i = 0; i < _recentLocalBuilds.Count; i++)
                {
                    var entry = _recentLocalBuilds[i];
                    bool areaMatch = !footprint.IsEmpty && !entry.Area.IsEmpty && entry.Area == footprint;
                    bool fallbackMatch = footprint.IsEmpty && entry.Area.IsEmpty
                        && string.Equals(entry.TypeName, typeName, StringComparison.Ordinal);
                    if (areaMatch || fallbackMatch)
                    {
                        removedGhostId = entry.GhostId;
                        _recentLocalBuilds.RemoveAt(i);
                        break;
                    }
                }
            }
            if (removedGhostId > 0)
                AllyOverlayRenderer.RemovePendingBuildGhost(removedGhostId);
        }

        internal static void ClearPendingBuilds()
        {
            lock (_pendingBuildsSync)
            {
                _recentLocalBuilds.Clear();
            }
            AllyOverlayRenderer.ClearPendingBuildGhosts();
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                GameBridge.TypeZXCommand,
                ObfuscatedSymbols.Method_ZXCommand_Execute,
                new[] { GameBridge.TypeZXEntity, GameBridge.TypeZXCommandTarget });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance, object __0, object __1)
        {
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || CommandInterceptor.InQueueAutoAdvance)
                return true;

            // Only intercept commands issued on Player team entities! (Never intercept Zombie AI attacks or neutral entities)
            var executor = __0 as DXEntity;
            if (executor == null || !GameBridge.IsPlayerEntity(executor))
                return true;

            // Ignore unit basic auto-attack commands and internal weapon sub-commands triggered during UpdateGameLogic or UpdatePhysics
            if (LockstepClock.IsSimulationThreadActive || GameBridge.IsWeaponSubCommand(__instance))
                return true;

            if (!TABCoopMod.Sync.FactionOwnershipTracker.CanPlayerControl(CommandInterceptor.LocalPlayerId, executor))
                return false;

            // Swallow all per-unit CurrentCommand follow-up executions right after GroupMoveSelected
            // (#=zQp2M56bpPd9jmiNkVA==), regardless of whether the unit's prior command was Travel,
            // Stop, Hold, or Attack (otherwise Attack-Move from idle re-emits Stop in the same tick).
            if (CommandInterceptor.SuppressIndividualMoveExecution)
                return false;

            bool isShift = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false;
            int cmdId = GameBridge.GetCommandId(__instance);
            GameBridge.UnpackCommandTarget(__1, out ulong targetEntityId, out PointF targetPos, out SizeF targetSize, out PointF targetOrigin);
            int curTick = DXGame.Current?.IAIterationInt ?? 0;

            // Build and the free mayor-reward buildings (EntityBonusCreation) are both entity-creation
            // commands; they must be replayed as PlaceBuilding so the peer builds the same structure.
            if (GameBridge.IsBuildOrBonusCommand(__instance, out bool isBonus))
            {
                Type buildingType = GameBridge.GetBuildingTypeFromCommand(__instance);
                if (buildingType != null)
                {
                    if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                        && TABCoopMod.Sync.WonderCrowdfundManager.TryClassifyEntityType(buildingType, out var fBuildKind)
                        && !TABCoopMod.Sync.WonderCrowdfundManager.CanPlaceBuildVoucher(fBuildKind))
                    {
                        TABCoopMod.UI.MultiFactionPanel.OpenToWonderTab(fBuildKind);
                        return false;
                    }

                    if (!isBonus && !GameBridge.CanExecuteCommandOnEntity(__instance, executor))
                        return false;

                    if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, targetPos, targetSize, targetOrigin))
                        return false;

                    Rectangle footprint = GameBridge.ResolveBuildCellArea(null, targetPos, targetSize, targetOrigin);
                    double nowMs = DXHelper_Environment.Ticks;
                    GameBridge.PendingResourceCost candidateCost = default;
                    if (!isBonus)
                        GameBridge.TryGetCommandResourceCost(__instance, executor, out candidateCost);

                    List<long> expiredGhosts = null;
                    long newGhostId = 0;

                    lock (_pendingBuildsSync)
                    {
                        for (int i = _recentLocalBuilds.Count - 1; i >= 0; i--)
                        {
                            var entry = _recentLocalBuilds[i];
                            bool expiredByTime = nowMs - entry.TimestampMs > PendingBuildTimeoutMs || nowMs < entry.TimestampMs;
                            bool expiredByTick = curTick < entry.Tick || (curTick - entry.Tick > PendingBuildTimeoutTicks);
                            if (expiredByTime || expiredByTick)
                            {
                                if (entry.GhostId > 0)
                                    (expiredGhosts ?? (expiredGhosts = new List<long>())).Add(entry.GhostId);
                                _recentLocalBuilds.RemoveAt(i);
                            }
                        }

                        GameBridge.PendingResourceCost pendingSum = default;
                        for (int i = 0; i < _recentLocalBuilds.Count; i++)
                        {
                            var entry = _recentLocalBuilds[i];
                            if (!footprint.IsEmpty)
                            {
                                if (!entry.Area.IsEmpty && entry.Area.IntersectsWith(footprint))
                                {
                                    if (expiredGhosts != null)
                                        foreach (var gid in expiredGhosts) AllyOverlayRenderer.RemovePendingBuildGhost(gid);
                                    return false;
                                }
                            }
                            else if (entry.Area.IsEmpty && string.Equals(entry.TypeName, buildingType.FullName, StringComparison.Ordinal))
                            {
                                if (expiredGhosts != null)
                                    foreach (var gid in expiredGhosts) AllyOverlayRenderer.RemovePendingBuildGhost(gid);
                                return false;
                            }
                            pendingSum = pendingSum + entry.Cost;
                        }

                        if (!isBonus && !GameBridge.CanAffordWithPending(candidateCost, pendingSum))
                        {
                            if (expiredGhosts != null)
                                foreach (var gid in expiredGhosts) AllyOverlayRenderer.RemovePendingBuildGhost(gid);
                            ModLogger.Info($"[CommandInterceptor] Rejected local build {buildingType.Name}: insufficient resources after pending pre-deduction (pendingGold={pendingSum.Gold}, newGold={candidateCost.Gold}).");
                            return false;
                        }

                        newGhostId = ++_nextGhostId;
                        _recentLocalBuilds.Add(new PendingBuildFootprint
                        {
                            GhostId = newGhostId,
                            Tick = curTick,
                            TimestampMs = nowMs,
                            Area = footprint,
                            TypeName = buildingType.FullName,
                            Cost = isBonus ? default : candidateCost
                        });
                    }

                    if (expiredGhosts != null)
                    {
                        foreach (var gid in expiredGhosts)
                            AllyOverlayRenderer.RemovePendingBuildGhost(gid);
                    }
                    if (!footprint.IsEmpty && newGhostId > 0)
                        AllyOverlayRenderer.SetPendingBuildGhost(newGhostId, footprint, CommandInterceptor.LocalPlayerId);

                    int bonusBuildSlot = 0;
                    if (isBonus)
                        GameBridge.TryGetEntityBonusInfo(__instance, out bonusBuildSlot, out _, out _, out _);

                    var netBuild = new NetCommand
                    {
                        Type = NetCommandType.PlaceBuilding,
                        PlayerId = CommandInterceptor.LocalPlayerId,
                        TargetSimTick = curTick,
                        IsShiftQueued = isShift,
                        CommandId = cmdId,
                        BuildingTypeName = buildingType.FullName,
                        IsBonusBuilding = isBonus,
                        QueueSlotIndex = (byte)Math.Max(0, Math.Min(255, bonusBuildSlot)),
                        ExecutorEntityIds = new[] { executor.ID },
                        TargetEntityId = targetEntityId,
                        TargetPosition = targetPos,
                        TargetSize = targetSize,
                        TargetOrigin = targetOrigin
                    };
                    GameBridge.PlayLocalBuildOrOrderFeedback(buildingType, preferCreationSound: true, fallbackExecutor: executor);
                    CommandInterceptor.OnLocalCommandCaptured?.Invoke(netBuild);
                    return false;
                }
            }

            if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                && TABCoopMod.Sync.WonderCrowdfundManager.TryClassifyTechCommand(__instance, out var fTechKind))
            {
                TABCoopMod.UI.MultiFactionPanel.OpenToWonderTab(fTechKind);
                return false;
            }

            string extraTypeOrSlotTag = string.Empty;
            byte extraSlotIndex = 0;
            bool isBonusUnit = false;
            bool isMercenary = false;
            Type resolvedOrderEntityType = null;
            if (GameBridge.TryGetEntityBonusInfo(__instance, out int bonusUnitSlot, out Type bonusEntityType, out bool bonusIsStructure, out _)
                && !bonusIsStructure)
            {
                // Empty bonus slot (already claimed/exhausted) — ignore safely.
                if (bonusEntityType == null)
                    return false;
                extraTypeOrSlotTag = bonusEntityType.FullName;
                extraSlotIndex = (byte)Math.Max(0, Math.Min(255, bonusUnitSlot));
                isBonusUnit = true;
                resolvedOrderEntityType = bonusEntityType;
            }
            else if (GameBridge.TryGetExtractSlotInfo(__instance, out int extractSlot))
            {
                extraTypeOrSlotTag = "ZX.Commands.ExtractFromUnitHolderSlot";
                extraSlotIndex = (byte)Math.Max(0, Math.Min(255, extractSlot));
            }
            else if (GameBridge.TryGetContractMercenaryInfo(__instance, executor, out int mercSlot, out Type mercEntityType, out _, out int innMercCount))
            {
                // Empty, uninitialized (NSlot<=0), or stale mercenary slot — ignore safely with diagnostics.
                if (mercSlot <= 0 || mercEntityType == null)
                {
                    ModLogger.Info($"[CommandInterceptor] Ignored ContractMercenary slot={mercSlot} on {executor.ID}: innMercCount={innMercCount}, entityType={mercEntityType?.Name ?? "null"}.");
                    return false;
                }
                extraTypeOrSlotTag = $"ZX.Commands.ContractMercenary|{mercEntityType.FullName}";
                extraSlotIndex = (byte)Math.Max(0, Math.Min(255, mercSlot));
                isMercenary = true;
                resolvedOrderEntityType = mercEntityType;
            }

            var netCmd = new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = curTick,
                IsShiftQueued = isShift,
                CommandId = cmdId,
                BuildingTypeName = extraTypeOrSlotTag,
                QueueSlotIndex = extraSlotIndex,
                ExecutorEntityIds = new[] { executor.ID },
                TargetEntityId = targetEntityId,
                TargetPosition = targetPos,
                TargetSize = targetSize,
                TargetOrigin = targetOrigin
            };

            if (targetPos != PointF.Empty)
                GameBridge.ShowGroundIndicatorForCommand(__instance, targetPos, targetEntityId != 0UL ? 0.6f : 1f);

            Type orderEntityType = resolvedOrderEntityType ?? GameBridge.GetBuildingTypeFromCommand(__instance);
            if (orderEntityType != null)
                GameBridge.PlayLocalBuildOrOrderFeedback(orderEntityType, preferCreationSound: isBonusUnit || isMercenary, fallbackExecutor: executor);
            else
                GameBridge.PlayUnitCommandVoice(executor, GameBridge.IsAttackCommand(__instance));

            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // Isolate CBehaviour local ResetMemNodes/ResetState calls during mouse clicks so UI click
    // handlers do not mutate unit AI ahead of the synchronized lockstep turn.
    [HarmonyPatch]
    public static class Patch_CBehaviour_IsolateLocalReset
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type tCBehav = GameBridge.GameAssembly?.GetType("ZX.Components.CBehaviour");
            if (tCBehav == null) yield break;

            var mResetMem = AccessTools.Method(tCBehav, "#=zXeRMQqKCGSFidmsp4w==", new[] { typeof(bool) });
            if (mResetMem != null) yield return mResetMem;

            var mResetState = AccessTools.Method(tCBehav, "#=zhPQK_m4yjl9wB_DGFw==", new[] { typeof(bool) });
            if (mResetState != null) yield return mResetState;
        }

        [HarmonyPrefix]
        public static bool Prefix(bool __0)
        {
            if (__0
                && CommandInterceptor.IsInterceptionEnabled
                && !CommandExecutor.IsReplayingCommand
                && !LockstepClock.IsSimulationThreadActive)
            {
                return false;
            }
            return true;
        }
    }

    // Intercept right-clicks on world entities in OnSceneRightClick to fix vanilla's zXEntity3
    // single-unit capture bug on enemy focus-fire and prevent premature local CCommandable mutation.
    [HarmonyPatch]
    public static class Patch_LevelSys_OnSceneRightClick
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnSceneRightClick);

        [HarmonyPrefix]
        public static bool Prefix(out List<GameBridge.CommandableSnapshot> __state)
        {
            __state = null;
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.IsSimulationThreadActive)
                return true;

            if (GameBridge.GetPendingCommandOnLevelSys() != null)
                return true;

            DXEntity mouseEntity = GameBridge.GetMouseEntityOnLevelSys();
            if (mouseEntity == null || mouseEntity.Disposed)
                return true;

            ulong[] unitIds = GameBridge.GetSelectedPlayerEntityIds();
            if (unitIds == null || unitIds.Length == 0)
                return true;

            DXEntity leadUnit = DXGame.Current?.GetEntityByID(unitIds[0]);
            if (leadUnit != null && GameBridge.IsEnemyOrAttackableCapsule(mouseEntity, leadUnit))
            {
                object attackCmd = GameBridge.GetAttackCommandObject();
                if (attackCmd != null)
                {
                    GameBridge.ShowGroundIndicatorForCommand(attackCmd, mouseEntity.Position, 0.6f);
                    GameBridge.PlayUnitCommandVoice(leadUnit, isAttack: true);
                    var netCmd = new NetCommand
                    {
                        Type = NetCommandType.ExecuteCommand,
                        PlayerId = CommandInterceptor.LocalPlayerId,
                        TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                        IsShiftQueued = DXInputState.Current?.IsKeyPressed(DXKeys.Shift, false) ?? false,
                        CommandId = GameBridge.GetCommandId(attackCmd),
                        ExecutorEntityIds = unitIds,
                        TargetEntityId = mouseEntity.ID,
                        TargetPosition = mouseEntity.Position
                    };
                    CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
                    return false;
                }
            }

            // Friendly CUnitsHolder / CInteractiveObject / PickableItem: let vanilla resolve the exact
            // per-unit command through Patch_ZXCommand_Execute, then restore local CCommandable state.
            __state = GameBridge.SnapshotSelectedCommandables();
            return true;
        }

        [HarmonyFinalizer]
        public static void Finalizer(List<GameBridge.CommandableSnapshot> __state)
        {
            if (__state != null)
                GameBridge.RestoreSelectedCommandables(__state);
        }
    }

    // Reset SuppressIndividualMoveExecution at the end of right-click / left-click command dispatch.
    // NOTE: OMiniMap.OnMouseRightDown is deliberately NOT patched here — its Eazfuscator-generated
    // exception blocks make MonoMod's IL rewriter throw "异常块的错误代码生成" at PatchAll time,
    // which aborts the entire mod initialization. Minimap right-click is instead handled by
    // GameBridge-level reset on the next frame (see SessionLifecyclePatches).
    [HarmonyPatch]
    public static class Patch_LevelSys_ResetMoveSuppression
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var onRightClick = AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnSceneRightClick);
            if (onRightClick != null) yield return onRightClick;

            var onExecPending = AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ExecutePendingCmd);
            if (onExecPending != null) yield return onExecPending;
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            CommandInterceptor.SuppressIndividualMoveExecution = false;
        }
    }

    // Reset per-frame command guards at the very start of every render frame.
    // Some input paths cannot be patched (OMiniMap.OnMouseRightDown throws in MonoMod), so the
    // suppression flag set by GroupMove can otherwise leak into later frames and eat real orders.
    [HarmonyPatch(typeof(DXGame), "StartFrame")]
    public static class Patch_ResetCommandGuardsOnStartFrame
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            if (CommandExecutor.ConsumePendingCommandPanelRefresh()
                && GameBridge.GetPendingCommandOnLevelSys() == null)
            {
                GameBridge.RefreshCommandPanel();
            }
        }
    }

    // 4. Intercept Cancel Current Order (CCommandable.#=zWGiN6nrTv8Dp)
    [HarmonyPatch]
    public static class Patch_CCommandable_CancelCurrent
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_CancelCurrent);

        [HarmonyPrefix]
        public static bool Prefix(DXComponent __instance)
        {
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.IsSimulationThreadActive)
                return true;

            var ent = __instance?.Entity;
            if (ent == null || !GameBridge.IsPlayerEntity(ent))
                return true;
            if (!TABCoopMod.Sync.FactionOwnershipTracker.CanPlayerControl(CommandInterceptor.LocalPlayerId, ent))
                return false;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.CancelCurrentOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                ExecutorEntityIds = new[] { ent.ID }
            };
            GameBridge.PlayLocalBuildOrOrderFeedback(null, preferCreationSound: false, fallbackExecutor: ent);
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 5. Intercept Cancel Production Queue Slot (CCommandable.#=z8g7GC7xLrr8j)
    [HarmonyPatch]
    public static class Patch_CCommandable_CancelQueueSlot
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeCCommandable, ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });

        [HarmonyPrefix]
        public static bool Prefix(DXComponent __instance, int __0)
        {
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;
            if (!CommandInterceptor.IsInterceptionEnabled || CommandExecutor.IsReplayingCommand || LockstepClock.IsSimulationThreadActive)
                return true;

            var ent = __instance?.Entity;
            if (ent == null || !GameBridge.IsPlayerEntity(ent))
                return true;
            if (!TABCoopMod.Sync.FactionOwnershipTracker.CanPlayerControl(CommandInterceptor.LocalPlayerId, ent))
                return false;

            var netCmd = new NetCommand
            {
                Type = NetCommandType.CancelQueuedOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = DXGame.Current?.IAIterationInt ?? 0,
                ExecutorEntityIds = new[] { ent.ID },
                QueueSlotIndex = (byte)__0
            };
            GameBridge.PlayLocalBuildOrOrderFeedback(null, preferCreationSound: false, fallbackExecutor: ent);
            CommandInterceptor.OnLocalCommandCaptured?.Invoke(netCmd);
            return false;
        }
    }

    // 6. Suppress duplicate unit command voice playback during lockstep replay since the local
    // player already receives an immediate 0ms voice confirmation when issuing the order.
    [HarmonyPatch]
    public static class Patch_Audio_SuppressReplayUnitVoice
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type audioType = GameBridge.TypeZXSystemAudio ?? AccessTools.TypeByName(ObfuscatedSymbols.Type_ZXSystemAudio);
            if (audioType == null) yield break;

            foreach (var m in AccessTools.GetDeclaredMethods(audioType))
            {
                if (m == null || m.IsAbstract) continue;
                if (string.Equals(m.Name, ObfuscatedSymbols.Method_Audio_PickWeightedVoice, StringComparison.Ordinal)
                    || string.Equals(m.Name, ObfuscatedSymbols.Method_Audio_PlayUnique, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(MethodBase __originalMethod, object[] __args, ref int __result)
        {
            if (!CommandExecutor.IsReplayingCommand)
                return true;

            string mName = __originalMethod?.Name;
            if (string.Equals(mName, ObfuscatedSymbols.Method_Audio_PickWeightedVoice, StringComparison.Ordinal))
            {
                __result = 0;
                return false;
            }

            if (string.Equals(mName, ObfuscatedSymbols.Method_Audio_PlayUnique, StringComparison.Ordinal)
                && __args != null && __args.Length >= 1 && __args[0] is ZXSoundCategory cat
                && cat == ZXSoundCategory.UnitVoices)
            {
                __result = 0;
                return false;
            }

            return true;
        }
    }

    // 7. Suppress duplicate local Effects SFX when replaying our own 0ms-predicted build/order command.
    [HarmonyPatch]
    public static class Patch_Audio_SuppressReplayLocalBuildSfx
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type audioType = GameBridge.TypeZXSystemAudio ?? AccessTools.TypeByName(ObfuscatedSymbols.Type_ZXSystemAudio);
            if (audioType == null) yield break;

            var mList = AccessTools.Method(audioType, ObfuscatedSymbols.Method_Audio_Play2DSound, new[] { typeof(ZXSoundCategory), typeof(List<string>) });
            if (mList != null) yield return mList;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(object[] __args)
        {
            if (!CommandExecutor.IsReplayingLocalBuild)
                return true;

            if (__args != null && __args.Length >= 1 && __args[0] is ZXSoundCategory cat
                && (cat == ZXSoundCategory.Effects || cat == ZXSoundCategory.UnitVoices))
            {
                return false;
            }
            return true;
        }
    }
}
