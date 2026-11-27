using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Commands
{
    public enum NetCommandType : byte
    {
        None = 0,
        ExecuteCommand = 1,      // Generic ZXCommand.#=zfmgzkuw= (Attack, Train, Tech, Destroy, Repair, Market, RallyPoint, etc.)
        GroupMoveOrPatrol = 2,   // Group distributed move/patrol/grenade (#=zgn6bDkSd9MGS3$WvAQ==)
        PlaceBuilding = 3,       // Building placement (Build.#=zBTwSMLo=)
        CancelCurrentOrder = 4,  // Cancel active command/construction (CCommandable.#=zWGiN6nrTv8Dp)
        CancelQueuedOrder = 5,   // Cancel production queue slot (CCommandable.#=z8g7GC7xLrr8j)
        SetPaused = 6,           // Pause / Resume toggle
        SetGameSpeed = 7,        // GameSpeed multiplier change
        MayorVoteOrSelect = 8,   // Mayor selection (0 = left male, 1 = right female)
        TransferResource = 9,    // Market-unlocked ally resource transfer (20% fee, 0% with CoopMayor_ZeroTransferFee)
        WonderContribute = 10,     // Functional Wonder 2-phase crowdfunding contribution
        WonderWithdraw = 11,       // Functional Wonder 2-phase crowdfunding withdrawal (while funding phase open)
        TestTriggerFinalSwarm = 12,// Test-harness synced FinalSwarm wave trigger for multiplayer benchmarks
        TestForcePlaceBuilding = 13// Test-harness synced instant building placement bypassing tech/upkeep gates
    }

    /// <summary>
    /// Serializable deterministic player command payload.
    /// Carries explicit ExecutorEntityIds so replay NEVER touches or depends on local CSelectable!
    /// </summary>
    public sealed class NetCommand
    {
        public NetCommandType Type { get; set; }
        public byte PlayerId { get; set; } = 1;
        public int TargetSimTick { get; set; }
        public bool IsShiftQueued { get; set; }

        // Command & Entity Identifiers
        public int CommandId { get; set; }
        public string BuildingTypeName { get; set; } = string.Empty;
        public ulong[] ExecutorEntityIds { get; set; } = Array.Empty<ulong>();

        // ZXCommandTarget Payload
        public ulong TargetEntityId { get; set; }
        public PointF TargetPosition { get; set; }
        public SizeF TargetSize { get; set; }
        public PointF TargetOrigin { get; set; }

        // Building / Queue / State Extras
        public Rectangle BuildingCellArea { get; set; }
        public bool IsBonusBuilding { get; set; }
        public bool InstantBuild { get; set; }
        public byte QueueSlotIndex { get; set; }
        public bool TargetPausedState { get; set; }
        public float GameSpeed { get; set; } = 1f;
        public string MayorId { get; set; } = string.Empty;
        public byte MayorOptionIndex { get; set; }
        public bool IsMayorFinalDecision { get; set; }
        public uint TraceId { get; set; }

        public static bool UseCompactWireFormat { get; set; } = true;

        public void WriteTo(BinaryWriter w)
        {
            if (!UseCompactWireFormat)
            {
                WriteLegacy(w);
                return;
            }

            w.Write((byte)(0x80 | ((byte)Type & 0x7F)));
            w.Write(PlayerId);
            WriteZigZagVarInt32(w, TargetSimTick);

            uint mask = 0;
            if (IsShiftQueued) mask |= 1u << 0;
            if (CommandId != 0) mask |= 1u << 1;
            if (!string.IsNullOrEmpty(BuildingTypeName)) mask |= 1u << 2;
            int idCount = ExecutorEntityIds != null ? ExecutorEntityIds.Length : 0;
            if (idCount > 0) mask |= 1u << 3;
            if (TargetEntityId != 0UL) mask |= 1u << 4;
            if (TargetPosition.X != 0f || TargetPosition.Y != 0f) mask |= 1u << 5;
            if (TargetSize.Width != 0f || TargetSize.Height != 0f) mask |= 1u << 6;
            if (TargetOrigin.X != 0f || TargetOrigin.Y != 0f) mask |= 1u << 7;
            if (BuildingCellArea.X != 0 || BuildingCellArea.Y != 0 || BuildingCellArea.Width != 0 || BuildingCellArea.Height != 0) mask |= 1u << 8;
            if (IsBonusBuilding) mask |= 1u << 9;
            if (InstantBuild) mask |= 1u << 10;
            if (QueueSlotIndex != 0) mask |= 1u << 11;
            if (TargetPausedState) mask |= 1u << 12;
            if (IsMayorFinalDecision) mask |= 1u << 13;
            if (MayorOptionIndex != 0) mask |= 1u << 14;
            if (GameSpeed != 1f) mask |= 1u << 15;
            if (!string.IsNullOrEmpty(MayorId)) mask |= 1u << 16;
            if (TraceId != 0u) mask |= 1u << 17;
            WriteVarUInt32(w, mask);

            if ((mask & (1u << 1)) != 0) WriteZigZagVarInt32(w, CommandId);
            if ((mask & (1u << 2)) != 0) w.Write(BuildingTypeName);
            if ((mask & (1u << 3)) != 0)
            {
                WriteVarUInt32(w, (uint)idCount);
                ulong prev = ExecutorEntityIds[0];
                w.Write(prev);
                for (int i = 1; i < idCount; i++)
                {
                    ulong cur = ExecutorEntityIds[i];
                    long delta = unchecked((long)(cur - prev));
                    WriteZigZagVarInt64(w, delta);
                    prev = cur;
                }
            }
            if ((mask & (1u << 4)) != 0) w.Write(TargetEntityId);
            if ((mask & (1u << 5)) != 0) { w.Write(TargetPosition.X); w.Write(TargetPosition.Y); }
            if ((mask & (1u << 6)) != 0) { w.Write(TargetSize.Width); w.Write(TargetSize.Height); }
            if ((mask & (1u << 7)) != 0) { w.Write(TargetOrigin.X); w.Write(TargetOrigin.Y); }
            if ((mask & (1u << 8)) != 0)
            {
                WriteZigZagVarInt32(w, BuildingCellArea.X);
                WriteZigZagVarInt32(w, BuildingCellArea.Y);
                WriteZigZagVarInt32(w, BuildingCellArea.Width);
                WriteZigZagVarInt32(w, BuildingCellArea.Height);
            }
            if ((mask & (1u << 11)) != 0) w.Write(QueueSlotIndex);
            if ((mask & (1u << 14)) != 0) w.Write(MayorOptionIndex);
            if ((mask & (1u << 15)) != 0) w.Write(GameSpeed);
            if ((mask & (1u << 16)) != 0) w.Write(MayorId);
            if ((mask & (1u << 17)) != 0) WriteVarUInt32(w, TraceId);
        }

        private void WriteLegacy(BinaryWriter w)
        {
            w.Write((byte)Type);
            w.Write(PlayerId);
            w.Write(TargetSimTick);
            w.Write(IsShiftQueued);
            w.Write(CommandId);
            w.Write(BuildingTypeName ?? string.Empty);

            ushort count = (ushort)(ExecutorEntityIds?.Length ?? 0);
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                w.Write(ExecutorEntityIds[i]);
            }

            w.Write(TargetEntityId);
            w.Write(TargetPosition.X);
            w.Write(TargetPosition.Y);
            w.Write(TargetSize.Width);
            w.Write(TargetSize.Height);
            w.Write(TargetOrigin.X);
            w.Write(TargetOrigin.Y);

            w.Write(BuildingCellArea.X);
            w.Write(BuildingCellArea.Y);
            w.Write(BuildingCellArea.Width);
            w.Write(BuildingCellArea.Height);
            w.Write(IsBonusBuilding);
            w.Write(InstantBuild);
            w.Write(QueueSlotIndex);
            w.Write(TargetPausedState);
            w.Write(MayorOptionIndex);
            w.Write(IsMayorFinalDecision);
            w.Write(GameSpeed);
            w.Write(MayorId ?? string.Empty);
            w.Write(TraceId);
        }

        public static NetCommand ReadFrom(BinaryReader r)
        {
            byte first = r.ReadByte();
            if ((first & 0x80) != 0)
                return ReadCompact(r, (NetCommandType)(first & 0x7F));

            var cmd = new NetCommand
            {
                Type = (NetCommandType)first,
                PlayerId = r.ReadByte(),
                TargetSimTick = r.ReadInt32(),
                IsShiftQueued = r.ReadBoolean(),
                CommandId = r.ReadInt32(),
                BuildingTypeName = r.ReadString()
            };

            ushort count = r.ReadUInt16();
            cmd.ExecutorEntityIds = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                cmd.ExecutorEntityIds[i] = r.ReadUInt64();
            }

            cmd.TargetEntityId = r.ReadUInt64();
            cmd.TargetPosition = new PointF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetSize = new SizeF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetOrigin = new PointF(r.ReadSingle(), r.ReadSingle());

            cmd.BuildingCellArea = new Rectangle(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            cmd.IsBonusBuilding = r.ReadBoolean();
            cmd.InstantBuild = r.ReadBoolean();
            cmd.QueueSlotIndex = r.ReadByte();
            cmd.TargetPausedState = r.ReadBoolean();
            cmd.MayorOptionIndex = r.ReadByte();
            cmd.IsMayorFinalDecision = r.ReadBoolean();
            cmd.GameSpeed = r.ReadSingle();
            cmd.MayorId = r.ReadString();
            if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                cmd.TraceId = r.ReadUInt32();

            return cmd;
        }

        private static NetCommand ReadCompact(BinaryReader r, NetCommandType type)
        {
            byte pid = r.ReadByte();
            int targetTick = ReadZigZagVarInt32(r);
            uint mask = ReadVarUInt32(r);

            var cmd = new NetCommand
            {
                Type = type,
                PlayerId = pid,
                TargetSimTick = targetTick,
                IsShiftQueued = (mask & (1u << 0)) != 0,
                CommandId = (mask & (1u << 1)) != 0 ? ReadZigZagVarInt32(r) : 0,
                BuildingTypeName = (mask & (1u << 2)) != 0 ? r.ReadString() : string.Empty
            };

            if ((mask & (1u << 3)) != 0)
            {
                uint count = ReadVarUInt32(r);
                cmd.ExecutorEntityIds = new ulong[count];
                if (count > 0)
                {
                    ulong prev = r.ReadUInt64();
                    cmd.ExecutorEntityIds[0] = prev;
                    for (uint i = 1; i < count; i++)
                    {
                        long delta = ReadZigZagVarInt64(r);
                        prev = unchecked(prev + (ulong)delta);
                        cmd.ExecutorEntityIds[i] = prev;
                    }
                }
            }
            if ((mask & (1u << 4)) != 0) cmd.TargetEntityId = r.ReadUInt64();
            if ((mask & (1u << 5)) != 0) cmd.TargetPosition = new PointF(r.ReadSingle(), r.ReadSingle());
            if ((mask & (1u << 6)) != 0) cmd.TargetSize = new SizeF(r.ReadSingle(), r.ReadSingle());
            if ((mask & (1u << 7)) != 0) cmd.TargetOrigin = new PointF(r.ReadSingle(), r.ReadSingle());
            if ((mask & (1u << 8)) != 0)
            {
                cmd.BuildingCellArea = new Rectangle(
                    ReadZigZagVarInt32(r),
                    ReadZigZagVarInt32(r),
                    ReadZigZagVarInt32(r),
                    ReadZigZagVarInt32(r));
            }
            cmd.IsBonusBuilding = (mask & (1u << 9)) != 0;
            cmd.InstantBuild = (mask & (1u << 10)) != 0;
            if ((mask & (1u << 11)) != 0) cmd.QueueSlotIndex = r.ReadByte();
            cmd.TargetPausedState = (mask & (1u << 12)) != 0;
            cmd.IsMayorFinalDecision = (mask & (1u << 13)) != 0;
            if ((mask & (1u << 14)) != 0) cmd.MayorOptionIndex = r.ReadByte();
            if ((mask & (1u << 15)) != 0) cmd.GameSpeed = r.ReadSingle();
            if ((mask & (1u << 16)) != 0) cmd.MayorId = r.ReadString();
            if ((mask & (1u << 17)) != 0) cmd.TraceId = ReadVarUInt32(r);
            return cmd;
        }

        internal static void WriteVarUInt32(BinaryWriter w, uint v)
        {
            while (v >= 0x80u)
            {
                w.Write((byte)((v & 0x7Fu) | 0x80u));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        internal static uint ReadVarUInt32(BinaryReader r)
        {
            uint v = 0;
            int shift = 0;
            while (shift < 35)
            {
                byte b = r.ReadByte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }

        internal static void WriteZigZagVarInt32(BinaryWriter w, int v)
        {
            uint zz = unchecked((uint)((v << 1) ^ (v >> 31)));
            WriteVarUInt32(w, zz);
        }

        internal static int ReadZigZagVarInt32(BinaryReader r)
        {
            uint zz = ReadVarUInt32(r);
            return unchecked((int)((zz >> 1) ^ (uint)-(int)(zz & 1u)));
        }

        internal static void WriteVarUInt64(BinaryWriter w, ulong v)
        {
            while (v >= 0x80UL)
            {
                w.Write((byte)((v & 0x7FUL) | 0x80UL));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        internal static ulong ReadVarUInt64(BinaryReader r)
        {
            ulong v = 0;
            int shift = 0;
            while (shift < 70)
            {
                byte b = r.ReadByte();
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }

        internal static void WriteZigZagVarInt64(BinaryWriter w, long v)
        {
            ulong zz = unchecked((ulong)((v << 1) ^ (v >> 63)));
            WriteVarUInt64(w, zz);
        }

        internal static long ReadZigZagVarInt64(BinaryReader r)
        {
            ulong zz = ReadVarUInt64(r);
            return unchecked((long)((zz >> 1) ^ (ulong)-(long)(zz & 1UL)));
        }

        public override string ToString()
        {
            return $"[NetCmd P{PlayerId} T={TargetSimTick} {Type} CmdId={CommandId} Entities={ExecutorEntityIds?.Length ?? 0} Pos=({TargetPosition.X:F1},{TargetPosition.Y:F1})]";
        }
    }

    /// <summary>
    /// Executes NetCommands deterministically on the local game state without modifying local player's CSelectable.
    /// </summary>
    public static class CommandExecutor
    {
        /// <summary>
        /// Reentrancy guard: when true, CommandInterceptorPatch lets original game methods run without re-capturing.
        /// </summary>
        [ThreadStatic]
        public static bool IsReplayingCommand;

        /// <summary>
        /// True while replaying a command originally issued by the local player in a live co-op session,
        /// allowing 0ms-predicted build/order SFX to suppress duplicate replay Effects audio.
        /// </summary>
        [ThreadStatic]
        public static bool IsReplayingLocalBuild;

        [ThreadStatic]
        public static byte ActiveCommandPlayerId;

        private static volatile bool _pendingCommandPanelRefresh;

        internal static bool ConsumePendingCommandPanelRefresh()
        {
            if (!_pendingCommandPanelRefresh) return false;
            _pendingCommandPanelRefresh = false;
            return true;
        }

        private const double RejectLogCooldownMs = 3000.0;
        private static string _lastRejectKey;
        private static double _lastRejectLogMs = -10000.0;
        private static int _suppressedRejectCount;

        private static void LogRejected(string key, string message)
        {
            if (ModLogger.IsVerbose)
            {
                ModLogger.Info(message);
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (string.Equals(_lastRejectKey, key, StringComparison.Ordinal)
                && now >= _lastRejectLogMs
                && (now - _lastRejectLogMs) < RejectLogCooldownMs)
            {
                _suppressedRejectCount++;
                return;
            }

            string suffix = _suppressedRejectCount > 0 ? $" (+{_suppressedRejectCount} similar suppressed)" : string.Empty;
            _lastRejectKey = key;
            _lastRejectLogMs = now;
            _suppressedRejectCount = 0;
            ModLogger.Info(message + suffix);
        }

        public static void Execute(NetCommand netCmd)
        {
            if (netCmd == null || netCmd.Type == NetCommandType.None)
                return;

            bool prevReplaying = IsReplayingCommand;
            bool prevLocalBuild = IsReplayingLocalBuild;
            bool prevDet = LockstepClock.InDeterministicContext;
            byte prevPlayerId = ActiveCommandPlayerId;
            bool swappedLevelSysCc = false;
            DXEntity prevLevelSysCc = null;
            bool pushedRngScope = false;
            object suspendedBuildCursor = GameBridge.SuspendLocalBuildCursorEnergyPreview();
            IsReplayingCommand = true;
            ActiveCommandPlayerId = netCmd.PlayerId;
            IsReplayingLocalBuild = TABCoopMod.Sync.CoopSessionManager.IsSessionActive
                && netCmd.PlayerId == TABCoopMod.Patches.CommandInterceptor.LocalPlayerId
                && (netCmd.Type == NetCommandType.PlaceBuilding
                    || netCmd.Type == NetCommandType.ExecuteCommand
                    || netCmd.Type == NetCommandType.CancelCurrentOrder
                    || netCmd.Type == NetCommandType.CancelQueuedOrder);
            LockstepClock.InDeterministicContext = true;
            if (LockstepClock.IsDeterministicModeActive)
            {
                ulong cmdScopeId = netCmd.ExecutorEntityIds != null && netCmd.ExecutorEntityIds.Length > 0 && netCmd.ExecutorEntityIds[0] != 0UL
                    ? netCmd.ExecutorEntityIds[0]
                    : (0x434D440000000000UL | ((ulong)unchecked((uint)netCmd.CommandId) << 8) | netCmd.PlayerId);
                LockstepClock.PushRngScope(cmdScopeId, LockstepClock.RngDomainTag.CommandReplay);
                pushedRngScope = true;
            }

            if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive && netCmd.PlayerId > 0)
            {
                var ownerCc = TABCoopMod.Sync.FactionOwnershipTracker.GetPrimaryCommandCenterForPlayer(netCmd.PlayerId);
                if (ownerCc != null)
                {
                    prevLevelSysCc = GameBridge.GetLevelSysCurrentCommandCenter();
                    GameBridge.SetLevelSysCurrentCommandCenter(ownerCc);
                    swappedLevelSysCc = true;
                }
            }

            // Save local Shift key state so we can simulate the sender's Shift modifier accurately
            bool hadShiftKey = false;
            var inputKeys = DXInputState.Current?.Keys;
            if (inputKeys != null)
            {
                hadShiftKey = inputKeys.Contains(DXKeys.Shift);
                if (netCmd.IsShiftQueued && !hadShiftKey)
                    inputKeys.Add(DXKeys.Shift);
                else if (!netCmd.IsShiftQueued && hadShiftKey)
                    inputKeys.Remove(DXKeys.Shift);
            }

            bool swappedContext = false;
            if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                && TABCoopMod.Sync.FactionContextManager.IsInitialized
                && netCmd.PlayerId >= 1 && netCmd.PlayerId <= 3)
            {
                TABCoopMod.Sync.FactionContextManager.PushContext(netCmd.PlayerId);
                swappedContext = true;
            }

            try
            {
                switch (netCmd.Type)
                {
                    case NetCommandType.ExecuteCommand:
                        ExecuteSingleOrBatchCommand(netCmd);
                        if (swappedContext) TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
                        break;

                    case NetCommandType.GroupMoveOrPatrol:
                        ExecuteGroupMoveCommand(netCmd);
                        break;

                    case NetCommandType.PlaceBuilding:
                        ExecutePlaceBuilding(netCmd);
                        if (swappedContext) TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
                        break;

                    case NetCommandType.CancelCurrentOrder:
                        ExecuteCancelCurrentOrder(netCmd);
                        if (swappedContext) TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
                        break;

                    case NetCommandType.CancelQueuedOrder:
                        ExecuteCancelQueuedOrder(netCmd);
                        if (swappedContext) TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
                        break;

                    case NetCommandType.SetPaused:
                        if (DXGame.Current != null)
                        {
                            // Drive the fixed-step clock's freeze off this session flag, not the
                            // engine's DXGame.Paused (which the save loader leaves set).
                            TABCoopMod.Determinism.LockstepClock.SetSessionPause(netCmd.TargetPausedState);
                            DXGame.Current.Paused = netCmd.TargetPausedState;
                            if (!TABCoopMod.Sync.PauseSyncController.CountdownActive)
                            {
                                // durationMs=0 avoids stalled fade-in clips while Paused is true.
                                ShowBannerOutsideDet(
                                    netCmd.TargetPausedState ? $"Player {netCmd.PlayerId} Paused Game" : $"Player {netCmd.PlayerId} Resumed Game",
                                    Color.Gold, netCmd.TargetPausedState ? 0 : 1500);
                            }
                        }
                        break;

                    case NetCommandType.SetGameSpeed:
                        if (DXGame.Current != null)
                        {
                            DXGame.Current.GameSpeed = Math.Max(0.0, Math.Min(20.0, (double)netCmd.GameSpeed));
                            ShowBannerOutsideDet($"Player {netCmd.PlayerId} set speed {DXGame.Current.GameSpeed:0.##}x", Color.Gold, 1200);
                        }
                        break;

                    case NetCommandType.MayorVoteOrSelect:
                        if (!string.IsNullOrEmpty(netCmd.MayorId) && GameBridge.ApplyMayor(netCmd.MayorId))
                        {
                            if (netCmd.MayorId == TABCoopMod.Sync.FactionContextManager.CoopMayorZeroFeeId && TABCoopMod.Sync.FactionContextManager.IsInitialized)
                                TABCoopMod.Sync.FactionContextManager.ZeroTransferFeeActive = true;

                            string mayorName = GameBridge.GetMayorNameById(netCmd.MayorId);
                            ShowBannerOutsideDet(
                                string.IsNullOrEmpty(mayorName)
                                    ? $"Player {netCmd.PlayerId} selected a mayor"
                                    : $"Player {netCmd.PlayerId} selected mayor: {mayorName}",
                                Color.Gold, 2500);
                        }
                        break;

                    case NetCommandType.TransferResource:
                        if (TABCoopMod.Sync.FactionContextManager.IsInitialized)
                        {
                            var resKind = (TABCoopMod.Sync.ResourceTransferKind)netCmd.QueueSlotIndex;
                            if (TABCoopMod.Sync.FactionContextManager.ExecuteTransferResource(
                                netCmd.PlayerId, netCmd.MayorOptionIndex, resKind, netCmd.CommandId, out int received))
                            {
                                int feePct = TABCoopMod.Sync.FactionContextManager.ZeroTransferFeeActive ? 0 : 20;
                                ShowBannerOutsideDet(
                                    $"P{netCmd.PlayerId} sent {netCmd.CommandId} {resKind} -> P{netCmd.MayorOptionIndex} (+{received}, fee {feePct}%)",
                                    Color.LightSkyBlue, 2200);
                            }
                        }
                        break;

                    case NetCommandType.WonderContribute:
                        if (TABCoopMod.Sync.WonderCrowdfundManager.IsInitialized)
                        {
                            var wKind = (TABCoopMod.Sync.FunctionalWonderType)netCmd.MayorOptionIndex;
                            var resKind = (TABCoopMod.Sync.ResourceTransferKind)netCmd.QueueSlotIndex;
                            var prevPhase = TABCoopMod.Sync.WonderCrowdfundManager.GetState(wKind)?.Phase;
                            int actual = TABCoopMod.Sync.WonderCrowdfundManager.TryContribute(netCmd.PlayerId, wKind, resKind, netCmd.CommandId);
                            var newPhase = TABCoopMod.Sync.WonderCrowdfundManager.GetState(wKind)?.Phase;
                            if (actual > 0 && prevPhase != newPhase)
                            {
                                string wName = TABCoopMod.Sync.WonderCrowdfundManager.GetDisplayName(wKind);
                                string msg;
                                if (newPhase == TABCoopMod.Sync.WonderPhase.Researching)
                                {
                                    int secs = Math.Max(1, TABCoopMod.Sync.WonderCrowdfundManager.GetResearchDurationTicks() / 30);
                                    msg = $"{wName}: Research funds complete! Researching ({secs}s)...";
                                }
                                else if (newPhase == TABCoopMod.Sync.WonderPhase.BuildFunding)
                                {
                                    msg = $"{wName}: Shared Research Complete! Build crowdfunding open.";
                                }
                                else
                                {
                                    msg = $"{wName}: Build Crowdfunding Complete! Ready to place (0 cost).";
                                }
                                ShowBannerOutsideDet(msg, Color.Gold, 3500);
                            }
                        }
                        break;

                    case NetCommandType.WonderWithdraw:
                        if (TABCoopMod.Sync.WonderCrowdfundManager.IsInitialized)
                        {
                            var wKind = (TABCoopMod.Sync.FunctionalWonderType)netCmd.MayorOptionIndex;
                            var resKind = (TABCoopMod.Sync.ResourceTransferKind)netCmd.QueueSlotIndex;
                            TABCoopMod.Sync.WonderCrowdfundManager.TryWithdraw(netCmd.PlayerId, wKind, resKind, netCmd.CommandId);
                        }
                        break;

                    case NetCommandType.TestTriggerFinalSwarm:
                        GameBridge.TriggerFinalSwarmEvents(out _, out _);
                        break;

                    case NetCommandType.TestForcePlaceBuilding:
                        ExecuteForcePlaceBuilding(netCmd);
                        if (swappedContext) TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CommandExecutor] Failed to execute {netCmd}", ex);
            }
            finally
            {
                if (swappedContext)
                    TABCoopMod.Sync.FactionContextManager.PopContext();

                // Restore local Shift key state
                if (inputKeys != null)
                {
                    if (hadShiftKey && !inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Add(DXKeys.Shift);
                    else if (!hadShiftKey && inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Remove(DXKeys.Shift);
                }

                if (swappedLevelSysCc)
                    GameBridge.SetLevelSysCurrentCommandCenter(prevLevelSysCc);

                if (pushedRngScope)
                    LockstepClock.PopRngScope();

                ActiveCommandPlayerId = prevPlayerId;
                LockstepClock.InDeterministicContext = prevDet;
                IsReplayingLocalBuild = prevLocalBuild;
                IsReplayingCommand = prevReplaying;
                GameBridge.RestoreLocalBuildCursorEnergyPreview(suspendedBuildCursor);
            }
        }

        private static void ExecuteSingleOrBatchCommand(NetCommand netCmd)
        {
            bool slotSensitive = !string.IsNullOrEmpty(netCmd.BuildingTypeName);
            object sharedCmd = slotSensitive ? null : GameBridge.GetCommandById(netCmd.CommandId);
            if (!slotSensitive && sharedCmd == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown CommandId: {netCmd.CommandId}");
                return;
            }

            object target = BuildCommandTarget(netCmd);

            foreach (ulong entityId in netCmd.ExecutorEntityIds)
            {
                object executor = DXGame.Current?.GetEntityByID(entityId);
                if (!IsLiveExecutableEntityForPlayer(executor, netCmd.PlayerId))
                    continue;

                object cmd = slotSensitive
                    ? GameBridge.ResolveReplayCommand(netCmd.CommandId, netCmd.BuildingTypeName, netCmd.QueueSlotIndex, executor, out _)
                    : sharedCmd;
                if (cmd == null)
                {
                    LogRejected($"slot:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected stale/unavailable slot command {netCmd.BuildingTypeName} (slot={netCmd.QueueSlotIndex}) on {entityId}.");
                    continue;
                }

                if (GameBridge.IsResourceSpendingCommand(cmd, executor) && !GameBridge.CanExecuteCommandOnEntity(cmd, executor))
                {
                    string cmdName = cmd.GetType().Name;
                    string innDiag = GameBridge.IsTheInnEntity(executor)
                        ? $" (slot={netCmd.QueueSlotIndex}, innMercCount={GameBridge.GetTheInnMercenaryCount(executor)})"
                        : string.Empty;
                    string rejDiag = GameBridge.DescribeCommandRejection(cmd, executor);
                    LogRejected($"exec:{cmdName}", $"[CommandExecutor] Rejected {cmdName} on {entityId}{innDiag}{rejDiag}: CanExecute=false.");
                    continue;
                }

                GameBridge.ExecuteCommandOnEntity(cmd, executor, target);
                GameBridge.NotifyUnitCommandExecuted(executor, cmd);
                if (slotSensitive)
                {
                    GameBridge.FlushStartFramePostMethods();
                    if (GameBridge.IsTheInnEntity(executor))
                        _pendingCommandPanelRefresh = true;
                }
            }
        }

        private static void ExecuteGroupMoveCommand(NetCommand netCmd)
        {
            object cmd = GameBridge.GetCommandById(netCmd.CommandId);
            if (cmd == null) return;

            Type movable = GameBridge.TypeCMovable;
            Type commandable = GameBridge.TypeCCommandable;

            var entities = new List<DXEntity>(netCmd.ExecutorEntityIds.Length);
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var ent = DXGame.Current?.GetEntityByID(id);
                if (!IsLiveExecutableEntityForPlayer(ent, netCmd.PlayerId)) continue;
                if (!GameBridge.HasComponent(ent, movable) || !GameBridge.HasComponent(ent, commandable)) continue;
                entities.Add(ent);
            }

            if (entities.Count == 0) return;

            // Make a copy because #=zgn6bDkSd9MGS3$WvAQ== mutates the passed list via Remove()!
            var entitiesForExecution = new List<DXEntity>(entities);

            // 1. Assign distributed target positions to each unit's CCommandable.Target
            GameBridge.AssignGroupMoveTargets(entities, cmd, netCmd.TargetPosition);

            // Two-Phase deterministic A* pre-warm for bulk unit group moves/attack-moves.
            if (entitiesForExecution.Count >= 4)
            {
                var batch = new List<DeterministicPathfindingPatch.PathPrewarmItem>(entitiesForExecution.Count);
                for (int i = 0; i < entitiesForExecution.Count; i++)
                {
                    var ent = entitiesForExecution[i];
                    object cmdComp = GameBridge.GetCommandableObject(ent);
                    object unitTarget = cmdComp != null ? GameBridge.GetCommandableTargetObject(cmdComp) : null;
                    if (unitTarget == null) continue;
                    PointF destPos = GameBridge.GetCommandTargetPosition(unitTarget);
                    if (float.IsNaN(destPos.X) || float.IsNaN(destPos.Y)) continue;
                    DXComponent mov = GameBridge.FindComponent(ent, movable);
                    int cellSize = mov != null ? GameBridge.GetMovableCellSize(mov) : 1;
                    uint mask = GameBridge.GetEntityPathfindingMask(ent);
                    batch.Add(new DeterministicPathfindingPatch.PathPrewarmItem
                    {
                        CellSize = cellSize,
                        Start = ent.Cell,
                        End = Point.Truncate(destPos),
                        Mask = mask
                    });
                }
                DeterministicPathfindingPatch.PrewarmUniquePathsParallel(batch);
            }

            // 2. Trigger command execution on each unit using its assigned CCommandable.Target
            foreach (var entity in entitiesForExecution)
            {
                object cmdComp = GameBridge.GetCommandableObject(entity);
                if (cmdComp == null) continue;

                object unitTarget = GameBridge.GetCommandableTargetObject(cmdComp);
                if (unitTarget == null) continue;

                GameBridge.ExecuteCommandOnEntity(cmd, entity, unitTarget);
                GameBridge.NotifyUnitCommandExecuted(entity, cmd);
            }
        }

        private static void ExecutePlaceBuilding(NetCommand netCmd)
        {
            if (netCmd.PlayerId == CommandInterceptor.LocalPlayerId)
            {
                Rectangle fp = GameBridge.ResolveBuildCellArea(null, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin);
                CommandInterceptor.ConfirmLocalPendingBuild(netCmd.BuildingTypeName, fp);
            }

            if (string.IsNullOrEmpty(netCmd.BuildingTypeName)) return;

            Type buildingType = GameBridge.GameAssembly.GetType(netCmd.BuildingTypeName);
            if (buildingType == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown BuildingTypeName: {netCmd.BuildingTypeName}");
                return;
            }

            ulong builderId = netCmd.ExecutorEntityIds.Length > 0 ? netCmd.ExecutorEntityIds[0] : 0UL;
            object builder = builderId != 0UL ? DXGame.Current?.GetEntityByID(builderId) : null;
            if (!IsLiveExecutableEntityForPlayer(builder, netCmd.PlayerId))
            {
                builder = TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                    ? TABCoopMod.Sync.FactionOwnershipTracker.GetPrimaryCommandCenterForPlayer(netCmd.PlayerId)
                    : GameBridge.GetCommandCenterEntity();
            }

            if (!IsLiveExecutableEntityForPlayer(builder, netCmd.PlayerId)) return;

            // Mayor-reward buildings come from EntityBonusCreation, which internally calls
            // Build.PlaceBuilding(isBonus:true) AND consumes one entry from BonusEntityTemplates.
            if (netCmd.IsBonusBuilding)
            {
                int prevBonusCount = GameBridge.GetBonusEntityCount(buildingType, out _);
                if (prevBonusCount <= 0)
                {
                    LogRejected($"bonus0:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected bonus PlaceBuilding {netCmd.BuildingTypeName}: no remaining bonus charge.");
                    return;
                }
                if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin))
                {
                    LogRejected($"bonusCell:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected bonus PlaceBuilding {netCmd.BuildingTypeName}: target cells occupied or invalid.");
                    return;
                }
                object bonusTarget = BuildCommandTarget(netCmd);
                object placed = GameBridge.PlaceBuilding(builder, bonusTarget, buildingType, isBonus: true, instant: netCmd.InstantBuild, template: null);
                GameBridge.FlushStartFramePostMethods();
                ModLogger.Info($"[CommandExecutor] Bonus PlaceBuilding {netCmd.BuildingTypeName} at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}) -> {(placed != null ? "OK" : "NULL")}");
                GameBridge.ConsumeBonusEntityByType(buildingType);
                if (prevBonusCount == 1 && netCmd.PlayerId == CommandInterceptor.LocalPlayerId)
                    GameBridge.CancelPendingBonusCommandIfType(buildingType);
                return;
            }

            if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                && TABCoopMod.Sync.FactionContextManager.IsInitialized)
            {
                TABCoopMod.Sync.FactionContextManager.RecalculateAllFactions();
            }

            object buildCmd = GameBridge.GetBuildCommandForType(buildingType);
            if (buildCmd != null && !GameBridge.CanExecuteCommandOnEntity(buildCmd, builder))
            {
                string errReason = null;
                try
                {
                    var errField = GameBridge.TypeZXCommand?.GetField("#=z4gwFgFCco_Z9",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    errReason = errField?.GetValue(buildCmd) as string;
                    if (string.IsNullOrEmpty(errReason))
                        errReason = buildCmd.GetType().GetProperty("Error")?.GetValue(buildCmd) as string;
                }
                catch { }
                string econInfo = GameBridge.ReadRawLevelEconomyState(out var eco)
                    ? $" [G={eco.Gold},W={eco.Wood},S={eco.Stone},I={eco.Iron},O={eco.Oil},Col={eco.Colonists},Work={eco.WorkersSupply - eco.WorkersNeeded},Eng={eco.EnergySupply - eco.EnergyNeeded},Food={eco.FoodSupply - eco.FoodNeeded}]"
                    : string.Empty;
                LogRejected($"buildExec:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected PlaceBuilding {netCmd.BuildingTypeName}: CanExecute=false ({errReason ?? "insufficient resources or prerequisites"}){econInfo}.");
                return;
            }

            if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin))
            {
                LogRejected($"buildCell:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected PlaceBuilding {netCmd.BuildingTypeName} at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}): target cells occupied or invalid.");
                return;
            }

            object target = BuildCommandTarget(netCmd);
            if (buildCmd != null)
            {
                // Execute through Build.#=zfmgzkuw= so resource deduction and statistics are properly applied!
                GameBridge.ExecuteCommandOnEntity(buildCmd, builder, target);
            }
            else
            {
                GameBridge.PlaceBuilding(builder, target, buildingType, netCmd.IsBonusBuilding, netCmd.InstantBuild, null);
            }
            // Commit AddToScene() / replacement Destroy immediately so subsequent PlaceBuilding
            // commands in the same tick see occupied cells on DXWorldGrid.
            GameBridge.FlushStartFramePostMethods();
        }

        private static void ExecuteForcePlaceBuilding(NetCommand netCmd)
        {
            if (string.IsNullOrEmpty(netCmd.BuildingTypeName)) return;

            Type buildingType = GameBridge.GameAssembly.GetType(netCmd.BuildingTypeName);
            if (buildingType == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown BuildingTypeName in force_place_building: {netCmd.BuildingTypeName}");
                return;
            }

            ulong builderId = netCmd.ExecutorEntityIds.Length > 0 ? netCmd.ExecutorEntityIds[0] : 0UL;
            object builder = builderId != 0UL ? DXGame.Current?.GetEntityByID(builderId) : null;
            if (!IsLiveExecutableEntityForPlayer(builder, netCmd.PlayerId))
            {
                builder = TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive
                    ? TABCoopMod.Sync.FactionOwnershipTracker.GetPrimaryCommandCenterForPlayer(netCmd.PlayerId)
                    : GameBridge.GetCommandCenterEntity();
            }
            if (!IsLiveExecutableEntityForPlayer(builder, netCmd.PlayerId)) return;

            if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin))
            {
                LogRejected($"forceBuildCell:{netCmd.BuildingTypeName}", $"[CommandExecutor] Rejected force_place_building {netCmd.BuildingTypeName} at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}): target cells occupied or invalid.");
                return;
            }

            object target = BuildCommandTarget(netCmd);
            byte prevSpawnOwner = FactionSpawnContext.ActiveSpawningOwner;
            if (TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive && netCmd.PlayerId > 0)
                FactionSpawnContext.ActiveSpawningOwner = netCmd.PlayerId;
            try
            {
                object placed = GameBridge.PlaceBuilding(builder, target, buildingType, isBonus: false, instant: true, template: null);
                GameBridge.FlushStartFramePostMethods();
                if (placed is DXEntity placedEnt && TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive && netCmd.PlayerId > 0)
                    TABCoopMod.Sync.FactionOwnershipTracker.SetOwner(placedEnt.ID, netCmd.PlayerId);
                if (netCmd.InstantBuild && placed != null && GameBridge.IsTheInnEntity(placed))
                    GameBridge.TriggerFastTheInnMercenaries(placed);
                ModLogger.Info($"[CommandExecutor] ForcePlaceBuilding {netCmd.BuildingTypeName} (P{netCmd.PlayerId}) at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}) -> {(placed != null ? "OK" : "NULL")}");
            }
            finally
            {
                FactionSpawnContext.ActiveSpawningOwner = prevSpawnOwner;
            }
        }

        private static void ExecuteCancelCurrentOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var ent = DXGame.Current?.GetEntityByID(id);
                if (!IsLiveExecutableEntityForPlayer(ent, netCmd.PlayerId)) continue;
                object cmdComp = GameBridge.GetCommandableObject(ent);
                if (cmdComp != null) GameBridge.CancelCurrentOrderObject(cmdComp);
            }
        }

        private static void ExecuteCancelQueuedOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var ent = DXGame.Current?.GetEntityByID(id);
                if (!IsLiveExecutableEntityForPlayer(ent, netCmd.PlayerId)) continue;
                object cmdComp = GameBridge.GetCommandableObject(ent);
                if (cmdComp != null) GameBridge.CancelQueueSlotObject(cmdComp, netCmd.QueueSlotIndex);
            }
        }

        internal static void ShowBannerOutsideDet(string message, Color color, int durationMs)
        {
            bool prevDet = LockstepClock.InDeterministicContext;
            LockstepClock.InDeterministicContext = false;
            LockstepClock.EnterVisualVfxContext();
            try
            {
                GameBridge.ShowTopBanner(message, color, durationMs);
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
                LockstepClock.InDeterministicContext = prevDet;
            }
        }

        private static bool IsLiveExecutableEntity(object entity)
        {
            var ent = entity as DXEntity; // DXEntity is public; safe to cast
            if (ent == null || ent.Disposed) return false;
            Type zxEntity = GameBridge.TypeZXEntity;
            return zxEntity == null || zxEntity.IsInstanceOfType(ent);
        }

        private static bool IsLiveExecutableEntityForPlayer(object entity, byte playerId)
        {
            if (!IsLiveExecutableEntity(entity)) return false;
            if (!TABCoopMod.Sync.CoopSessionManager.IsMultiFactionActive) return true;
            return TABCoopMod.Sync.FactionOwnershipTracker.CanPlayerControl(playerId, entity as DXEntity);
        }

        public static object BuildCommandTarget(NetCommand netCmd)
        {
            return GameBridge.CreateCommandTarget(
                netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin, netCmd.TargetEntityId);
        }
    }
}
