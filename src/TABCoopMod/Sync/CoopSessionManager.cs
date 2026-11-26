using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Networking;
using TABCoopMod.Patches;
using TABCoopMod.Testing;
using TABCoopMod.UI;
using ZX.GUI;

namespace TABCoopMod.Sync
{
    public enum LobbyRoomPhase : byte
    {
        Idle = 0,
        InLobby = 1,
        HostSelectingMenu = 2,
        HostLoadingLevel = 3,
        InGame = 4
    }

    public enum LobbyHostNoticeType : byte
    {
        PhaseChanged = 1,
        ReturnToLobby = 2
    }

    /// <summary>
    /// Orchestrates the Archon Mode Co-op Session:
    /// 1. Initial & Resync Save-Stream Transfer (.zxsav + .zxcheck chunked over Reliable channel)
    /// 2. 30Hz Lockstep Command Turn Synchronization (works both when Running and when Paused!)
    /// 3. Periodic 60-Tick StateHash verification & Auto-Resync trigger
    /// 4. 20Hz Unreliable Ally Cursor & Minimap Camera Viewport broadcast
    /// </summary>
    public static class CoopSessionManager
    {
        public const int ChunkSizeBytes = 32768; // 32 KB per reliable packet
        public const int SyncSeed = 20261109;

        public static INetworkTransport Transport { get; private set; }
        public static bool IsSessionActive => Transport != null && Transport.IsActive;
        public static bool IsInSyncedLevel { get; private set; }
        public static bool IsInSyncBarrier =>
            IsSessionActive && !IsInSyncedLevel &&
            (_hostSnapshotReloadPending || _awaitingSnapshotLoad || _awaitingClientSnapshotAck
             || _pendingClientSnapshotLoadPath != null || _pendingHostSnapshotExport
             || GameBridge.CurrentLevelState != null);
        public static bool IsGameplayInputLocked => IsInSyncBarrier;
        public static int CurrentSessionSeed { get; private set; } = SyncSeed;
        public static LobbyRoomPhase RoomPhase { get; private set; } = LobbyRoomPhase.Idle;
        public static CoopBalancePreset BalancePreset { get; private set; } = CoopBalancePreset.Balanced;
        // 0 = auto (actual player count, capped at MaxBalanceEnhancementPlayers); 2/3 = explicit scaling tier.
        public static int BalanceEnhancementPlayers { get; private set; } = 0;
        public static CoopGameMode GameMode { get; private set; } = CoopGameMode.Archon;
        public static MultiFactionDefeatRule DefeatRule { get; private set; } = MultiFactionDefeatRule.AllBasesLost;
        public static MultiFactionSpawnMode SpawnMode { get; private set; } = MultiFactionSpawnMode.Separated;
        public static bool IsMultiFactionActive => IsSessionActive && GameMode == CoopGameMode.MultiFaction;
        public static string HostActionHint { get; private set; } = string.Empty;
        public static bool ShouldOpenLobbyOnMainMenu { get; set; }

        // Local loopback network fluctuation simulation (only applies to LocalTcpLoopbackTransport).
        public static bool LocalChaosEnabled { get; private set; }
        public static int LocalChaosLatencyMs { get; private set; } = 80;
        public static int LocalChaosJitterMs { get; private set; } = 20;
        public static double LocalChaosLossRate { get; private set; } = 0.02;
        public static bool IsLocalChaosLockedByEnv => TestHarnessConfig.HasChaos;
        public static bool IsLocalLoopbackSession =>
            UnwrapTransport() is LocalTcpLoopbackTransport loop && !loop.IsLanMode;
        public static bool IsLanSession =>
            UnwrapTransport() is LocalTcpLoopbackTransport lan && lan.IsLanMode;

        public static INetworkTransport UnwrapTransport(INetworkTransport transport = null)
        {
            var t = transport ?? Transport;
            while (t is ChaosTransport chaos)
                t = chaos.InnerTransport;
            return t;
        }
        private static volatile bool _pendingClientReturnToMainMenu;
        private static volatile bool _suppressReturnNotice;
        private static readonly List<PeerInfo> _lobbyRoster = new List<PeerInfo>();

        public static IReadOnlyList<PeerInfo> LobbyRoster => _lobbyRoster;
        public static event Action LobbyStateChanged;

        public static bool EnableAutoResync = true;
        // Host-authoritative async correction radius. Host positions are valid on the client because
        // static terrain/buildings are identical, so diverged units up to this distance are snapped back.
        public const float MinAsyncPosSnapCells = 0.50f;
        public const float MaxAsyncNudgeCells = 8.0f;
        public const float MaxAsyncTeleportCells = 8.0f;
        public const double AsyncPosAlignCooldownMs = 2000.0;
        public const double AsyncSoftPosAlignCooldownMs = 8000.0;
        public const int EntityPosRealignCooldownTicks = 180;

        // Per-owner differential alignment (docs/11 Section 1)
        public const int OwnershipLeaseTicks = 300;               // 10s max lease
        public const int OwnershipIdleGraceTicks = 90;            // 3s idle grace after last order
        public const float SelfOwnedHostOverrideThreshold = 3.5f; // Client skips Host soft nudges < 3.5 cells for own units
        public const float ReverseAlignMinThreshold = 0.25f;      // Keep Host/Client owned squad within ~0.2-0.3 cells
        public const int MaxReverseAlignUnitsPerReport = 64;

        public static bool HasVersionMismatch { get; private set; }
        public static string VersionMismatchSummary { get; private set; } = string.Empty;
        private static readonly HashSet<string> _versionMismatchAlertedKeys = new HashSet<string>();
        private static string _pendingVersionMismatchAlert;
        public static string SteamLobbyError { get; private set; } = string.Empty;
        private static string _pendingSteamLobbyErrorAlert;

        // Tiered reload policy (async_align). Positions / HP / aggro / projectiles / 5-resource econ /
        // production queues / small structure & entity deltas are corrected continuously without pausing;
        // only sustained or large-scale divergence forces a full reload.
        private const int EconReloadStreak = 3;              // ~6s persistent time/unhandled econ mismatch
        private const int AsyncEconFallbackStreak = 6;       // ~12s fallback if async econ/prod/resv fails to converge
        private const int StructuralReloadStreak = 6;        // ~12s entity-count delta beyond tolerance
        private const int MissingOnClientReloadStreak = 6;   // ~12s sustained unspawned/missing Host entities on Client
        private const int WidePosReloadStreak = 10;          // ~15-20s wide residual drift after corrections
        private const double StructuralResyncCooldownMs = 90000.0;
        private const double LongPeriodRealignMs = 300000.0; // 5 min continuous residual divergence
        private static int _consecutiveEconMismatch;
        private static int _consecutiveAsyncEconMismatch;
        private static int _consecutiveStructuralMismatch;
        private static int _consecutiveMissingOnClient;
        private static int _consecutiveSubCoarsePosDrift;
        private static int _lastClientMissingOnClient;
        private static int _lastClientMissingOnHost;
        private static double _divergedSinceTicks = -1.0;

        // Periodic log throttling (verbose mode bypasses via ModLogger.IsVerbose)
        private const double ChecksumSummaryIntervalMs = 30000.0;
        private const double PosAlignLogIntervalMs = 30000.0;
        private static string _lastChecksumLogKind;
        private static double _lastChecksumMatchLogMs = -100000.0;
        private static double _lastChecksumTransientLogMs = -100000.0;
        private static double _lastChecksumSoftLogMs = -100000.0;
        private static double _lastPosDriftStatsLogMs = -100000.0;
        private static double _lastAsyncPosAlignLogMs = -100000.0;
        private static int _suppressedChecksumMatchCount;
        private static int _suppressedChecksumSoftCount;

        private static void ResetTieredResyncState()
        {
            _consecutiveEconMismatch = 0;
            _consecutiveAsyncEconMismatch = 0;
            _consecutiveStructuralMismatch = 0;
            _consecutiveMissingOnClient = 0;
            _consecutiveSubCoarsePosDrift = 0;
            _lastClientMissingOnClient = 0;
            _lastClientMissingOnHost = 0;
            _divergedSinceTicks = -1.0;
        }
        private static double _lastAutoResyncTicks = -100000.0;
        private static double _lastPosSyncSentTicks = -100000.0;
        private static double _lastFullPosAlignSentTicks = -100000.0;
        private static readonly Dictionary<ulong, int> _lastEntityPosCorrectedTick = new Dictionary<ulong, int>();
        private static bool _desyncDumped;

        private static string EffectiveResyncMode =>
            string.IsNullOrEmpty(TestHarnessConfig.ResyncMode) ? "async_align" : TestHarnessConfig.ResyncMode.ToLowerInvariant();
        internal static bool IsStrictMode => EffectiveResyncMode == "strict";
        internal static bool EnableAsyncPosAlign => EffectiveResyncMode == "async_align";
        private static double AutoResyncCooldownMs =>
            TestHarnessConfig.ResyncCooldownMs > 0
                ? TestHarnessConfig.ResyncCooldownMs
                : (IsStrictMode ? 15000.0 : 120000.0);

        private static readonly List<NetCommand> _localOutgoingQueue = new List<NetCommand>();
        private static readonly List<NetCommand> _hostTurnBuffer = new List<NetCommand>();
        private static readonly SortedDictionary<int, List<NetCommand>> _hostSymmetricQueue =
            new SortedDictionary<int, List<NetCommand>>();

        // True network lockstep: Host stamps every command with TargetSimTick = simTick + InputDelayTicks
        // and broadcasts a heartbeat for EVERY tick (even empty), so all peers execute the same
        // commands on the same tick regardless of network latency. InputDelayTicks adapts cleanly
        // between 1..4 ticks based on smoothed peer RTT.
        public const int MinInputDelayTicks = 1;
        public const int MaxInputDelayTicks = 4;
        public static int InputDelayTicks { get; private set; } = 1;
        public static double CurrentSmoothedRttMs { get; private set; }
        private static readonly SortedDictionary<int, List<NetCommand>> _pendingTurnCommands =
            new SortedDictionary<int, List<NetCommand>>();
        private static TurnInputTickGate _networkGate = new TurnInputTickGate();
        private static int _hostLastBroadcastSimTick = -1;
        private static int _hostLastBroadcastTargetTick = -1;
        private static uint _rttPingSeq;
        private static double _lastRttProbeTicks = -100000.0;
        private static double _lastDelayAdjustTicks = -100000.0;
        private static readonly Dictionary<byte, double> _peerSmoothedRttMs = new Dictionary<byte, double>();
        private static readonly Dictionary<byte, double> _peerRttSampleTicks = new Dictionary<byte, double>();
        private static readonly Dictionary<byte, int> _peerLastSimTick = new Dictionary<byte, int>();
        private static readonly Dictionary<byte, double> _peerLastSimTickWallMs = new Dictionary<byte, double>();
        private const double RttProbeIntervalMs = 500.0;
        private const double RttStalePeerMs = 5000.0;
        private const double ClientLagStaleMs = 3500.0;
        private const int HostBackpressureSlowLagTicks = 5;
        private const int HostBackpressureHoldLagTicks = 9;
        private const double DelayAdjustCooldownMs = 1500.0;
        private static int _gateLastObservedTick = -1;
        private static double _gateWaitStartTicks = -1.0;
        private static double _gateLastWarnTicks = -1.0;
        private const double GateTimeoutMs = 3000.0;

        private static bool _awaitingSnapshotLoad;
        private static bool _awaitingClientSnapshotAck;
        private static double _awaitingClientAckStartTicks = -1.0;
        private static bool _hostSnapshotReloadPending;
        private static bool _hostWaitingOwnReload;
        private static bool _clientSnapshotAckReceived;
        private static bool _hasEnteredSyncedLevelInMatch;
        private static ulong[][] _pendingResyncSelectionGroups;
        public static bool IsHostSnapshotReloadPending => _hostSnapshotReloadPending;

        // Deferred work: never export/load from inside a deep engine callback (LevelLoadComplete
        // postfix / network PollEvents). Doing the ZIP save from that stack exhausted the 1 MB main
        // thread stack (0xc00000fd) and re-entered the session callbacks.
        private static bool _pendingHostSnapshotExport;
        private static string _pendingClientSnapshotLoadPath;
        private static byte[] _pendingBroadcastSavBytes;
        private static string _pendingBroadcastChkText;
        private static int _pendingBroadcastSeed;
        private static long _pendingBroadcastIdCounter;
        private static bool _kickingLoadFade;
        private static int _loadFadeKickFrames;
        private static double _loadFadeKickStartTicks = -1.0;
        private const double LoadFadeTimeoutMs = 30000.0;

        // Snapshot receive buffer on Client. Chaos/jitter can deliver chunks out of order (even
        // before SnapshotBegin), so buffer by index and assemble in order once all are present.
        private static readonly Dictionary<int, byte[]> _incomingChunks = new Dictionary<int, byte[]>();
        private static int _expectedChunks;
        private static int _expectedSaveBytes;
        private static string _incomingCheckContent = string.Empty;
        private static int _incomingRandomSeed = 1337;
        private static long _incomingEntityIdCounter;

        // Ally Cursor & Camera State
        public sealed class AllyVisualState
        {
            public byte PlayerId;
            public PointF MouseWorldPosition;
            public RectangleF CameraWorldArea;
            public DateTime LastUpdatedUtc;
        }

        public sealed class TickDigestRecord
        {
            public StateHasher.LevelStateSnapshotDigest Digest;
            public string Summary;
            public List<OwnedUnitPosSample> OwnedUnits;
            public long SyncedIdCounter;
            public bool HasFullDetail = true;
            public bool DetailRequested;
            public double DetailRequestedAtMs;
            public ulong DetailMixHash;
        }

        private struct UnitOwnershipEntry
        {
            public byte PlayerId;
            public int LastOrderTick;
        }

        public struct OwnedUnitPosSample
        {
            public ulong Id;
            public float X;
            public float Y;
        }

        private sealed class PendingReverseAlignApply
        {
            public int Generation;
            public byte SenderPlayerId;
            public int Tick;
            public List<OwnedUnitPosSample> Units;
        }

        private sealed class PosSyncReceiveBuffer
        {
            public int SimTick;
            public bool ApplyCorrection;
            public int TotalChunks;
            public int CompressedTotalLen;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
        }

        private struct EntityDeltaFix
        {
            public ulong Id;
            public float Dx;
            public float Dy;
        }

        private struct EntityLifeFix
        {
            public ulong Id;
            public int Delta;
            public bool Kill;
            public byte ExpectedTeamFilter; // 0 = any, 1 = player (teamRaw==0), 2 = non-player/zombie (teamRaw!=0)
        }

        private sealed class OutgoingPosSyncBatch
        {
            public int Generation;
            public byte TargetPlayerId;
            public int Seq;
            public int Tick;
            public bool ApplyCorrection;
            public int TotalCompressedLen;
            public List<byte[]> Chunks;
        }

        private struct EconSnapshot
        {
            public int Gold, Wood, Stone, Iron, Oil;
        }

        private struct StructSampleMeta
        {
            public ulong Id;
            public ulong TemplateId;
            public int CellX;
            public int CellY;
            public byte Width;
            public byte Height;
            public int Life;
            public float BuildFactor;
            public byte OwnerPlayerId;
        }

        private struct DynamicTargetSample
        {
            public ulong Id;
            public ulong TargetEntityId;
            public float MoveX;
            public float MoveY;
        }

        private struct DynamicSpawnMeta
        {
            public ulong Id;
            public ulong TemplateId;
            public byte TeamRaw;
            public ulong TargetEntityId;
            public byte OwnerPlayerId;
        }

        private struct DynamicSpawnSample
        {
            public ulong Id;
            public ulong TemplateId;
            public float X;
            public float Y;
            public int Life;
            public byte TeamRaw;
            public ulong TargetEntityId;
            public byte OwnerPlayerId;
        }

        private struct ProdQueueFixItem
        {
            public ulong EntityId;
            public int[] ExpectedClientCommandIds;
            public int[] HostCommandIds;
        }

        private sealed class PendingPosAlignApply
        {
            public int Generation;
            public int Tick;
            public bool ApplyCorrection;
            public bool ApplyPosCorrection;
            public int ComparedCount;
            public int Drifted001, Drifted01, Drifted1, Drifted5;
            public float MaxDelta;
            public float RmsDelta;
            public ulong MaxDeltaEntityId;
            public ulong HostMaxId;
            public bool ExceededThreshold;
            public bool HasTargetFixesPayload;
            public List<EntityDeltaFix> Deltas;
            public List<EntityLifeFix> LifeFixes;
            public int LifeDiffCount, MissingOnHost, MissingOnClient;
            public bool ApplyEconCorrection;
            public int DeltaGold, DeltaWood, DeltaStone, DeltaIron, DeltaOil;
            public List<ProdQueueFixItem> ProdQueueFixes;
            public List<StructSampleMeta> StructBuilds;
            public List<ulong> StructDestroys;
            public Dictionary<ulong, DynamicTargetSample> TargetFixes;
            public HashSet<ulong> DriftedPosIds15;
            public List<DynamicSpawnSample> DynamicSpawns;
        }

        public static readonly Dictionary<byte, AllyVisualState> AllyStates = new Dictionary<byte, AllyVisualState>();
        private static readonly StateHasher.IncrementalEpochHasher _epochHasher = new StateHasher.IncrementalEpochHasher();
        private static readonly ConcurrentQueue<OutgoingPosSyncBatch> _outgoingPosSyncQueue =
            new ConcurrentQueue<OutgoingPosSyncBatch>();
        private static readonly ConcurrentQueue<PendingPosAlignApply> _incomingPosAlignQueue =
            new ConcurrentQueue<PendingPosAlignApply>();
        private static readonly ConcurrentQueue<PendingReverseAlignApply> _incomingReverseAlignQueue =
            new ConcurrentQueue<PendingReverseAlignApply>();
        private static readonly object _unitOwnershipSync = new object();
        private static readonly Dictionary<ulong, UnitOwnershipEntry> _unitOwnership =
            new Dictionary<ulong, UnitOwnershipEntry>();
        private static volatile int _asyncGeneration;
        private static int _posSyncBuildInFlight;

        private static double _lastCursorBroadcastTicks;
        private static int _lastCheckedSimTick = -1;
        private static bool _firstChecksumPosDumped;
        private static int _consecutiveProjOnlyMismatch;
        private static int _consecutiveHardMismatch;
        private static int _consecutivePosExceeded;
        private static int _verifiedChecksumCycles;
        private static bool _econEarlyBypassUsed;
        private static bool _driftOnsetLogged;
        private static int _posSyncSeq;

        // Modal / engine pause keepalive & E2E command latency telemetry
        private static double _hostLastPauseKeepaliveSentTicks = -100000.0;
        private static double _clientHostPauseKeepaliveUntilTicks = -1.0;
        private static int _clientHostPausedTick = -1;
        private static bool _clientHostInModalPause;
        private static bool _levelEndedSynced;
        public static bool IsReceivingRemoteLevelEnd { get; private set; }
        public static bool IsLevelEndedSynced => _levelEndedSynced;
        private static uint _nextTraceSeq;
        private static readonly Dictionary<uint, long> _localCmdSentTs = new Dictionary<uint, long>();
        private static readonly List<double> _cmdE2ELatencySamplesMs = new List<double>();
        private static double _lastLatencyStatsLogTicks = -100000.0;

        private static readonly Dictionary<int, TickDigestRecord> _hostTickDigests = new Dictionary<int, TickDigestRecord>();
        private static readonly Dictionary<int, Dictionary<byte, TickDigestRecord>> _clientTickDigests =
            new Dictionary<int, Dictionary<byte, TickDigestRecord>>();
        private static readonly Dictionary<int, List<StateHasher.EntityPosSample>> _tickPosCache =
            new Dictionary<int, List<StateHasher.EntityPosSample>>();
        private static readonly Dictionary<int, EconSnapshot> _tickEconCache =
            new Dictionary<int, EconSnapshot>();
        private static readonly Dictionary<int, List<StateHasher.ProdQueueSample>> _tickProdCache =
            new Dictionary<int, List<StateHasher.ProdQueueSample>>();
        private static readonly Queue<int> _tickPosCacheOrder = new Queue<int>();
        private static readonly Dictionary<int, PosSyncReceiveBuffer> _posSyncIncoming =
            new Dictionary<int, PosSyncReceiveBuffer>();

        private const byte SessionStartProtoFlag_CompactBinary = 1 << 0;
        private const byte SessionStartProtoFlag_AdaptivePhysIter = 1 << 1;
        private static bool _useCompactWireProtocol;
        public static bool SyncedAdaptivePhysicsIterations { get; private set; }
        private static PointF _lastSentCursorWorldPos = new PointF(-9999f, -9999f);
        private static RectangleF _lastSentCamArea = RectangleF.Empty;
        private static double _lastCursorCamPacketMs = -100000.0;

        private sealed class ClientEpochDetailEntry
        {
            public StateHasher.LevelStateSnapshotDigest Digest;
            public List<OwnedUnitPosSample> OwnedUnits;
        }

        private struct DriftBaselineEntry
        {
            public float X;
            public float Y;
            public int Life;
        }

        private sealed class HostPendingBaseline
        {
            public byte PeerId;
            public int Tick;
            public int Seq;
            public Dictionary<ulong, DriftBaselineEntry> Samples;
        }

        private static readonly Dictionary<int, ClientEpochDetailEntry> _clientEpochDetails = new Dictionary<int, ClientEpochDetailEntry>();
        private static readonly Queue<int> _clientEpochDetailOrder = new Queue<int>();
        private static readonly object _driftBaselineLock = new object();
        private static readonly Dictionary<byte, (int Seq, Dictionary<ulong, DriftBaselineEntry> Samples)> _hostAckedBaselineByPeer =
            new Dictionary<byte, (int, Dictionary<ulong, DriftBaselineEntry>)>();
        private static readonly Dictionary<int, HostPendingBaseline> _hostPendingBaselinesBySeq =
            new Dictionary<int, HostPendingBaseline>();
        private static int _clientPrevDriftSeq;
        private static Dictionary<ulong, StateHasher.EntityPosSample> _clientPrevDriftSamples;

        private static ushort QuantizeCursorCoord(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            int scaled = (int)Math.Round((value + 128.0) * 32.0, MidpointRounding.ToEven);
            if (scaled < 0) return 0;
            if (scaled > 65535) return 65535;
            return (ushort)scaled;
        }

        private static float DequantizeCursorCoord(ushort q)
        {
            return (q / 32.0f) - 128.0f;
        }

        private static string FormatDigestSummary(in StateHasher.LevelStateSnapshotDigest d)
        {
            return $"Pos={d.PosChecksum:X16} Coarse={d.CoarsePosChecksum:X16} Life={d.LifeChecksum:X16} " +
                   $"Aggro={d.ZombieAggroChecksum:X16}({d.AggroZombieCount}) Proj={d.ProjectileChecksum:X16} " +
                   $"Prod={d.ProductionChecksum:X16} Resv={d.ReservationChecksum:X16} " +
                   $"(entities={d.EntityCount} proj={d.ProjectileCount} queued={d.QueuedOrderCount} " +
                   $"res={d.Gold}/{d.Wood}/{d.Stone}/{d.Iron}/{d.Oil} time={d.GameTimeMs})";
        }

        private static ulong ComputeDetailMixHash(in StateHasher.LevelStateSnapshotDigest d, List<OwnedUnitPosSample> ownedUnits)
        {
            ulong h = 14695981039346656037UL;
            h = (h ^ unchecked((ulong)d.ZombieAggroChecksum)) * 1099511628211UL;
            h = (h ^ unchecked((ulong)d.ProjectileChecksum)) * 1099511628211UL;
            h = (h ^ unchecked((ulong)d.ProductionChecksum)) * 1099511628211UL;
            h = (h ^ unchecked((ulong)d.ReservationChecksum)) * 1099511628211UL;
            h = (h ^ (uint)d.Gold) * 1099511628211UL;
            h = (h ^ (uint)d.Wood) * 1099511628211UL;
            h = (h ^ (uint)d.Stone) * 1099511628211UL;
            h = (h ^ (uint)d.Iron) * 1099511628211UL;
            h = (h ^ (uint)d.Oil) * 1099511628211UL;
            if (d.PosBuckets != null)
                for (int i = 0; i < d.PosBuckets.Length; i++) h = (h ^ d.PosBuckets[i]) * 1099511628211UL;
            if (d.LifeBuckets != null)
                for (int i = 0; i < d.LifeBuckets.Length; i++) h = (h ^ d.LifeBuckets[i]) * 1099511628211UL;
            if (d.EntityBuckets != null)
                for (int i = 0; i < d.EntityBuckets.Length; i++) h = (h ^ d.EntityBuckets[i]) * 1099511628211UL;
            if (ownedUnits != null)
                for (int i = 0; i < ownedUnits.Count; i++)
                    h = (h ^ ownedUnits[i].Id) * 1099511628211UL;
            return h;
        }

        private static bool TryPackPos5B(float x, float y, out ulong packed)
        {
            packed = 0;
            if (float.IsNaN(x) || float.IsNaN(y) || x < -64f || y < -64f || x >= 959f || y >= 959f)
                return false;
            double sx = Math.Round((x + 64.0) * 256.0, MidpointRounding.ToEven);
            double sy = Math.Round((y + 64.0) * 256.0, MidpointRounding.ToEven);
            if (sx < 0 || sx > 262143.0 || sy < 0 || sy > 262143.0)
                return false;
            uint qx = (uint)sx;
            uint qy = (uint)sy;
            float rx = (qx / 256.0f) - 64.0f;
            float ry = (qy / 256.0f) - 64.0f;
            if (Math.Abs(rx - x) > 1e-4f || Math.Abs(ry - y) > 1e-4f)
                return false;
            packed = (ulong)qx | ((ulong)qy << 18);
            return true;
        }

        private static void WritePacked5B(BinaryWriter w, ulong packed)
        {
            w.Write((byte)(packed & 0xFFUL));
            w.Write((byte)((packed >> 8) & 0xFFUL));
            w.Write((byte)((packed >> 16) & 0xFFUL));
            w.Write((byte)((packed >> 24) & 0xFFUL));
            w.Write((byte)((packed >> 32) & 0xFFUL));
        }

        private static void ReadPacked5B(BinaryReader r, out float x, out float y)
        {
            ulong b0 = r.ReadByte();
            ulong b1 = r.ReadByte();
            ulong b2 = r.ReadByte();
            ulong b3 = r.ReadByte();
            ulong b4 = r.ReadByte();
            ulong packed = b0 | (b1 << 8) | (b2 << 16) | (b3 << 24) | (b4 << 32);
            uint qx = (uint)(packed & 0x3FFFFUL);
            uint qy = (uint)((packed >> 18) & 0x3FFFFUL);
            x = (qx / 256.0f) - 64.0f;
            y = (qy / 256.0f) - 64.0f;
        }

        private static void ResetSyncTrackingState()
        {
            _asyncGeneration++;
            Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
            _lastSentCursorWorldPos = new PointF(-9999f, -9999f);
            _lastSentCamArea = RectangleF.Empty;
            _lastCursorCamPacketMs = -100000.0;
            _clientEpochDetails.Clear();
            _clientEpochDetailOrder.Clear();
            lock (_driftBaselineLock)
            {
                _hostAckedBaselineByPeer.Clear();
                _hostPendingBaselinesBySeq.Clear();
                _clientPrevDriftSeq = 0;
                _clientPrevDriftSamples = null;
            }
            _lastCheckedSimTick = -1;
            _lastFullPosAlignSentTicks = -100000.0;
            _lastEntityPosCorrectedTick.Clear();
            _consecutiveProjOnlyMismatch = 0;
            _consecutiveHardMismatch = 0;
            _consecutivePosExceeded = 0;
            _verifiedChecksumCycles = 0;
            _econEarlyBypassUsed = false;
            _driftOnsetLogged = false;
            _hostLastPauseKeepaliveSentTicks = -100000.0;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostPausedTick = -1;
            _clientHostInModalPause = false;
            _levelEndedSynced = false;
            IsReceivingRemoteLevelEnd = false;
            lock (_localCmdSentTs) _localCmdSentTs.Clear();
            lock (_cmdE2ELatencySamplesMs) _cmdE2ELatencySamplesMs.Clear();
            _lastLatencyStatsLogTicks = -100000.0;
            _lastChecksumLogKind = null;
            _lastChecksumMatchLogMs = -100000.0;
            _lastChecksumTransientLogMs = -100000.0;
            _lastChecksumSoftLogMs = -100000.0;
            _lastPosDriftStatsLogMs = -100000.0;
            _lastAsyncPosAlignLogMs = -100000.0;
            _suppressedChecksumMatchCount = 0;
            _suppressedChecksumSoftCount = 0;
            ResetTieredResyncState();
            _epochHasher.Reset();
            _hostTickDigests.Clear();
            _clientTickDigests.Clear();
            _tickPosCache.Clear();
            _tickEconCache.Clear();
            _tickProdCache.Clear();
            _tickPosCacheOrder.Clear();
            _posSyncIncoming.Clear();
            while (_outgoingPosSyncQueue.TryDequeue(out _)) { }
            while (_incomingPosAlignQueue.TryDequeue(out _)) { }
            while (_incomingReverseAlignQueue.TryDequeue(out _)) { }
            lock (_unitOwnershipSync) _unitOwnership.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            CommandInterceptor.ClearPendingLocalBuilds();
            PauseSyncController.CancelCountdown();
            Patches.MayorSyncPatch.ResetTracking();
        }

        public static int RollNewGameSeed()
        {
            if (TestHarnessConfig.Enabled)
            {
                CurrentSessionSeed = SyncSeed;
                return CurrentSessionSeed;
            }
            int seed = unchecked((int)(DateTime.UtcNow.Ticks ^ Guid.NewGuid().GetHashCode()));
            if (seed == 0 || seed == 1337) seed = 20261110;
            CurrentSessionSeed = seed;
            LockstepClock.EnableDeterministicSandbox(CurrentSessionSeed);
            LockstepClock.RequestPostLoadCalibration(CurrentSessionSeed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(CurrentSessionSeed);
            ModLogger.Info($"[CoopSessionManager] Rolled dynamic session seed: {CurrentSessionSeed}");
            return CurrentSessionSeed;
        }

        public static bool HasJoinedClient
        {
            get
            {
                if (Transport == null) return false;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId != 1) return true;
                }
                return false;
            }
        }

        public static bool AreAllClientsReady
        {
            get
            {
                bool hasClient = false;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId == 1) continue;
                    hasClient = true;
                    if (!p.IsReady) return false;
                }
                return hasClient;
            }
        }

        public static bool CanHostStartGame =>
            IsSessionActive && Transport.IsHost && AreAllClientsReady;

        public static PeerInfo LocalPeer
        {
            get
            {
                if (Transport == null) return null;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId == Transport.LocalPlayerId) return p;
                }
                return null;
            }
        }

        public static IReadOnlyList<PeerInfo> GetEffectivePeers()
        {
            if (_lobbyRoster.Count > 0) return _lobbyRoster;
            if (Transport?.ConnectedPeers != null) return Transport.ConnectedPeers;
            return Array.Empty<PeerInfo>();
        }

        public static void SetLocalReady(bool ready)
        {
            if (!IsSessionActive || Transport.IsHost) return;
            byte localId = Transport.LocalPlayerId;
            if (localId == 0) return;

            foreach (var p in _lobbyRoster)
            {
                if (p.PlayerId == localId)
                {
                    p.IsReady = ready;
                    break;
                }
            }
            Transport.SyncPeers(_lobbyRoster);
            Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.ClientReadyState, w =>
            {
                w.Write(ready);
            });
            LobbyStateChanged?.Invoke();
        }

        public static void SetHostRoomPhase(LobbyRoomPhase phase, string hint = "")
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            RoomPhase = phase;
            HostActionHint = hint ?? string.Empty;
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void SetBalancePreset(CoopBalancePreset preset)
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            if (BalancePreset == preset) return;
            BalancePreset = preset;
            ModLogger.Info($"[CoopSessionManager] BalancePreset changed to {preset}.");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleBalancePreset()
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            SetBalancePreset(BalancePreset == CoopBalancePreset.Balanced
                ? CoopBalancePreset.Vanilla
                : CoopBalancePreset.Balanced);
        }

        public static void SetBalanceEnhancementPlayers(int value)
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            int clamped = Math.Max(0, Math.Min(CoopLimits.MaxBalanceEnhancementPlayers, value));
            if (clamped == 1) clamped = 2; // 1P enhancement is not a supported tier.
            if (BalanceEnhancementPlayers == clamped) return;
            BalanceEnhancementPlayers = clamped;
            ModLogger.Info($"[CoopSessionManager] BalanceEnhancementPlayers changed to {clamped} (0=auto).");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static int GetEffectiveBalanceEnhancementPlayers()
        {
            int actual = GetEffectivePeers().Count;
            if (actual < 2) return 0;
            int effective = BalanceEnhancementPlayers > 0
                ? BalanceEnhancementPlayers
                : Math.Min(actual, CoopLimits.MaxBalanceEnhancementPlayers);
            return Math.Max(2, Math.Min(CoopLimits.MaxBalanceEnhancementPlayers, effective));
        }

        public static void SetGameMode(CoopGameMode mode)
        {
            if (!IsSessionActive || !Transport.IsHost) return;

            if (mode == CoopGameMode.MultiFaction &&
                GetEffectivePeers().Count > CoopLimits.MaxFactionPlayers)
            {
                GameBridge.ShowTopBanner(
                    $"独立基地最多 {CoopLimits.MaxFactionPlayers} 人，已保持执政官模式",
                    Color.Orange, 3500);
                mode = CoopGameMode.Archon;
            }

            if (GameMode == mode) return;
            GameMode = mode;
            ModLogger.Info($"[CoopSessionManager] GameMode changed to {mode}.");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleGameMode()
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            SetGameMode(GameMode == CoopGameMode.Archon ? CoopGameMode.MultiFaction : CoopGameMode.Archon);
        }

        public static void SetDefeatRule(MultiFactionDefeatRule rule)
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            if (DefeatRule == rule) return;
            DefeatRule = rule;
            ModLogger.Info($"[CoopSessionManager] DefeatRule changed to {rule}.");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleDefeatRule()
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            SetDefeatRule(DefeatRule == MultiFactionDefeatRule.AllBasesLost
                ? MultiFactionDefeatRule.AnyBaseLost
                : MultiFactionDefeatRule.AllBasesLost);
        }

        internal static void ApplySyncedMultiFactionSettings(CoopGameMode mode, MultiFactionDefeatRule defeatRule)
        {
            GameMode = mode;
            DefeatRule = defeatRule;
        }

        public static void SetSpawnMode(MultiFactionSpawnMode mode)
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            if (SpawnMode == mode) return;
            SpawnMode = mode;
            ModLogger.Info($"[CoopSessionManager] SpawnMode changed to {mode}.");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleSpawnMode()
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            SetSpawnMode(SpawnMode == MultiFactionSpawnMode.Separated
                ? MultiFactionSpawnMode.Clustered
                : MultiFactionSpawnMode.Separated);
        }

        internal static void ApplySyncedSpawnMode(MultiFactionSpawnMode mode)
        {
            SpawnMode = mode;
        }

        private static ChaosConfig BuildLocalChaosConfig()
        {
            return new ChaosConfig
            {
                Enabled = LocalChaosEnabled,
                LatencyMs = LocalChaosLatencyMs,
                JitterMs = LocalChaosJitterMs,
                LossRate = LocalChaosLossRate,
                Seed = 12345
            };
        }

        public static void SetLocalChaosSettings(bool enabled, int latencyMs, int jitterMs, double lossRate)
        {
            if (TestHarnessConfig.HasChaos) return;
            if (IsSessionActive && (!Transport.IsHost || !IsLocalLoopbackSession)) return;

            LocalChaosEnabled = enabled;
            LocalChaosLatencyMs = Math.Max(0, Math.Min(500, latencyMs));
            LocalChaosJitterMs = Math.Max(0, Math.Min(300, jitterMs));
            LocalChaosLossRate = Math.Round(Math.Max(0.0, Math.Min(0.30, lossRate)), 3);

            if (IsLocalLoopbackSession && Transport is ChaosTransport chaos)
            {
                chaos.UpdateConfig(BuildLocalChaosConfig());
            }

            if (IsSessionActive && Transport.IsHost && IsLocalLoopbackSession)
            {
                BroadcastLobbyRoster();
            }
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleLocalChaosEnabled()
        {
            SetLocalChaosSettings(!LocalChaosEnabled, LocalChaosLatencyMs, LocalChaosJitterMs, LocalChaosLossRate);
        }

        public static void BroadcastLobbyRoster()
        {
            if (!IsSessionActive || !Transport.IsHost) return;

            if (_lobbyRoster.Count == 0 && Transport.ConnectedPeers != null)
            {
                foreach (var p in Transport.ConnectedPeers)
                {
                    _lobbyRoster.Add(new PeerInfo
                    {
                        PlayerId = p.PlayerId,
                        PlayerName = p.PlayerName,
                        SteamId64 = p.SteamId64,
                        IsReady = p.PlayerId == 1 || p.IsReady
                    });
                }
            }

            Transport.SyncPeers(_lobbyRoster);
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LobbyRosterSync, w =>
            {
                w.Write((byte)RoomPhase);
                w.Write(HostActionHint ?? string.Empty);
                w.Write((byte)_lobbyRoster.Count);
                foreach (var p in _lobbyRoster)
                {
                    w.Write(p.PlayerId);
                    w.Write(p.PlayerName ?? string.Empty);
                    w.Write(p.SteamId64);
                    w.Write(p.IsReady);
                }
                w.Write((byte)BalancePreset);
                bool hasChaos = IsLocalLoopbackSession && !TestHarnessConfig.HasChaos;
                w.Write(hasChaos);
                if (hasChaos)
                {
                    w.Write(LocalChaosEnabled);
                    w.Write(LocalChaosLatencyMs);
                    w.Write(LocalChaosJitterMs);
                    w.Write((float)LocalChaosLossRate);
                }
                w.Write((byte)GameMode);
                w.Write((byte)DefeatRule);
                w.Write((byte)SpawnMode);
                w.Write((byte)BalanceEnhancementPlayers);
            });
        }

        public static void StartHostSession(bool useLoopback, int lanPort = 0)
        {
            StopSession();
            int seed = RollNewGameSeed();
            LockstepClock.EnableDeterministicSandbox(seed);
            LockstepClock.RequestPostLoadCalibration(seed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(seed);

            LocalTcpLoopbackTransport.HostRoomPhaseProvider = () => (byte)RoomPhase;
            Transport = lanPort > 0
                ? (INetworkTransport)CreateLanTransport(host: true, port: lanPort)
                : CreateTransport(useLoopback);
            BindTransportEvents();
            Transport.StartHost(CoopLimits.MaxPlayers);

            _lobbyRoster.Clear();
            if (Transport.ConnectedPeers != null)
            {
                foreach (var p in Transport.ConnectedPeers)
                {
                    _lobbyRoster.Add(new PeerInfo
                    {
                        PlayerId = p.PlayerId,
                        PlayerName = p.PlayerName,
                        SteamId64 = p.SteamId64,
                        IsReady = true
                    });
                }
            }
            RoomPhase = LobbyRoomPhase.InLobby;
            BalancePreset = CoopBalancePreset.Balanced;
            BalanceEnhancementPlayers = 0;
            GameMode = CoopGameMode.Archon;
            DefeatRule = MultiFactionDefeatRule.AllBasesLost;
            SpawnMode = MultiFactionSpawnMode.Separated;
            FactionOwnershipTracker.Reset();
            HostActionHint = string.Empty;

            CommandInterceptor.LocalPlayerId = 1;
            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            string hostMode = lanPort > 0 ? "LanTcp" : (useLoopback ? "LocalLoopback" : "SteamP2P");
            ModLogger.Info($"[CoopSessionManager] Started Host Session (Mode={hostMode}, seed={seed}).");
            LobbyStateChanged?.Invoke();
        }

        public static void JoinSession(bool useLoopback, string targetAddressOrSteamId, bool lanMode = false)
        {
            StopSession();
            CurrentSessionSeed = SyncSeed;
            LockstepClock.EnableDeterministicSandbox(CurrentSessionSeed);
            LockstepClock.RequestPostLoadCalibration(CurrentSessionSeed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(CurrentSessionSeed);

            Transport = lanMode
                ? (INetworkTransport)CreateLanTransport(host: false, port: 0)
                : CreateTransport(useLoopback);
            BindTransportEvents();
            Transport.ConnectToHost(targetAddressOrSteamId);

            RoomPhase = LobbyRoomPhase.InLobby;
            BalancePreset = CoopBalancePreset.Balanced;
            BalanceEnhancementPlayers = 0;
            GameMode = CoopGameMode.Archon;
            DefeatRule = MultiFactionDefeatRule.AllBasesLost;
            SpawnMode = MultiFactionSpawnMode.Separated;
            FactionOwnershipTracker.Reset();
            HostActionHint = string.Empty;
            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            string joinMode = lanMode ? "LanTcp" : (useLoopback ? "LocalLoopback" : "SteamP2P");
            ModLogger.Info($"[CoopSessionManager] Joining Session at '{targetAddressOrSteamId}' (Mode={joinMode}).");
            LobbyStateChanged?.Invoke();
        }

        public static void StopSession()
        {
            CommandInterceptor.IsInterceptionEnabled = false;
            CommandInterceptor.OnLocalCommandCaptured = null;
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            IsInSyncedLevel = false;
            RoomPhase = LobbyRoomPhase.Idle;
            BalancePreset = CoopBalancePreset.Balanced;
            BalanceEnhancementPlayers = 0;
            GameMode = CoopGameMode.Archon;
            DefeatRule = MultiFactionDefeatRule.AllBasesLost;
            SpawnMode = MultiFactionSpawnMode.Separated;
            FactionOwnershipTracker.Reset();
            HostActionHint = string.Empty;
            HasVersionMismatch = false;
            VersionMismatchSummary = string.Empty;
            _versionMismatchAlertedKeys.Clear();
            _pendingVersionMismatchAlert = null;
            SteamLobbyError = string.Empty;
            _pendingSteamLobbyErrorAlert = null;
            ShouldOpenLobbyOnMainMenu = false;
            _pendingClientReturnToMainMenu = false;
            _suppressReturnNotice = false;
            _lobbyRoster.Clear();
            LocalTcpLoopbackTransport.HostRoomPhaseProvider = null;
            ResetSyncTrackingState();
            LockstepClock.DisableDeterministicSandbox();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _hostSymmetricQueue.Clear();
            _pendingTurnCommands.Clear();
            UI.MultiFactionPanel.Reset();
            UI.ManualSyncButton.Reset();
            UI.AllyOverlayRenderer.ClearUnitFactionRings();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            InputDelayTicks = MinInputDelayTicks;
            CurrentSmoothedRttMs = 0.0;
            _lastRttProbeTicks = -100000.0;
            _lastDelayAdjustTicks = -100000.0;
            _peerSmoothedRttMs.Clear();
            _peerRttSampleTicks.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            _gateLastObservedTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _hasEnteredSyncedLevelInMatch = false;
            _pendingResyncSelectionGroups = null;
            _lastAutoResyncTicks = -100000.0;
            _lastPosSyncSentTicks = -100000.0;
            _lastFullPosAlignSentTicks = -100000.0;
            _lastEntityPosCorrectedTick.Clear();
            _pendingHostSnapshotExport = false;
            _pendingClientSnapshotLoadPath = null;
            _pendingBroadcastSavBytes = null;
            _pendingBroadcastChkText = null;
            _kickingLoadFade = false;
            _loadFadeKickStartTicks = -1.0;
            _incomingChunks.Clear();
            _expectedChunks = 0;
            _networkGate = new TurnInputTickGate();
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;

            if (Transport != null)
            {
                Transport.Dispose();
                Transport = null;
            }
            try
            {
                if (DXGame.Current != null && !DXGame.Current.InputEnabled)
                    DXGame.Current.InputEnabled = true;
            }
            catch { }
            LobbyStateChanged?.Invoke();
        }

        public static void OnReturningToMainMenu()
        {
            if (!IsSessionActive) return;

            bool isHost = Transport.IsHost;
            bool suppressBroadcast = _suppressReturnNotice;
            _suppressReturnNotice = false;

            IsInSyncedLevel = false;
            CommandInterceptor.IsInterceptionEnabled = false;
            try
            {
                if (DXGame.Current != null && !DXGame.Current.InputEnabled)
                    DXGame.Current.InputEnabled = true;
            }
            catch { }
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            PauseSyncController.CancelCountdown();
            LockstepClock.SetSessionPause(false);
            RenderVisualInterpolationPatch.ExitVisualSceneUpdate(DXGame.Current);
            RenderVisualInterpolationPatch.Reset();

            ResetSyncTrackingState();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _hostSymmetricQueue.Clear();
            _pendingTurnCommands.Clear();
            UI.MultiFactionPanel.Reset();
            UI.ManualSyncButton.Reset();
            UI.AllyOverlayRenderer.ClearUnitFactionRings();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            InputDelayTicks = MinInputDelayTicks;
            CurrentSmoothedRttMs = 0.0;
            _lastRttProbeTicks = -100000.0;
            _lastDelayAdjustTicks = -100000.0;
            _peerSmoothedRttMs.Clear();
            _peerRttSampleTicks.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            _gateLastObservedTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _hasEnteredSyncedLevelInMatch = false;
            _pendingResyncSelectionGroups = null;
            FactionOwnershipTracker.Reset();
            _pendingHostSnapshotExport = false;
            _pendingClientSnapshotLoadPath = null;
            _pendingBroadcastSavBytes = null;
            _pendingBroadcastChkText = null;
            _kickingLoadFade = false;
            _loadFadeKickStartTicks = -1.0;
            _incomingChunks.Clear();
            _expectedChunks = 0;
            _networkGate = new TurnInputTickGate();
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;
            LockstepClock.TickGate = null;

            ShouldOpenLobbyOnMainMenu = true;

            if (isHost)
            {
                foreach (var p in _lobbyRoster)
                {
                    if (p.PlayerId != 1) p.IsReady = false;
                }
                RoomPhase = LobbyRoomPhase.InLobby;
                HostActionHint = string.Empty;
                RollNewGameSeed();

                if (!suppressBroadcast)
                {
                    Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LobbyHostNotice, w =>
                    {
                        w.Write((byte)LobbyHostNoticeType.ReturnToLobby);
                    });
                }
                BroadcastLobbyRoster();
                ModLogger.Info("[CoopSessionManager] Host returning to Main Menu -> preserved connection and returned room to Lobby.");
            }
            else
            {
                if (!suppressBroadcast)
                {
                    SetLocalReady(false);
                    ModLogger.Info("[CoopSessionManager] Client returning to Main Menu -> preserved connection and notified Host Unready.");
                }
                else
                {
                    foreach (var p in _lobbyRoster)
                    {
                        if (p.PlayerId != 1) p.IsReady = false;
                    }
                    RoomPhase = LobbyRoomPhase.InLobby;
                    HostActionHint = string.Empty;
                    Transport.SyncPeers(_lobbyRoster);
                    ModLogger.Info("[CoopSessionManager] Client followed Host back to Lobby.");
                }
            }
            LobbyStateChanged?.Invoke();
        }

        private static LocalTcpLoopbackTransport CreateLanTransport(bool host, int port)
        {
            var transport = new LocalTcpLoopbackTransport
            {
                IsLanMode = true,
                BindAddress = host ? "0.0.0.0" : "127.0.0.1",
                ListenPort = (host && port > 0 && port <= 65535) ? port : LocalTcpLoopbackTransport.DefaultPort
            };
            return transport;
        }

        private static INetworkTransport CreateTransport(bool useLoopback)
        {
            INetworkTransport transport = useLoopback
                ? (INetworkTransport)new LocalTcpLoopbackTransport()
                : new SteamP2PTransport();

            if (TestHarnessConfig.HasChaos)
            {
                var envCfg = ChaosConfig.FromString(TestHarnessConfig.ChaosSpec);
                envCfg.Enabled = true;
                LocalChaosEnabled = true;
                LocalChaosLatencyMs = envCfg.LatencyMs;
                LocalChaosJitterMs = envCfg.JitterMs;
                LocalChaosLossRate = envCfg.LossRate;
                transport = new ChaosTransport(transport, envCfg);
                ModLogger.Info($"[CoopSessionManager] Chaos injection active: {TestHarnessConfig.ChaosSpec}");
            }
            else if (useLoopback)
            {
                transport = new ChaosTransport(transport, BuildLocalChaosConfig());
            }
            return transport;
        }

        private static void BindTransportEvents()
        {
            Transport.OnPeerConnected += peer =>
            {
                CommandInterceptor.LocalPlayerId = Transport.LocalPlayerId;
                ModLogger.Info($"[CoopSessionManager] Peer connected: {peer?.PlayerName} (PlayerId={peer?.PlayerId}, LocalId={Transport.LocalPlayerId})");
                GameBridge.ShowTopBanner($"Co-op Peer Connected: {peer?.PlayerName}", Color.SpringGreen, 3000);

                if (Transport.IsHost)
                {
                    if (peer != null)
                    {
                        _lobbyRoster.RemoveAll(p =>
                            p.PlayerId == peer.PlayerId
                            || (peer.SteamId64 != 0 && p.SteamId64 == peer.SteamId64 && p.PlayerId != 1));
                        _lobbyRoster.Add(new PeerInfo
                        {
                            PlayerId = peer.PlayerId,
                            PlayerName = peer.PlayerName,
                            SteamId64 = peer.SteamId64,
                            IsReady = peer.PlayerId == 1 || peer.IsReady
                        });
                        _lobbyRoster.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
                    }
                    BroadcastLobbyRoster();

                    if (GameMode == CoopGameMode.MultiFaction &&
                        _lobbyRoster.Count > CoopLimits.MaxFactionPlayers)
                    {
                        GameBridge.ShowTopBanner(
                            $"房间超过 {CoopLimits.MaxFactionPlayers} 人，已自动切回执政官模式",
                            Color.Orange, 3500);
                        SetGameMode(CoopGameMode.Archon);
                    }

                    // If Host is already inside a level when a peer connects, push snapshot immediately!
                    if (GameBridge.CurrentLevelState != null && !_hostSnapshotReloadPending)
                    {
                        RequestSnapshotBroadcast();
                    }
                }
                else
                {
                    if (Transport.ConnectedPeers != null && Transport.ConnectedPeers.Count > 0)
                    {
                        _lobbyRoster.Clear();
                        foreach (var p in Transport.ConnectedPeers)
                        {
                            _lobbyRoster.Add(new PeerInfo
                            {
                                PlayerId = p.PlayerId,
                                PlayerName = p.PlayerName,
                                SteamId64 = p.SteamId64,
                                IsReady = p.IsReady
                            });
                        }
                    }
                }
                LobbyStateChanged?.Invoke();
            };

            Transport.OnPeerDisconnected += playerId =>
            {
                AllyStates.Remove(playerId);
                _peerSmoothedRttMs.Remove(playerId);
                _peerRttSampleTicks.Remove(playerId);
                _peerLastSimTick.Remove(playerId);
                _peerLastSimTickWallMs.Remove(playerId);
                _lobbyRoster.RemoveAll(p => p.PlayerId == playerId);
                if (Transport != null && Transport.IsHost)
                    UpdateAdaptiveInputDelay();

                if (Transport != null && Transport.IsHost)
                {
                    BroadcastLobbyRoster();
                    GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
                    LobbyStateChanged?.Invoke();
                }
                else if (Transport != null && !Transport.IsHost)
                {
                    _networkGate.MarkReady(int.MaxValue);
                    IsInSyncedLevel = false;
                    if (playerId == 1)
                    {
                        GameBridge.ShowTopBanner("Host disconnected / Room disbanded", Color.OrangeRed, 3500);
                        StopSession();
                    }
                    else
                    {
                        GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
                        LobbyStateChanged?.Invoke();
                    }
                }
            };

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnVersionMismatch += OnTransportVersionMismatch;
            if (UnwrapTransport(Transport) is SteamP2PTransport steamTransport)
            {
                steamTransport.OnLobbyReady += () =>
                {
                    SteamLobbyError = string.Empty;
                    _pendingSteamLobbyErrorAlert = null;
                    LobbyStateChanged?.Invoke();
                };
                steamTransport.OnLobbyError += OnSteamLobbyError;
            }
        }

        private static void OnSteamLobbyError(string shortError, string detailError)
        {
            SteamLobbyError = shortError ?? string.Empty;
            _pendingSteamLobbyErrorAlert = detailError ?? shortError;
            LobbyStateChanged?.Invoke();
        }

        private static void OnTransportVersionMismatch(string peerName, string localVer, string remoteVer)
        {
            string peerLabel = string.IsNullOrEmpty(peerName)
                ? (Transport != null && Transport.IsHost ? "客机" : "房主")
                : peerName;
            HasVersionMismatch = true;
            VersionMismatchSummary = $"⚠ 版本不一致：本机 v{localVer} ≠ {peerLabel} (v{remoteVer})";

            string key = $"{peerLabel}|{localVer}|{remoteVer}";
            if (_versionMismatchAlertedKeys.Add(key))
            {
                ModLogger.Warn($"[CoopSessionManager] {VersionMismatchSummary}");
                _pendingVersionMismatchAlert =
                    $"检测到联机 Mod 版本不一致！\n\n" +
                    $"本机版本：v{localVer}\n" +
                    $"对端 ({peerLabel}) 版本：v{remoteVer}\n\n" +
                    $"版本不一致会导致市长候选不同、重开天数异常、物理/尸潮不同步等问题。\n" +
                    $"请所有联机玩家安装完全相同的 TABCoopMod 版本后再开始游戏！";
                GameBridge.ShowTopBanner($"Version Mismatch: v{localVer} != v{remoteVer}", Color.OrangeRed, 5000);
            }
            LobbyStateChanged?.Invoke();
        }

        /// <summary>
        /// Defers a Host snapshot export to the next OnFrameTick top-level. Callers may sit on a deep
        /// engine/network stack (LevelLoadComplete postfix, packet delivery), where the recursive ZIP
        /// serializer would blow the main-thread stack.
        /// </summary>
        public static void RequestSnapshotBroadcast()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;
            _pendingHostSnapshotExport = true;
        }

        /// <summary>
        /// Manual resync requested from the in-game button / F9.
        /// Host: export and broadcast a fresh snapshot. Client: ask the Host to do it.
        /// </summary>
        public static bool RequestManualResync()
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null)
                return false;

            if (LockstepClock.LoadingLevel || GameBridge.IsGameLoading())
            {
                GameBridge.ShowTopBanner("正在加载，暂时无法手动同步", Color.Orange, 1600);
                return false;
            }

            if (Transport.IsHost)
            {
                if (_pendingHostSnapshotExport || _awaitingClientSnapshotAck)
                {
                    GameBridge.ShowTopBanner("同步已在进行中...", Color.Gold, 1600);
                    return false;
                }

                _lastAutoResyncTicks = DXHelper_Environment.Ticks;
                ResetTieredResyncState();
                ModLogger.WarnImmediate("[CoopSessionManager] Manual resync requested by Host.");
                GameBridge.ShowTopBanner("手动同步：正在导出并广播快照...", Color.Cyan, 3000);
                RequestSnapshotBroadcast();
                return true;
            }

            ModLogger.Info("[CoopSessionManager] Manual resync requested by Client; asking Host.");
            Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.ManualResyncRequest, _ => { });
            GameBridge.ShowTopBanner("已请求房主手动同步...", Color.Cyan, 2200);
            return true;
        }

        /// <summary>
        /// Snapshots the local player's 1..8 control groups (as deterministic entity IDs) right
        /// before an in-game Auto-resync reload replaces ZXLevelState. Gated on
        /// _hasEnteredSyncedLevelInMatch so initial lobby loads never overwrite a loaded save's
        /// groups or leak groups from an earlier match.
        /// </summary>
        private static void CaptureLocalControlGroupsForMidGameReload()
        {
            if (!_hasEnteredSyncedLevelInMatch || GameBridge.CurrentLevelState == null)
                return;
            if (_pendingResyncSelectionGroups != null)
                return;
            _pendingResyncSelectionGroups = GameBridge.CaptureSelectionGroupEntityIds();
        }

        /// <summary>
        /// Restores the locally captured 1..8 control groups onto the newly loaded ZXLevelState
        /// after OnLevelLoadComplete. Control groups are local-only per Archon player and are not
        /// present in the Host's snapshot for Clients.
        /// </summary>
        private static void RestoreLocalControlGroupsAfterMidGameReload()
        {
            var saved = _pendingResyncSelectionGroups;
            _pendingResyncSelectionGroups = null;
            if (saved != null)
                GameBridge.RestoreSelectionGroupEntityIds(saved);
        }

        /// <summary>
        /// Exports the Host's current level state into a temporary .zxsav + .zxcheck pair
        /// and streams it to all connected peers in 32KB chunks.
        /// </summary>
        public static void BroadcastCurrentLevelSnapshot()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;

            double wasGameSpeed = DXGame.Current.GameSpeed;
            try
            {
                CaptureLocalControlGroupsForMidGameReload();
                DXGame.Current.Paused = true;

                string savesDir = GameBridge.GetSavesDirectory();
                string tempBaseName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, tempBaseName + ".zxsav");
                string chkPath = Path.Combine(savesDir, tempBaseName + ".zxcheck");

                if (IsMultiFactionActive)
                {
                    if (!FactionOwnershipTracker.IsInitialized)
                    {
                        int pCount = Math.Max(1, Math.Min(
                            CoopLimits.MaxFactionPlayers, GetEffectivePeers().Count));
                        FactionOwnershipTracker.EnsureExtraCommandCentersInLiveLevel(pCount);
                        FactionOwnershipTracker.AssignInitialOwnershipFromLevel(pCount);
                    }
                    FactionOwnershipTracker.SaveToLevelScriptVariables();
                }

                ModLogger.Info($"[CoopSessionManager] Exporting Host snapshot to {savPath}...");
                GameBridge.WriteSaveFile(savPath);

                byte[] savBytes = File.ReadAllBytes(savPath);
                string chkText = File.Exists(chkPath) ? File.ReadAllText(chkPath) : string.Empty;
                int syncSeed = CurrentSessionSeed;
                long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;

                _hostTickDigests.Clear();
                _clientTickDigests.Clear();
                _tickPosCache.Clear();
                _tickPosCacheOrder.Clear();
                _posSyncIncoming.Clear();
                _consecutiveProjOnlyMismatch = 0;
                _consecutiveHardMismatch = 0;
                _consecutivePosExceeded = 0;
                ResetTieredResyncState();
                _lastCheckedSimTick = -1;
                _pendingTurnCommands.Clear();
                _hostTurnBuffer.Clear();
                _localOutgoingQueue.Clear();

                bool hasRemotePeers = Transport.ConnectedPeers != null && Transport.ConnectedPeers.Count > 1;
                if (!hasRemotePeers)
                {
                    LockstepClock.ApplyDeterministicLevelBaseline(syncSeed, idCounter);
                    EnterSyncedLevel();
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _hostSnapshotReloadPending = false;
                    _hostWaitingOwnReload = false;
                    _clientSnapshotAckReceived = false;
                    return;
                }

                // Exit synced-level state during symmetric reload so neither peer runs ticks/inputs early.
                IsInSyncedLevel = false;
                CommandInterceptor.IsInterceptionEnabled = false;
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;

                _hostSnapshotReloadPending = true;
                _hostWaitingOwnReload = true;
                _clientSnapshotAckReceived = false;
                _awaitingClientSnapshotAck = false;
                _awaitingClientAckStartTicks = -1.0;

                if (!IsLocalLoopbackSession)
                {
                    // Steam P2P (distinct machines / disks): broadcast the snapshot chunks immediately BEFORE
                    // Host starts its own symmetric reload so Host and Client load in parallel (~2x faster resync).
                    _pendingBroadcastSavBytes = null;
                    _pendingBroadcastChkText = null;
                    BroadcastSnapshotChunksToClients(savBytes, chkText, syncSeed, idCounter);
                    ModLogger.InfoImmediate($"[CoopSessionManager] Parallel Steam P2P reload: broadcasted snapshot ({savBytes.Length:N0} bytes) before starting Host reload of {savPath}...");
                }
                else
                {
                    // Local loopback shares the same Saves directory (coop_host_snapshot.zxsav), so Host reloads
                    // first before streaming to avoid same-disk file-lock collisions.
                    _pendingBroadcastSavBytes = savBytes;
                    _pendingBroadcastChkText = chkText;
                    _pendingBroadcastSeed = syncSeed;
                    _pendingBroadcastIdCounter = idCounter;
                    ModLogger.Info($"[CoopSessionManager] Host starting symmetric reload of {savPath} ({savBytes.Length:N0} bytes) before broadcasting...");
                }

                LockstepClock.EnableDeterministicSandbox(syncSeed);
                LockstepClock.RequestPostLoadCalibration(syncSeed, idCounter);
                if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false;
                GameBridge.PrepareForProgrammaticLoad();
                GameBridge.LoadSaveFileAndEnter(savPath);
                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;

                GameBridge.ShowTopBanner($"Reloading Host Snapshot ({savBytes.Length / 1024} KB)...", Color.Cyan, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to broadcast level snapshot", ex);
            }
            finally
            {
                // Never restore a zero speed: that would look like an engine pause and stall the
                // host heartbeat. Session pauses are handled separately via LockstepClock.PausedBySession.
                try { DXGame.Current.GameSpeed = wasGameSpeed > 0.0 ? wasGameSpeed : 1.0; } catch { }
                try { if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false; } catch { }
                if (_awaitingClientSnapshotAck)
                {
                    LockstepClock.SetSessionPause(true);
                }
            }
        }

        private static void BroadcastSnapshotChunksToClients(byte[] savBytes, string chkText, int syncSeed, long idCounter)
        {
            if (savBytes == null || Transport == null || !Transport.IsHost) return;
            int totalChunks = (savBytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes;
            _awaitingClientSnapshotAck = true;
            _awaitingClientAckStartTicks = DXHelper_Environment.Ticks;
            ModLogger.InfoImmediate($"[CoopSessionManager] Broadcasting Snapshot ({savBytes.Length:N0} bytes in {totalChunks} chunks, seed={syncSeed}, idCounter={idCounter})...");
            GameBridge.ShowTopBanner("Waiting for Client to finish loading...", Color.Cyan, 4000);
            Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotBegin, w =>
            {
                w.Write(savBytes.Length);
                w.Write(totalChunks);
                w.Write(chkText ?? string.Empty);
                w.Write(syncSeed);
                w.Write(idCounter);
            });
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkSizeBytes;
                int len = Math.Min(ChunkSizeBytes, savBytes.Length - offset);
                int chunkIndex = i;
                Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotChunk, w =>
                {
                    w.Write(chunkIndex);
                    w.Write(len);
                    w.Write(savBytes, offset, len);
                });
            }
        }

        private static void TryCompleteHostSyncBarrier()
        {
            if (Transport == null || !Transport.IsHost) return;
            if (_hostWaitingOwnReload || !_clientSnapshotAckReceived) return;

            _hostSnapshotReloadPending = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _clientSnapshotAckReceived = false;

            ResetSyncTrackingState();

            EnterSyncedLevel();

            int startTick = DXGame.Current?.IAIterationInt ?? 0;
            long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;
            _useCompactWireProtocol = true;
            SyncedAdaptivePhysicsIterations = TestHarnessConfig.AdaptivePhysicsIterations;
            byte protoFlags = (byte)(SessionStartProtoFlag_CompactBinary
                | (SyncedAdaptivePhysicsIterations ? SessionStartProtoFlag_AdaptivePhysIter : 0));
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.SessionStart, w =>
            {
                w.Write(startTick);
                w.Write(CurrentSessionSeed);
                w.Write(idCounter);
                w.Write((byte)InputDelayTicks);
                w.Write(protoFlags);
            });
            ModLogger.InfoImmediate($"[CoopSessionManager] Host+Client barrier satisfied; entered synced level and sent SessionStart at tick={startTick}, seed={CurrentSessionSeed}, inputDelay={InputDelayTicks}!");
        }

        private static void OnLocalCommandCaptured(NetCommand cmd)
        {
            if (IsGameplayInputLocked)
            {
                ModLogger.Warn($"[CoopSessionManager] Dropped local command {cmd?.Type} during sync barrier.");
                return;
            }

            if (!IsSessionActive || !IsInSyncedLevel)
            {
                // Single-player deterministic test mode: execute immediately
                CommandExecutor.Execute(cmd);
                return;
            }

            if (cmd != null && cmd.TraceId == 0)
            {
                cmd.TraceId = ++_nextTraceSeq;
                if (cmd.TraceId == 0) cmd.TraceId = ++_nextTraceSeq;
                lock (_localCmdSentTs)
                {
                    if (_localCmdSentTs.Count > 512) _localCmdSentTs.Clear();
                    _localCmdSentTs[cmd.TraceId] = Stopwatch.GetTimestamp();
                }
            }

            if (Transport.IsHost)
            {
                lock (_hostTurnBuffer)
                {
                    if (!TryMergeIntoTailCommand(_hostTurnBuffer, cmd))
                        _hostTurnBuffer.Add(cmd);
                }
            }
            else
            {
                lock (_localOutgoingQueue)
                {
                    if (!TryMergeIntoTailCommand(_localOutgoingQueue, cmd))
                        _localOutgoingQueue.Add(cmd);
                }
                if (Transport != null && Transport.IsActive)
                    FlushOutgoingClientCommands();
            }
        }

        private static bool TryMergeIntoTailCommand(List<NetCommand> list, NetCommand incoming)
        {
            if (list == null || list.Count == 0 || incoming == null) return false;
            if (incoming.Type != NetCommandType.ExecuteCommand) return false;
            if (incoming.ExecutorEntityIds == null || incoming.ExecutorEntityIds.Length == 0) return false;

            var prev = list[list.Count - 1];
            if (prev == null || prev.Type != NetCommandType.ExecuteCommand) return false;
            if (prev.PlayerId != incoming.PlayerId
                || prev.CommandId != incoming.CommandId
                || prev.IsShiftQueued != incoming.IsShiftQueued
                || prev.TargetEntityId != incoming.TargetEntityId
                || prev.TargetPosition != incoming.TargetPosition
                || prev.TargetSize != incoming.TargetSize
                || prev.TargetOrigin != incoming.TargetOrigin
                || prev.ExecutorEntityIds == null
                || prev.ExecutorEntityIds.Length == 0)
            {
                return false;
            }

            var merged = new List<ulong>(prev.ExecutorEntityIds.Length + incoming.ExecutorEntityIds.Length);
            merged.AddRange(prev.ExecutorEntityIds);
            for (int i = 0; i < incoming.ExecutorEntityIds.Length; i++)
            {
                ulong id = incoming.ExecutorEntityIds[i];
                if (!merged.Contains(id))
                    merged.Add(id);
            }
            prev.ExecutorEntityIds = merged.ToArray();
            return true;
        }

        /// <summary>
        /// Call once both peers are inside the same synced level. Installs the turn-input gate and
        /// lets the simulation run the first InputDelayTicks immediately while broadcasts catch up.
        /// </summary>
        public static void EnterSyncedLevel()
        {
            if (!IsSessionActive) return;

            _pendingTurnCommands.Clear();
            _hostSymmetricQueue.Clear();
            CommandInterceptor.ClearPendingLocalBuilds();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            _gateWaitStartTicks = -1.0;
            _desyncDumped = false;
            _firstChecksumPosDumped = false;
            _networkGate = new TurnInputTickGate();
            LockstepClock.TickGate = _networkGate;

            int cur = DXGame.Current?.IAIterationInt ?? 0;
            int initialDelay = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, InputDelayTicks));
            _networkGate.MarkReady(cur + initialDelay);
            _hostLastBroadcastSimTick = cur;
            _hostLastBroadcastTargetTick = cur + initialDelay;
            bool isFirstEntry = !_hasEnteredSyncedLevelInMatch;
            _hasEnteredSyncedLevelInMatch = true;
            IsInSyncedLevel = true;
            CommandInterceptor.IsInterceptionEnabled = true;
            RoomPhase = LobbyRoomPhase.InGame;
            HostActionHint = string.Empty;
            if (Transport.IsHost)
            {
                SyncedAdaptivePhysicsIterations = TestHarnessConfig.AdaptivePhysicsIterations;
                BroadcastLobbyRoster();
            }
            LobbyStateChanged?.Invoke();

            // The save loader leaves DXGame.Paused/GameSpeed at 0; clear it so engine animations and
            // the fixed-step clock start running. Session pauses are tracked separately.
            LockstepClock.SetSessionPause(false);
            LockstepClock.ResetHostTickPacing();
            if (DXGame.Current != null)
            {
                if (DXGame.Current.Paused) DXGame.Current.Paused = false;
                if (DXGame.Current.GameSpeed <= 0.0) DXGame.Current.GameSpeed = 1.0;
                if (!DXGame.Current.InputEnabled)
                {
                    DXGame.Current.InputEnabled = true;
                    ModLogger.Info("[CoopSessionManager] Restored DXGame.InputEnabled=true on synced level entry.");
                }
            }
            if (GameBridge.CurrentGameSystem is DXSystem activeSys && !activeSys.Enabled)
            {
                activeSys.Enabled = true;
                ModLogger.Info("[CoopSessionManager] Re-enabled CurrentGameSystem on synced level entry.");
            }
            GameBridge.ClearWaitCursor();
            GameBridge.ClearLoadingFadeOverlay();

            if (IsMultiFactionActive)
            {
                FactionOwnershipTracker.InstallControlFilterIfNeeded();
                if (isFirstEntry)
                {
                    FactionOwnershipTracker.CenterLocalCameraOnOwnBase(selectCc: true);
                }
                else
                {
                    var myCc = FactionOwnershipTracker.GetPrimaryCommandCenterForPlayer(CommandInterceptor.LocalPlayerId);
                    if (myCc != null)
                        GameBridge.SetLevelSysCurrentCommandCenter(myCc);
                }
                if (FactionContextManager.IsInitialized)
                    FactionContextManager.RecalculateAllFactions();
            }

            if (!isFirstEntry)
            {
                // Anchor resync cooldown AFTER in-game reload finishes so export/transfer/InitLevel time
                // never eats up the cooldown window and triggers a 10-20s reload loop.
                _lastAutoResyncTicks = DXHelper_Environment.Ticks;
                _consecutivePosExceeded = 0;
                ResetTieredResyncState();
            }

            ModLogger.Info($"[CoopSessionManager] Entered synced level at tick {cur} (inputDelay={InputDelayTicks}, role={(Transport.IsHost ? "Host" : "Client")}).");
        }

        /// <summary>
        /// Executes every buffered turn command for ticks &lt;= nextSimTick, in ascending tick order.
        /// Called from the deterministic clock prefix at the moment a tick is allowed to advance.
        /// Must not touch local selection state (see CommandExecutor).
        /// </summary>
        public static void FlushDueTurnCommands(int nextSimTick)
        {
            while (true)
            {
                List<NetCommand> due = null;
                lock (_pendingTurnCommands)
                {
                    if (_pendingTurnCommands.Count == 0) return;
                    var first = _pendingTurnCommands.GetEnumerator();
                    if (!first.MoveNext() || first.Current.Key > nextSimTick) return;
                    due = first.Current.Value;
                    _pendingTurnCommands.Remove(first.Current.Key);
                }

                foreach (var cmd in due)
                {
                    if (cmd != null && cmd.TraceId != 0 && Transport != null && cmd.PlayerId == Transport.LocalPlayerId)
                    {
                        long sentTs = 0;
                        bool found = false;
                        lock (_localCmdSentTs)
                        {
                            if (_localCmdSentTs.TryGetValue(cmd.TraceId, out sentTs))
                            {
                                _localCmdSentTs.Remove(cmd.TraceId);
                                found = true;
                            }
                        }
                        if (found && sentTs > 0)
                        {
                            double e2eMs = (Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency;
                            if (e2eMs >= 0.0 && e2eMs < 30000.0)
                            {
                                lock (_cmdE2ELatencySamplesMs)
                                {
                                    if (_cmdE2ELatencySamplesMs.Count >= 256)
                                        _cmdE2ELatencySamplesMs.RemoveAt(0);
                                    _cmdE2ELatencySamplesMs.Add(e2eMs);
                                }
                            }
                        }
                    }
                    RecordDeterministicCommandOwnership(cmd, nextSimTick);
                    CommandExecutor.Execute(cmd);
                }
            }
        }

        private static void RecordDeterministicCommandOwnership(NetCommand cmd, int simTick)
        {
            if (cmd == null || cmd.PlayerId == 0 || cmd.ExecutorEntityIds == null || cmd.ExecutorEntityIds.Length == 0)
                return;
            if (cmd.Type != NetCommandType.GroupMoveOrPatrol
                && cmd.Type != NetCommandType.ExecuteCommand
                && cmd.Type != NetCommandType.CancelCurrentOrder)
                return;

            var game = DXGame.Current;
            if (game == null) return;
            Type movableType = GameBridge.TypeCMovable;

            lock (_unitOwnershipSync)
            {
                if (_unitOwnership.Count > 512)
                {
                    var stale = new List<ulong>();
                    foreach (var kv in _unitOwnership)
                    {
                        int age = simTick - kv.Value.LastOrderTick;
                        if (age < 0 || age > OwnershipLeaseTicks)
                            stale.Add(kv.Key);
                    }
                    for (int i = 0; i < stale.Count; i++)
                        _unitOwnership.Remove(stale[i]);
                }

                for (int i = 0; i < cmd.ExecutorEntityIds.Length; i++)
                {
                    ulong id = cmd.ExecutorEntityIds[i];
                    DXEntity e = game.GetEntityByID(id);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;
                    if (!GameBridge.IsPlayerEntity(e)) continue;
                    if (movableType != null && !GameBridge.HasComponent(e, movableType)) continue;
                    _unitOwnership[id] = new UnitOwnershipEntry { PlayerId = cmd.PlayerId, LastOrderTick = simTick };
                }
            }
        }

        private static bool TryGetActiveUnitOwner(ulong entityId, int curTick, DXEntity entity, out byte ownerPlayerId)
        {
            ownerPlayerId = 0;
            if (entity == null || entity.Disposed) return false;

            UnitOwnershipEntry entry;
            lock (_unitOwnershipSync)
            {
                if (!_unitOwnership.TryGetValue(entityId, out entry))
                    return false;
            }

            int age = curTick - entry.LastOrderTick;
            if (age < 0 || age > OwnershipLeaseTicks)
            {
                lock (_unitOwnershipSync) _unitOwnership.Remove(entityId);
                return false;
            }

            if (age > OwnershipIdleGraceTicks && !GameBridge.IsEntityActivelyMoving(entity))
            {
                lock (_unitOwnershipSync) _unitOwnership.Remove(entityId);
                return false;
            }

            ownerPlayerId = entry.PlayerId;
            return ownerPlayerId > 0;
        }

        private static List<OwnedUnitPosSample> CollectClientOwnedActiveUnits(int curTick, List<StateHasher.EntityPosSample> dynamicPositions)
        {
            if (Transport == null || Transport.IsHost || !EnableAsyncPosAlign || dynamicPositions == null || dynamicPositions.Count == 0)
                return null;
            var game = DXGame.Current;
            if (game == null) return null;

            byte localPid = Transport.LocalPlayerId;
            if (localPid <= 1) return null;

            Dictionary<ulong, UnitOwnershipEntry> ownedSnapshot = null;
            List<ulong> expired = null;

            lock (_unitOwnershipSync)
            {
                if (_unitOwnership.Count == 0) return null;
                foreach (var kv in _unitOwnership)
                {
                    int age = curTick - kv.Value.LastOrderTick;
                    if (age < 0 || age > OwnershipLeaseTicks)
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }
                    if (kv.Value.PlayerId != localPid) continue;

                    DXEntity e = game.GetEntityByID(kv.Key);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic)
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }
                    if (age > OwnershipIdleGraceTicks && !GameBridge.IsEntityActivelyMoving(e))
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }

                    if (ownedSnapshot == null) ownedSnapshot = new Dictionary<ulong, UnitOwnershipEntry>(16);
                    ownedSnapshot[kv.Key] = kv.Value;
                }

                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                        _unitOwnership.Remove(expired[i]);
                }
            }

            if (ownedSnapshot == null || ownedSnapshot.Count == 0)
                return null;

            var result = new List<OwnedUnitPosSample>(Math.Min(ownedSnapshot.Count, MaxReverseAlignUnitsPerReport));
            for (int i = 0; i < dynamicPositions.Count && result.Count < MaxReverseAlignUnitsPerReport; i++)
            {
                var s = dynamicPositions[i];
                if (!s.IsDynamic || !ownedSnapshot.ContainsKey(s.Id)) continue;
                result.Add(new OwnedUnitPosSample
                {
                    Id = s.Id,
                    X = s.X,
                    Y = s.Y
                });
            }

            return result.Count > 0 ? result : null;
        }

        private static void MonitorGateWait()
        {
            if (_levelEndedSynced || GameBridge.IsLevelEnded())
            {
                _gateWaitStartTicks = -1.0;
                return;
            }

            int cur = DXGame.Current?.IAIterationInt ?? 0;
            double now = DXHelper_Environment.Ticks;

            if (now <= _clientHostPauseKeepaliveUntilTicks)
            {
                _gateWaitStartTicks = -1.0;
                if (now - _gateLastWarnTicks >= 1500.0)
                {
                    _gateLastWarnTicks = now;
                    if (_clientHostInModalPause)
                        GameBridge.ShowTopBanner("Host paused (dialog/pause open)...", Color.LightSkyBlue, 900);
                }
                return;
            }

            if (cur != _gateLastObservedTick)
            {
                _gateLastObservedTick = cur;
                _gateWaitStartTicks = -1.0;
            }

            if (_networkGate.IsTickReady(cur + 1))
            {
                _gateWaitStartTicks = -1.0;
                return;
            }

            if (_gateWaitStartTicks < 0.0)
            {
                _gateWaitStartTicks = now;
                return;
            }

            if (now - _gateWaitStartTicks >= GateTimeoutMs && now - _gateLastWarnTicks >= 1000.0)
            {
                _gateLastWarnTicks = now;
                double waited = (now - _gateWaitStartTicks) / 1000.0;
                ModLogger.Warn($"[CoopSessionManager] Tick gate waiting {waited:F1}s at tick {cur} — packets from Host are delayed or lost.");
                if (waited >= GateTimeoutMs / 1000.0)
                    GameBridge.ShowTopBanner("Waiting for Host network...", Color.Orange, 1200);
            }
        }

        /// <summary>Called from LevelLoadCompletePatch on the main thread after a level fully loads.</summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsSessionActive) return;

            if (Transport.IsHost)
            {
                // Freeze immediately at 0 elapsed ticks until snapshot export and client handshake complete.
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;
                if (_hostSnapshotReloadPending && _hostWaitingOwnReload)
                {
                    FactionOwnershipTracker.TryLoadFromLevelScriptVariables();
                    RestoreLocalControlGroupsAfterMidGameReload();
                    _hostWaitingOwnReload = false;
                    ModLogger.InfoImmediate($"[CoopSessionManager] Host symmetric snapshot reload finished at tick={DXGame.Current?.IAIterationInt ?? 0} (clientAck={_clientSnapshotAckReceived}).");
                    byte[] savBytes = _pendingBroadcastSavBytes;
                    _pendingBroadcastSavBytes = null;
                    if (savBytes != null && Transport != null && Transport.IsHost)
                    {
                        string chkText = _pendingBroadcastChkText ?? string.Empty;
                        _pendingBroadcastChkText = null;
                        BroadcastSnapshotChunksToClients(savBytes, chkText, _pendingBroadcastSeed, _pendingBroadcastIdCounter);
                    }
                    else if (_awaitingClientSnapshotAck)
                    {
                        // Refresh timeout start after Host finishes its own parallel reload so Client gets the full ack window.
                        _awaitingClientAckStartTicks = DXHelper_Environment.Ticks;
                    }
                    TryCompleteHostSyncBarrier();
                    return;
                }
                if (!IsInSyncedLevel && !_hostSnapshotReloadPending)
                {
                    SetHostRoomPhase(LobbyRoomPhase.HostLoadingLevel, "正在同步关卡...");
                    RequestSnapshotBroadcast();
                }
                return;
            }

            if (!_awaitingSnapshotLoad) return;

            _awaitingSnapshotLoad = false;
            FactionOwnershipTracker.TryLoadFromLevelScriptVariables();
            RestoreLocalControlGroupsAfterMidGameReload();
            IsInSyncedLevel = false;
            LockstepClock.SetSessionPause(true);
            if (DXGame.Current != null) DXGame.Current.InputEnabled = false;
            int clientTick = DXGame.Current?.IAIterationInt ?? 0;
            Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.SnapshotLoadedAck, w =>
            {
                w.Write(clientTick);
            });
            ModLogger.Info($"[CoopSessionManager] Client finished loading snapshot at tick={clientTick}; sent SnapshotLoadedAck and waiting for SessionStart. {GameBridge.GetHudDiagSummary()}");
        }

        /// <summary>Records the snapshot seed so the post-load calibration uses the Host's seed.</summary>
        public static void RequestSnapshotCalibration(int seed, long entityIdCounter = 0)
        {
            _awaitingSnapshotLoad = true;
            LockstepClock.EnableDeterministicSandbox(seed);
            LockstepClock.RequestPostLoadCalibration(seed, entityIdCounter);
        }

        private static void TryHandlePendingSteamInvite()
        {
            if (GameBridge.CurrentLevelState != null || GameBridge.IsGameLoading()) return;
            string inviteLobbyId = SteamP2PTransport.ConsumePendingInviteLobbyId();
            if (string.IsNullOrEmpty(inviteLobbyId)) return;

            ModLogger.Info($"[CoopSessionManager] Auto-joining invited Steam Lobby {inviteLobbyId} on Main Menu...");
            try
            {
                JoinSession(false, inviteLobbyId);
                ShouldOpenLobbyOnMainMenu = true;
                CoopLobbyWindow.ShowLobbyWindow();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CoopSessionManager] Failed to auto-join invited Steam Lobby {inviteLobbyId}", ex);
                StopSession();
            }
        }

        public static void OnFrameTick()
        {
            var frameGame = DXGame.Current;
            if (frameGame == null || frameGame.Disposing)
                return;

            if (!TestHarnessConfig.Enabled && !TestHarnessConfig.ControlEnabled)
            {
                SteamP2PTransport.PumpSteamCallbacksIfNeeded(Transport);
                TryHandlePendingSteamInvite();
            }

            if (_pendingClientReturnToMainMenu)
            {
                _pendingClientReturnToMainMenu = false;
                if (GameBridge.CurrentLevelState != null)
                {
                    ModLogger.Info("[CoopSessionManager] Host returned to lobby -> returning Client to Main Menu...");
                    _suppressReturnNotice = true;
                    IsInSyncedLevel = false;
                    CommandInterceptor.IsInterceptionEnabled = false;
                    CommandInterceptor.ClearPendingLocalBuilds();
                    LockstepClock.SetSessionPause(false);
                    GameBridge.ReturnToMainMenu();
                    return;
                }
            }

            CoopLobbyWindow.Tick();
            UI.MultiFactionPanel.Tick();
            UI.ManualSyncButton.Tick();
            UI.AllyOverlayRenderer.UpdateUnitFactionRings();

            if (!string.IsNullOrEmpty(_pendingVersionMismatchAlert)
                && !TestHarnessConfig.Enabled
                && !TestHarnessConfig.ControlEnabled
                && !GameBridge.IsGameLoading())
            {
                string alertMsg = _pendingVersionMismatchAlert;
                _pendingVersionMismatchAlert = null;
                try
                {
                    GameBridge.ShowAlertMessageBox("TABCoopMod 版本不一致警告", alertMsg);
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[CoopSessionManager] Failed to show version mismatch alert modal: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(_pendingSteamLobbyErrorAlert)
                && !TestHarnessConfig.Enabled
                && !TestHarnessConfig.ControlEnabled
                && !GameBridge.IsGameLoading())
            {
                string lobbyErrMsg = _pendingSteamLobbyErrorAlert;
                _pendingSteamLobbyErrorAlert = null;
                try
                {
                    GameBridge.ShowAlertMessageBox("Steam 大厅创建/加入失败", lobbyErrMsg);
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[CoopSessionManager] Failed to show Steam lobby error alert modal: {ex.Message}");
                }
            }

            if (!IsSessionActive || Transport == null) return;

            // Deferred, top-of-frame (shallow stack) work: export never runs inside the loader
            // postfix; client load never runs inside packet delivery.
            if (_pendingHostSnapshotExport)
            {
                _pendingHostSnapshotExport = false;
                BroadcastCurrentLevelSnapshot();
            }
            if (_pendingClientSnapshotLoadPath != null
                && GameBridge.CurrentGameSystem != null
                && !GameBridge.IsGameLoading()
                && (GameBridge.CurrentLevelState != null || !GameBridge.IsStartupFadeActive()))
            {
                string path = _pendingClientSnapshotLoadPath;
                _pendingClientSnapshotLoadPath = null;
                BeginClientSnapshotLoad(path);
            }

            Transport?.PollEvents();
            ProbeNetworkRtt();

            // Keep the engine's loader fades advancing while the Client waits to enter the level.
            TickLoadingFade();

            if (!IsSessionActive || Transport == null || DXGame.Current == null || DXGame.Current.Disposing)
                return;

            // Keep Host paused and hold turn broadcasts while waiting for Host reload + Client ack.
            if (Transport.IsHost && (_hostWaitingOwnReload || _awaitingClientSnapshotAck))
            {
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null && DXGame.Current.InputEnabled) DXGame.Current.InputEnabled = false;
                if (!_hostWaitingOwnReload && _awaitingClientAckStartTicks > 0.0 && DXHelper_Environment.Ticks - _awaitingClientAckStartTicks > 60000.0)
                {
                    ModLogger.Warn("[CoopSessionManager] Timed out (60s) waiting for Client SnapshotLoadedAck; resuming Host.");
                    _hostSnapshotReloadPending = false;
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _clientSnapshotAckReceived = false;
                    EnterSyncedLevel();
                }
                else
                {
                    return;
                }
            }

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || GameBridge.CurrentLevelState == null)
                return;

            DrainAsyncQueues();

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || GameBridge.CurrentLevelState == null)
                return;

            // 1. Flush Client outgoing commands to Host
            if (!Transport.IsHost)
                FlushOutgoingClientCommands();

            // 2. Host broadcasts TurnInputBroadcast while paused (session or engine/modal pause) or before
            //    first tick from OnFrameTick; during active sim turns are sealed in OnSimulationTickStarting.
            if (Transport.IsHost)
            {
                if (!_levelEndedSynced && GameBridge.IsLevelEnded())
                    NotifyHostLevelEnded(GameBridge.GetLevelEndStatus() == 1);

                var gx = DXGame.Current;
                bool hostEnginePaused = gx != null && gx.Paused;
                if (!_levelEndedSynced && (hostEnginePaused || LockstepClock.PausedBySession || _hostLastBroadcastSimTick < 0))
                {
                    int cur = gx?.IAIterationInt ?? 0;
                    EmitHostTurns(cur);
                    if (hostEnginePaused || LockstepClock.PausedBySession)
                    {
                        double nowKeep = DXHelper_Environment.Ticks;
                        if (nowKeep - _hostLastPauseKeepaliveSentTicks >= 250.0 || nowKeep < _hostLastPauseKeepaliveSentTicks)
                        {
                            _hostLastPauseKeepaliveSentTicks = nowKeep;
                            bool inModal = GameBridge.IsAnyModalOpen();
                            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.HostPauseKeepalive, w =>
                            {
                                w.Write(cur);
                                w.Write(inModal);
                                w.Write(LockstepClock.PausedBySession);
                            });
                        }
                    }
                }
            }
            else
            {
                MonitorGateWait();
            }

            LogPeriodicLatencyStats();

            // While paused (by session or local modal pause such as Mayor selection), UpdateGameLogic
            // is frozen, so flush any authorized turn commands stamped <= pausedTick on the main thread
            // so a peer's Mayor pick can close this machine's open Mayor modal immediately.
            bool isLocalEnginePaused = DXGame.Current != null && DXGame.Current.Paused;
            if ((LockstepClock.PausedBySession || isLocalEnginePaused)
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsLevelEnded()
                && GameBridge.IsCurrentLevelSystemEnabled())
            {
                int pausedTick = DXGame.Current?.IAIterationInt ?? 0;
                if (_networkGate == null || _networkGate.IsTickReady(pausedTick))
                    LockstepClock.RunInDeterministicContext(() => FlushDueTurnCommands(pausedTick));
            }

            // 3. Broadcast Ally Cursor & Camera Viewport at 20Hz when moving, 4Hz heartbeat when idle
            double nowTicks = DXHelper_Environment.Ticks;
            if (nowTicks - _lastCursorCamPacketMs >= 50.0 && DXScene.Current != null)
            {
                var camArea = DXScene.Current.CameraArea;
                var mouseWorldPos = GetLocalMouseWorldPos();
                bool movedEnough = Math.Abs(mouseWorldPos.X - _lastSentCursorWorldPos.X) >= 0.05f
                    || Math.Abs(mouseWorldPos.Y - _lastSentCursorWorldPos.Y) >= 0.05f
                    || Math.Abs(camArea.X - _lastSentCamArea.X) >= 0.05f
                    || Math.Abs(camArea.Y - _lastSentCamArea.Y) >= 0.05f
                    || Math.Abs(camArea.Width - _lastSentCamArea.Width) >= 0.05f
                    || Math.Abs(camArea.Height - _lastSentCamArea.Height) >= 0.05f;
                bool heartbeatDue = (nowTicks - _lastCursorCamPacketMs) >= 250.0 || nowTicks < _lastCursorCamPacketMs;

                if (movedEnough || heartbeatDue)
                {
                    _lastCursorCamPacketMs = nowTicks;
                    _lastCursorBroadcastTicks = nowTicks;
                    _lastSentCursorWorldPos = mouseWorldPos;
                    _lastSentCamArea = camArea;

                    int broadcastTick = DXGame.Current?.IAIterationInt ?? 0;
                    if (_useCompactWireProtocol)
                    {
                        Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                        {
                            w.Write((byte)(Transport.LocalPlayerId | 0x80));
                            w.Write(QuantizeCursorCoord(mouseWorldPos.X));
                            w.Write(QuantizeCursorCoord(mouseWorldPos.Y));
                            w.Write(QuantizeCursorCoord(camArea.X));
                            w.Write(QuantizeCursorCoord(camArea.Y));
                            w.Write(QuantizeCursorCoord(camArea.Width));
                            w.Write(QuantizeCursorCoord(camArea.Height));
                            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, broadcastTick));
                        });
                    }
                    else
                    {
                        Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                        {
                            w.Write(Transport.LocalPlayerId);
                            w.Write(mouseWorldPos.X);
                            w.Write(mouseWorldPos.Y);
                            w.Write(camArea.X);
                            w.Write(camArea.Y);
                            w.Write(camArea.Width);
                            w.Write(camArea.Height);
                            w.Write(broadcastTick);
                        });
                    }
                }
            }

            // 4. Initial StateHash baseline on session entry (per-tick epoch steps are driven
            //    sequentially by OnSimulationTickCompleted so multi-tick catchup never skips a tick).
            int entryTick = DXGame.Current?.IAIterationInt ?? 0;
            if (entryTick > 0 && _lastCheckedSimTick < 0)
                RunEpochHashAndChecksum(entryTick);
        }

        public static void NotifyHostLevelEnded(bool won)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return;
            if (_levelEndedSynced)
                return;

            _levelEndedSynced = true;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostInModalPause = false;
            LockstepClock.SetSessionPause(false);
            PauseSyncController.CancelCountdown();

            int tick = DXGame.Current?.IAIterationInt ?? 0;
            ModLogger.Info($"[CoopSessionManager] Host level ended (won={won}, tick={tick}) -> broadcasting PacketType.LevelEnded to clients.");
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LevelEnded, w =>
            {
                w.Write(won);
                w.Write(tick);
            });
        }

        private static void OnClientLevelEndedReceived(bool won, int endTick)
        {
            if (_levelEndedSynced || GameBridge.IsLevelEnded())
                return;

            _levelEndedSynced = true;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostInModalPause = false;
            _gateWaitStartTicks = -1.0;
            LockstepClock.SetSessionPause(false);
            PauseSyncController.CancelCountdown();

            ModLogger.Info($"[CoopSessionManager] Client received LevelEnded(won={won}, endTick={endTick}) from Host -> showing GameEnd modal.");
            IsReceivingRemoteLevelEnd = true;
            try
            {
                if (DXVision.GUI.DXModalScreen.IsInModal)
                    DXVision.GUI.DXModalScreen.CloseAll();
                GameBridge.ShowSyncedGameEnd(won);
            }
            finally
            {
                IsReceivingRemoteLevelEnd = false;
            }
        }

        /// <summary>
        /// Polls transport and flushes pending Client commands right before UpdateGameLogic chooses
        /// how many ticks to advance this frame, reducing Client->Host->Client input roundtrip lag.
        /// </summary>
        public static void PumpNetworkBeforeLogic()
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            if (!Transport.IsHost)
                FlushOutgoingClientCommands();
            Transport.PollEvents();
            ProbeNetworkRtt();
        }

        /// <summary>
        /// Called synchronously right before a simulation tick starts advancing, so Host seals the
        /// turn at the exact tick boundary instead of 17ms early on a render frame.
        /// </summary>
        public static void OnSimulationTickStarting(int nextTick)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            if (Transport.IsHost)
                EmitHostTurns(nextTick);
        }

        /// <summary>
        /// Called synchronously after each single simulation tick finishes during UpdateGameLogic.
        /// Keeps incremental epoch hashing lockstep-aligned even when a slow render frame runs 2..8
        /// ticks to maintain 30Hz game speed.
        /// </summary>
        public static void OnSimulationTickCompleted(int completedTick)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;

            if (Transport.IsHost && (LockstepClock.PausedBySession || Testing.DeterministicReplayVerifier.IsRunning))
                EmitHostTurns(completedTick);

            if (completedTick > 0)
            {
                if (IsMultiFactionActive)
                    WonderCrowdfundManager.TickResearchProgress();
                RunEpochHashAndChecksum(completedTick);
            }
        }

        private static void FlushOutgoingClientCommands()
        {
            List<NetCommand> toSend = null;
            lock (_localOutgoingQueue)
            {
                if (_localOutgoingQueue.Count > 0)
                {
                    toSend = new List<NetCommand>(_localOutgoingQueue);
                    _localOutgoingQueue.Clear();
                }
            }

            if (toSend != null)
            {
                int clientTick = DXGame.Current?.IAIterationInt ?? 0;
                if (IsMultiFactionActive)
                {
                    int symDelay = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, InputDelayTicks));
                    int readyFloor = _networkGate != null ? _networkGate.ReadyThrough + 1 : clientTick + symDelay;
                    int symTick = Math.Max(clientTick + symDelay, readyFloor);
                    foreach (var c in toSend)
                    {
                        if (c.TargetSimTick < symTick)
                            c.TargetSimTick = symTick;
                    }
                    Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.SymmetricTurnInput, w =>
                    {
                        w.Write((ushort)toSend.Count);
                        foreach (var c in toSend) c.WriteTo(w);
                        w.Write(clientTick);
                    });
                }
                else
                {
                    Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.ClientInputReport, w =>
                    {
                        w.Write((ushort)toSend.Count);
                        foreach (var c in toSend) c.WriteTo(w);
                        w.Write(clientTick);
                    });
                }
            }
        }

        private static int CompareSymmetricTurnCommand(NetCommand a, NetCommand b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int t = a.TargetSimTick.CompareTo(b.TargetSimTick);
            if (t != 0) return t;
            int p = a.PlayerId.CompareTo(b.PlayerId);
            if (p != 0) return p;
            return a.TraceId.CompareTo(b.TraceId);
        }

        private static void EmitHostTurns(int cur)
        {
            if (!Transport.IsHost || _levelEndedSynced || GameBridge.IsLevelEnded()) return;

            // Use the synchronized pause flag, or engine/modal pause when a game modal opens:
            // during modal pause UpdateGameLogic is frozen so we must not keep advancing targetTick
            // into the future, while still flushing buffered commands and sending HostPauseKeepalive.
            bool paused = LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
            bool hasTurn = false;
            lock (_hostTurnBuffer) hasTurn = _hostTurnBuffer.Count > 0;
            if (!hasTurn && IsMultiFactionActive)
            {
                lock (_hostSymmetricQueue) hasTurn = _hostSymmetricQueue.Count > 0;
            }
            if (paused)
            {
                if (!hasTurn) return;
                BroadcastHostTurn(cur, drainBuffer: true);
                return;
            }

            if (cur <= _hostLastBroadcastSimTick && !hasTurn)
                return;
            if (cur > _hostLastBroadcastSimTick)
                _hostLastBroadcastSimTick = cur;

            int delay = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, InputDelayTicks));
            int desiredTarget = cur + delay;
            if (!hasTurn && desiredTarget <= _hostLastBroadcastTargetTick)
                return;

            int targetTick = desiredTarget > _hostLastBroadcastTargetTick
                ? desiredTarget
                : _hostLastBroadcastTargetTick + 1;

            if (_hostLastBroadcastTargetTick >= 0)
            {
                for (int fillTick = _hostLastBroadcastTargetTick + 1; fillTick < targetTick; fillTick++)
                {
                    BroadcastHostTurn(fillTick, drainBuffer: false);
                }
            }
            if (targetTick > _hostLastBroadcastTargetTick)
                _hostLastBroadcastTargetTick = targetTick;

            BroadcastHostTurn(targetTick, drainBuffer: true);
        }

        private static void BroadcastHostTurn(int targetTick, bool drainBuffer)
        {
            List<NetCommand> turnCmds = null;
            if (drainBuffer)
            {
                lock (_hostTurnBuffer)
                {
                    if (_hostTurnBuffer.Count > 0)
                    {
                        turnCmds = new List<NetCommand>(_hostTurnBuffer);
                        _hostTurnBuffer.Clear();
                    }
                }
            }

            if (IsMultiFactionActive)
            {
                lock (_hostSymmetricQueue)
                {
                    if (_hostSymmetricQueue.Count > 0)
                    {
                        var dueKeys = new List<int>();
                        foreach (var kv in _hostSymmetricQueue)
                        {
                            if (kv.Key <= targetTick)
                            {
                                (turnCmds ?? (turnCmds = new List<NetCommand>())).AddRange(kv.Value);
                                dueKeys.Add(kv.Key);
                            }
                        }
                        for (int i = 0; i < dueKeys.Count; i++)
                            _hostSymmetricQueue.Remove(dueKeys[i]);
                    }
                }
            }

            if (turnCmds != null)
            {
                foreach (var c in turnCmds) c.TargetSimTick = targetTick;
                if (IsMultiFactionActive && turnCmds.Count > 1)
                    turnCmds.Sort(CompareSymmetricTurnCommand);

                lock (_pendingTurnCommands)
                {
                    if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                        _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                    slot.AddRange(turnCmds);
                    if (IsMultiFactionActive && slot.Count > 1)
                        slot.Sort(CompareSymmetricTurnCommand);
                }
            }

            _networkGate.MarkReady(targetTick);

            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TurnInputBroadcast, w =>
            {
                w.Write(targetTick);
                w.Write((ushort)(turnCmds?.Count ?? 0));
                if (turnCmds != null)
                    foreach (var c in turnCmds) c.WriteTo(w);
            });
        }

        private static void ProbeNetworkRtt()
        {
            if (Transport == null || !Transport.IsHost || !HasJoinedClient) return;
            if (LockstepClock.LoadingLevel) return;
            bool inSyncedGame = IsInSyncedLevel && GameBridge.CurrentLevelState != null;
            bool inLobby = RoomPhase == LobbyRoomPhase.InLobby || RoomPhase == LobbyRoomPhase.HostSelectingMenu;
            if (!inSyncedGame && !inLobby) return;

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRttProbeTicks < RttProbeIntervalMs && now >= _lastRttProbeTicks)
                return;

            _lastRttProbeTicks = now;
            uint seq = ++_rttPingSeq;
            long timestamp = Stopwatch.GetTimestamp();
            float hostSmoothedRtt = (float)CurrentSmoothedRttMs;
            Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.RttPing, w =>
            {
                w.Write(seq);
                w.Write(timestamp);
                w.Write(hostSmoothedRtt);
            });
        }

        private static void UpdateAdaptiveInputDelay()
        {
            if (Transport == null || !Transport.IsHost) return;

            double now = DXHelper_Environment.Ticks;
            double maxRtt = -1.0;
            foreach (var p in GetEffectivePeers())
            {
                if (p.PlayerId == 1) continue;
                if (_peerSmoothedRttMs.TryGetValue(p.PlayerId, out double rtt)
                    && _peerRttSampleTicks.TryGetValue(p.PlayerId, out double sampleTs)
                    && (now - sampleTs <= RttStalePeerMs || now < sampleTs))
                {
                    if (rtt > maxRtt) maxRtt = rtt;
                }
            }

            if (maxRtt < 0.0)
            {
                CurrentSmoothedRttMs = 0.0;
                return;
            }

            // When background DirectFastPath UDP is verified bidirectional, prefer its wire RTT for
            // InputDelayTicks sizing so slower Client render frames never inflate InputDelayTicks 1 -> 2..3.
            double effectiveDelayRtt = maxRtt;
            if (Transport is ITransportDiagnostics diag && diag.ActiveDirectRttMs > 0.0)
            {
                effectiveDelayRtt = diag.ActiveDirectRttMs;
            }

            CurrentSmoothedRttMs = maxRtt;

            int cur = InputDelayTicks;
            int desired = cur;
            if (effectiveDelayRtt > 145.0) desired = 4;
            else if (effectiveDelayRtt > 100.0) desired = (cur == 4 && effectiveDelayRtt >= 122.0) ? 4 : 3;
            else if (effectiveDelayRtt > 64.0) desired = (cur >= 3 && effectiveDelayRtt >= 82.0) ? 3 : 2;
            else if (cur == 2 && effectiveDelayRtt >= 50.0) desired = 2;
            else desired = 1;

            desired = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, desired));
            if (desired == cur) return;
            if (now - _lastDelayAdjustTicks < DelayAdjustCooldownMs && now >= _lastDelayAdjustTicks)
                return;

            _lastDelayAdjustTicks = now;
            InputDelayTicks = desired;
            ModLogger.Info($"[AdaptiveDelay] InputDelayTicks {cur} -> {desired} (delayRtt={effectiveDelayRtt:F1}ms, mainRtt={maxRtt:F1}ms)");
        }

        private static void UpdatePeerSimTick(byte peerId, int simTick)
        {
            if (peerId == 0 || peerId == 1 || simTick < 0) return;
            if (_peerLastSimTick.TryGetValue(peerId, out int prevTick) && simTick < prevTick)
            {
                // Ignore stale out-of-order UDP RttPong samples, unless a snapshot reload reset tick downward.
                if (prevTick - simTick < 300) return;
            }
            _peerLastSimTick[peerId] = simTick;
            _peerLastSimTickWallMs[peerId] = DXHelper_Environment.Ticks;
        }

        private static bool TryGetSlowestClientLagTicks(out int maxLagTicks)
        {
            maxLagTicks = 0;
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return false;

            int hostTick = DXGame.Current?.IAIterationInt ?? 0;
            double now = DXHelper_Environment.Ticks;
            bool found = false;

            foreach (var p in GetEffectivePeers())
            {
                if (p.PlayerId == 1) continue;
                if (_peerLastSimTick.TryGetValue(p.PlayerId, out int peerTick)
                    && _peerLastSimTickWallMs.TryGetValue(p.PlayerId, out double sampleTs)
                    && now >= sampleTs
                    && (now - sampleTs) <= ClientLagStaleMs)
                {
                    int lag = Math.Max(0, hostTick - peerTick);
                    if (!found || lag > maxLagTicks)
                    {
                        maxLagTicks = lag;
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Backpressure for Host tick pacing so a slower Client cannot fall seconds behind Host
        /// and accumulate multi-second input/pause latency. Bounded by ClientLagStaleMs (3.5s)
        /// so a frozen/disconnected Client can never deadlock Host.
        /// </summary>
        internal static void GetHostPaceAdjustment(out double paceMultiplier, out int maxTicks)
        {
            paceMultiplier = 1.0;
            double gameSpeed = DXGame.Current?.GameSpeed ?? 1.0;
            int speedCeil = gameSpeed > 1.05 ? Math.Max(2, (int)Math.Ceiling(gameSpeed)) : 1;
            maxTicks = speedCeil > 1 ? Math.Min(16, speedCeil + 2) : 3;

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return;
            if (LockstepClock.PausedBySession || _awaitingClientSnapshotAck || _hostWaitingOwnReload
                || Testing.DeterministicReplayVerifier.IsRunning)
                return;
            if (!TryGetSlowestClientLagTicks(out int maxLagTicks))
                return;

            // During heavy final-wave slow ticks (DiagAvgTickLogicMs > 30ms), avoid the abrupt 9t hard-stop
            // stutter by smoothly ramping paceMultiplier up to 2.5x and raising the hard-hold ceiling to 15t.
            bool slowTickRegime = DeterministicSandboxPatches.DiagAvgTickLogicMs > 30.0;
            if (slowTickRegime)
            {
                if (maxLagTicks >= 15)
                {
                    maxTicks = 0;
                }
                else if (maxLagTicks >= 4)
                {
                    maxTicks = 1;
                    paceMultiplier = Math.Min(2.5, 1.15 + (maxLagTicks - 4) * 0.13);
                }
                return;
            }

            // At elevated GameSpeed (>1x fast-forward), one render frame spans multiple sim ticks so RTT +
            // inputDelay produce larger transient tick deltas without indicating Client overload.
            int slowLag = speedCeil > 1 ? Math.Max(HostBackpressureSlowLagTicks, speedCeil * 2) : HostBackpressureSlowLagTicks;
            int midLag = speedCeil > 1 ? Math.Max(6, speedCeil * 3) : 6;
            int holdLag = speedCeil > 1 ? Math.Max(HostBackpressureHoldLagTicks, speedCeil * 4) : HostBackpressureHoldLagTicks;

            if (maxLagTicks >= holdLag)
            {
                maxTicks = 0;
            }
            else if (maxLagTicks >= midLag)
            {
                maxTicks = speedCeil > 1 ? Math.Max(1, speedCeil / 3) : 1;
                paceMultiplier = 1.38;
            }
            else if (maxLagTicks >= slowLag)
            {
                maxTicks = speedCeil > 1 ? Math.Max(2, (speedCeil * 2) / 3) : 2;
                paceMultiplier = 1.15;
            }
        }

        private static void LogPeriodicLatencyStats()
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            double now = DXHelper_Environment.Ticks;
            if (now - _lastLatencyStatsLogTicks < 10000.0 && now >= _lastLatencyStatsLogTicks)
                return;
            _lastLatencyStatsLogTicks = now;

            double avgMs = 0.0, p95Ms = 0.0, maxMs = 0.0;
            int count;
            lock (_cmdE2ELatencySamplesMs)
            {
                count = _cmdE2ELatencySamplesMs.Count;
                if (count > 0)
                {
                    var copy = new List<double>(_cmdE2ELatencySamplesMs);
                    _cmdE2ELatencySamplesMs.Clear();
                    copy.Sort();
                    double sum = 0.0;
                    for (int i = 0; i < copy.Count; i++) sum += copy[i];
                    avgMs = sum / count;
                    int p95Idx = Math.Min(count - 1, (int)Math.Floor((count - 1) * 0.95));
                    p95Ms = copy[p95Idx];
                    maxMs = copy[count - 1];
                }
            }

            var diag = Transport as ITransportDiagnostics;
            string path = diag != null ? diag.ActivePathSummary : Transport.GetType().Name;
            double directRtt = diag != null ? diag.ActiveDirectRttMs : -1.0;
            string statsLine = diag != null ? diag.GetDebugStatsLine() : string.Empty;
            string role = Transport.IsHost ? "Host" : "Client";

            string lagPart = string.Empty;
            if (Transport.IsHost)
            {
                if (TryGetSlowestClientLagTicks(out int cLag))
                    lagPart = $" clientLag={cLag}t({cLag * 33.3:F0}ms)";
            }
            else
            {
                int curTick = DXGame.Current?.IAIterationInt ?? 0;
                int cLag = Math.Max(0, _networkGate.ReadyThrough - curTick);
                lagPart = $" tickLag={cLag}t({cLag * 33.3:F0}ms)";
            }

            ModLogger.Info(
                $"[LatencyStats] role={role} path=\"{path}\" rtt={CurrentSmoothedRttMs:F1}ms directRtt={directRtt:F1}ms " +
                $"delay={InputDelayTicks}t({InputDelayTicks * 33.3:F0}ms){lagPart} cmdN={count} cmdAvg={avgMs:F1}ms cmdP95={p95Ms:F1}ms cmdMax={maxMs:F1}ms" +
                (string.IsNullOrEmpty(statsLine) ? string.Empty : $" | {statsLine}"));
        }

        private static void RunEpochHashAndChecksum(int curTick)
        {
            bool hasDigest = false;
            StateHasher.LevelStateSnapshotDigest digest = default;
            List<StateHasher.EntityPosSample> dynamicPositions = null;
            List<StateHasher.ProdQueueSample> prodQueues = null;

            if (_lastCheckedSimTick < 0)
            {
                dynamicPositions = new List<StateHasher.EntityPosSample>(2048);
                prodQueues = new List<StateHasher.ProdQueueSample>(16);
                digest = StateHasher.ComputeDigest(dynamicPositions, prodQueues);
                _epochHasher.Reset();
                hasDigest = true;
            }
            else if (_epochHasher.Step(curTick, out digest, out dynamicPositions, out prodQueues))
            {
                hasDigest = true;
            }

            if (!hasDigest) return;

            _lastCheckedSimTick = digest.SimTick;

            // Prune stale digests (keep ~40s of history) so dictionaries cannot grow unbounded.
            if (Transport.IsHost)
            {
                if (_hostTickDigests.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var k in _hostTickDigests.Keys)
                        if (k < curTick - 1200) stale.Add(k);
                    foreach (var k in stale) _hostTickDigests.Remove(k);
                }
                if (_clientTickDigests.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var k in _clientTickDigests.Keys)
                        if (k < curTick - 1200) stale.Add(k);
                    foreach (var k in stale) _clientTickDigests.Remove(k);
                }
            }

            CacheTickPositions(digest.SimTick, dynamicPositions, in digest, prodQueues);

            long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;
            if (Transport.IsHost)
            {
                _hostTickDigests[digest.SimTick] = new TickDigestRecord { Digest = digest, Summary = null, SyncedIdCounter = idCounter, HasFullDetail = true };
                VerifyTickChecksum(digest.SimTick);
            }
            else
            {
                List<OwnedUnitPosSample> ownedUnits = CollectClientOwnedActiveUnits(digest.SimTick, dynamicPositions);
                _clientEpochDetails[digest.SimTick] = new ClientEpochDetailEntry { Digest = digest, OwnedUnits = ownedUnits };
                _clientEpochDetailOrder.Enqueue(digest.SimTick);
                while (_clientEpochDetailOrder.Count > 8)
                    _clientEpochDetails.Remove(_clientEpochDetailOrder.Dequeue());

                Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumReport, w =>
                {
                    if (_useCompactWireProtocol)
                        WriteStage1DigestReport(w, in digest, ownedUnits, idCounter);
                    else
                        WriteDigestReport(w, in digest, FormatDigestSummary(in digest), ownedUnits, idCounter);
                });
            }
        }

        private static PointF GetLocalMouseWorldPos()
        {
            var levelSys = GameBridge.LevelSystemInstance;
            if (levelSys == null) return PointF.Empty;
            var field = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_MouseWorldPos);
            return field != null ? (PointF)field.GetValue(levelSys) : PointF.Empty;
        }

        /// <summary>
        /// Triggers a tactical ping locally and broadcasts it to all session peers.
        /// Pings are purely visual UI signals (not deterministic world mutations) so they travel over
        /// PacketType.TacticalPing immediately without consuming a lockstep command slot.
        /// </summary>
        public static void SendTacticalPing(Point cell, string label = null)
        {
            byte pid = Transport != null ? Transport.LocalPlayerId : CommandInterceptor.LocalPlayerId;
            string effectiveLabel = string.IsNullOrEmpty(label) ? $"Player {pid}" : label;

            AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, cell, effectiveLabel);

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;

            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TacticalPing, w =>
            {
                w.Write(pid);
                w.Write(cell.X);
                w.Write(cell.Y);
                w.Write(effectiveLabel);
            });
        }

        private static void OnPacketReceived(byte senderId, PacketType pktType, BinaryReader r)
        {
            switch (pktType)
            {
                case PacketType.LobbyRosterSync:
                {
                    if (Transport.IsHost) break;
                    var phase = (LobbyRoomPhase)r.ReadByte();
                    string hint = r.ReadString();
                    byte count = r.ReadByte();
                    _lobbyRoster.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        _lobbyRoster.Add(new PeerInfo
                        {
                            PlayerId = r.ReadByte(),
                            PlayerName = r.ReadString(),
                            SteamId64 = r.ReadUInt64(),
                            IsReady = r.ReadBoolean()
                        });
                    }
                    RoomPhase = phase;
                    HostActionHint = hint;
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        BalancePreset = (CoopBalancePreset)r.ReadByte();
                    }
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        bool hasChaos = r.ReadBoolean();
                        if (hasChaos && r.BaseStream.Position + 13 <= r.BaseStream.Length)
                        {
                            bool chaosOn = r.ReadBoolean();
                            int lat = r.ReadInt32();
                            int jit = r.ReadInt32();
                            float loss = r.ReadSingle();
                            if (!TestHarnessConfig.HasChaos && IsLocalLoopbackSession)
                            {
                                LocalChaosEnabled = chaosOn;
                                LocalChaosLatencyMs = Math.Max(0, Math.Min(500, lat));
                                LocalChaosJitterMs = Math.Max(0, Math.Min(300, jit));
                                LocalChaosLossRate = Math.Round(Math.Max(0.0, Math.Min(0.30, (double)loss)), 3);
                                if (Transport is ChaosTransport chaos)
                                {
                                    chaos.UpdateConfig(BuildLocalChaosConfig());
                                }
                            }
                        }
                    }
                    if (r.BaseStream.Position + 2 <= r.BaseStream.Length)
                    {
                        GameMode = (CoopGameMode)r.ReadByte();
                        DefeatRule = (MultiFactionDefeatRule)r.ReadByte();
                    }
                    if (r.BaseStream.Position + 1 <= r.BaseStream.Length)
                    {
                        SpawnMode = (MultiFactionSpawnMode)r.ReadByte();
                    }
                    if (r.BaseStream.Position + 1 <= r.BaseStream.Length)
                    {
                        BalanceEnhancementPlayers = Math.Max(0, Math.Min(
                            CoopLimits.MaxBalanceEnhancementPlayers, (int)r.ReadByte()));
                    }
                    Transport.SyncPeers(_lobbyRoster);
                    LobbyStateChanged?.Invoke();
                    break;
                }

                case PacketType.ClientReadyState:
                {
                    if (!Transport.IsHost) break;
                    bool ready = r.ReadBoolean();
                    foreach (var p in _lobbyRoster)
                    {
                        if (p.PlayerId == senderId)
                        {
                            p.IsReady = ready;
                            break;
                        }
                    }
                    BroadcastLobbyRoster();
                    LobbyStateChanged?.Invoke();
                    break;
                }

                case PacketType.LobbyHostNotice:
                {
                    if (Transport.IsHost) break;
                    var noticeType = (LobbyHostNoticeType)r.ReadByte();
                    if (noticeType == LobbyHostNoticeType.ReturnToLobby)
                    {
                        foreach (var p in _lobbyRoster)
                        {
                            if (p.PlayerId != 1) p.IsReady = false;
                        }
                        RoomPhase = LobbyRoomPhase.InLobby;
                        HostActionHint = string.Empty;
                        ShouldOpenLobbyOnMainMenu = true;
                        Transport.SyncPeers(_lobbyRoster);
                        if (GameBridge.CurrentLevelState != null)
                        {
                            _pendingClientReturnToMainMenu = true;
                        }
                        LobbyStateChanged?.Invoke();
                    }
                    break;
                }

                case PacketType.SnapshotBegin:
                {
                    _expectedSaveBytes = r.ReadInt32();
                    _expectedChunks = r.ReadInt32();
                    _incomingCheckContent = r.ReadString();
                    _incomingRandomSeed = r.ReadInt32();
                    _incomingEntityIdCounter = r.ReadInt64();
                    ModLogger.Info($"[CoopSessionManager] Receiving Snapshot from Host ({_expectedSaveBytes:N0} bytes, {_expectedChunks} chunks, seed={_incomingRandomSeed}, idCounter={_incomingEntityIdCounter}); buffered={_incomingChunks.Count}.");
                    GameBridge.ShowTopBanner("Receiving Map Snapshot from Host...", Color.Cyan, 3000);
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotChunk:
                {
                    int chunkIdx = r.ReadInt32();
                    int len = r.ReadInt32();
                    byte[] data = r.ReadBytes(len);
                    // Chunks may arrive before SnapshotBegin and out of order (ChaosTransport jitter
                    // reorders even ReliableOrdered). Buffer by index; assemble once complete.
                    if (chunkIdx >= 0) _incomingChunks[chunkIdx] = data;
                    ModLogger.Info($"[CoopSessionManager] Snapshot chunk {chunkIdx} received ({len:N0} bytes, buffered={_incomingChunks.Count}/{_expectedChunks}).");
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotLoadedAck:
                {
                    if (!Transport.IsHost) break;
                    int clientTick = r.ReadInt32();
                    _clientSnapshotAckReceived = true;
                    ModLogger.Info($"[CoopSessionManager] Received SnapshotLoadedAck from Client({senderId}) at tick={clientTick} (hostWaitingOwnReload={_hostWaitingOwnReload}).");
                    TryCompleteHostSyncBarrier();
                    break;
                }

                case PacketType.SessionStart:
                {
                    if (Transport.IsHost) break;
                    int startTick = r.ReadInt32();
                    int seed = r.ReadInt32();
                    long idCounter = r.ReadInt64();
                    byte hostDelay = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : (byte)MinInputDelayTicks;
                    byte protoFlags = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : (byte)0;
                    ResetSyncTrackingState();
                    _useCompactWireProtocol = (protoFlags & SessionStartProtoFlag_CompactBinary) != 0;
                    SyncedAdaptivePhysicsIterations = (protoFlags & SessionStartProtoFlag_AdaptivePhysIter) != 0;
                    InputDelayTicks = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, (int)hostDelay));
                    EnterSyncedLevel();
                    ModLogger.InfoImmediate($"[CoopSessionManager] Received SessionStart from Host (tick={startTick}, localTick={DXGame.Current?.IAIterationInt ?? 0}, seed={seed}, idCounter={idCounter}, inputDelay={InputDelayTicks}, compact={_useCompactWireProtocol}, mode={EffectiveResyncMode}). Unpausing in lockstep!");
                    break;
                }

                case PacketType.SymmetricTurnInput:
                {
                    if (!Transport.IsHost) break;
                    ushort count = r.ReadUInt16();
                    int curSim = DXGame.Current?.IAIterationInt ?? 0;
                    int minSafeTick = Math.Max(_hostLastBroadcastTargetTick + 1, curSim + 1);
                    lock (_hostSymmetricQueue)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var cmd = NetCommand.ReadFrom(r);
                            cmd.PlayerId = senderId;
                            int safeTick = Math.Max(cmd.TargetSimTick, minSafeTick);
                            cmd.TargetSimTick = safeTick;
                            if (!_hostSymmetricQueue.TryGetValue(safeTick, out var bucket))
                                _hostSymmetricQueue[safeTick] = bucket = new List<NetCommand>();
                            bucket.Add(cmd);
                        }
                    }
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        int clientTick = r.ReadInt32();
                        UpdatePeerSimTick(senderId, clientTick);
                    }
                    break;
                }

                case PacketType.ClientInputReport:
                {
                    if (!Transport.IsHost) break;
                    ushort count = r.ReadUInt16();
                    lock (_hostTurnBuffer)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var cmd = NetCommand.ReadFrom(r);
                            cmd.PlayerId = senderId;
                            _hostTurnBuffer.Add(cmd);
                        }
                    }
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        int clientTick = r.ReadInt32();
                        UpdatePeerSimTick(senderId, clientTick);
                    }
                    break;
                }

                case PacketType.TurnInputBroadcast:
                {
                    if (Transport.IsHost) break;
                    int targetTick = r.ReadInt32();
                    ushort count = r.ReadUInt16();
                    List<NetCommand> cmds = null;
                    for (int i = 0; i < count; i++)
                    {
                        var cmd = NetCommand.ReadFrom(r);
                        (cmds ?? (cmds = new List<NetCommand>())).Add(cmd);
                    }

                    if (cmds != null)
                    {
                        lock (_pendingTurnCommands)
                        {
                            if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                                _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                            slot.AddRange(cmds);
                            if (IsMultiFactionActive && slot.Count > 1)
                                slot.Sort(CompareSymmetricTurnCommand);
                        }
                    }

                    // The heartbeat arrived: allow the simulation to advance up to this tick. The
                    // commands themselves are executed by FlushDueTurnCommands at the tick boundary.
                    _networkGate.MarkReady(targetTick);
                    if (targetTick > _clientHostPausedTick)
                        _clientHostPauseKeepaliveUntilTicks = -1.0;

                    if (cmds != null && cmds.Exists(c => c.Type == NetCommandType.MayorVoteOrSelect))
                    {
                        Patches.MayorSyncPatch.NotifyRemoteMayorCommitted(GameBridge.GetLevelCollectionCount("MayorStates"));
                        Patches.MayorSyncPatch.CloseActiveMayorWindow();
                    }
                    break;
                }

                case PacketType.HostPauseKeepalive:
                {
                    if (Transport.IsHost || _levelEndedSynced || GameBridge.IsLevelEnded()) break;
                    int hostTick = r.ReadInt32();
                    bool hostInModal = r.ReadBoolean();
                    bool hostSessionPaused = r.ReadBoolean();
                    _clientHostPausedTick = hostTick;
                    _clientHostInModalPause = hostInModal || hostSessionPaused;
                    _clientHostPauseKeepaliveUntilTicks = DXHelper_Environment.Ticks + 1500.0;
                    int curClientTick = DXGame.Current?.IAIterationInt ?? 0;
                    if (hostSessionPaused && !LockstepClock.PausedBySession && curClientTick < hostTick)
                    {
                        GameBridge.ShowTopBanner($"Pausing (syncing {hostTick - curClientTick}t)...", Color.LightSkyBlue, 600);
                    }
                    break;
                }

                case PacketType.LevelEnded:
                {
                    if (Transport.IsHost) break;
                    bool won = r.ReadBoolean();
                    int endTick = r.ReadInt32();
                    OnClientLevelEndedReceived(won, endTick);
                    break;
                }

                case PacketType.StateChecksumReport:
                {
                    if (!Transport.IsHost) break;
                    TickDigestRecord rec = _useCompactWireProtocol ? ReadStage1DigestReport(r) : ReadDigestReport(r);
                    int clientTick = rec.Digest.SimTick;
                    UpdatePeerSimTick(senderId, clientTick);
                    if (!_clientTickDigests.TryGetValue(clientTick, out var byPeer))
                        _clientTickDigests[clientTick] = byPeer = new Dictionary<byte, TickDigestRecord>();
                    byPeer[senderId] = rec;
                    if (EnableAsyncPosAlign && rec.OwnedUnits != null && rec.OwnedUnits.Count > 0)
                    {
                        _incomingReverseAlignQueue.Enqueue(new PendingReverseAlignApply
                        {
                            Generation = _asyncGeneration,
                            SenderPlayerId = senderId,
                            Tick = clientTick,
                            Units = rec.OwnedUnits
                        });
                    }
                    VerifyTickChecksum(clientTick);
                    break;
                }

                case PacketType.StateChecksumDetailRequest:
                {
                    if (Transport.IsHost) break;
                    int reqTick = r.ReadInt32();
                    _clientEpochDetails.TryGetValue(reqTick, out var entry);
                    Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumDetailResponse, w =>
                    {
                        WriteStage2DetailResponse(w, reqTick, entry);
                    });
                    break;
                }

                case PacketType.StateChecksumDetailResponse:
                {
                    if (!Transport.IsHost) break;
                    MergeStage2DetailResponse(r, out int mergedTick, senderId);
                    VerifyTickChecksum(mergedTick);
                    break;
                }

                case PacketType.ManualResyncRequest:
                {
                    if (!Transport.IsHost) break;
                    if (LockstepClock.LoadingLevel || GameBridge.IsGameLoading()
                        || _pendingHostSnapshotExport || _awaitingClientSnapshotAck)
                    {
                        GameBridge.ShowTopBanner("同步已在进行中...", Color.Gold, 1600);
                        break;
                    }

                    _lastAutoResyncTicks = DXHelper_Environment.Ticks;
                    ResetTieredResyncState();
                    ModLogger.WarnImmediate($"[CoopSessionManager] Manual resync requested by Client P{senderId}.");
                    GameBridge.ShowTopBanner($"P{senderId} 请求手动同步，正在导出快照...", Color.Cyan, 3000);
                    RequestSnapshotBroadcast();
                    break;
                }

                case PacketType.PosDriftSyncChunk:
                {
                    if (Transport.IsHost) break;
                    OnPosDriftSyncChunkReceived(r);
                    break;
                }

                case PacketType.PosDriftStatsReport:
                {
                    if (!Transport.IsHost) break;
                    int pTick = r.ReadInt32();
                    lock (_driftBaselineLock)
                    {
                        int ackSeq = 0;
                        foreach (var kvp in _hostPendingBaselinesBySeq)
                        {
                            if (kvp.Value.PeerId == senderId && kvp.Value.Tick == pTick && kvp.Value.Seq > ackSeq)
                                ackSeq = kvp.Value.Seq;
                        }
                        if (ackSeq > 0 && _hostPendingBaselinesBySeq.TryGetValue(ackSeq, out var confirmed))
                        {
                            _hostAckedBaselineByPeer[senderId] = (confirmed.Seq, confirmed.Samples);
                            var rm = new List<int>();
                            foreach (var k in _hostPendingBaselinesBySeq.Keys)
                                if (_hostPendingBaselinesBySeq[k].PeerId == senderId && k <= ackSeq) rm.Add(k);
                            for (int ri = 0; ri < rm.Count; ri++) _hostPendingBaselinesBySeq.Remove(rm[ri]);
                        }
                    }
                    bool wasApplied = r.ReadBoolean();
                    int appliedCount = r.ReadInt32();
                    int comparedCount = r.ReadInt32();
                    int drifted001 = r.ReadInt32();
                    int drifted01 = r.ReadInt32();
                    int drifted1 = r.ReadInt32();
                    int drifted5 = r.ReadInt32();
                    float maxDelta = r.ReadSingle();
                    float rmsDelta = r.ReadSingle();
                    bool exceededThreshold = r.ReadBoolean();
                    string maxType = r.ReadString();
                    int lifeDiffCount = r.ReadInt32();
                    int lifeApplied = r.ReadInt32();
                    int killsApplied = r.ReadInt32();
                    int missingOnHost = r.ReadInt32();
                    int missingOnClient = r.ReadInt32();

                    // After host-authoritative snapping (<= MaxAsyncNudgeCells) a residual this wide means
                    // many units are on entirely different paths; only that, sustained, warrants a reload.
                    // Require at least 40 units drifted >0.5 cells (and >1% of compared units in huge swarms)
                    // so a single stuck outlier unit (e.g. 1 Sniper) cannot blow up RMS and trigger a 35s reload.
                    int minWideDrifted5 = Math.Max(40, comparedCount / 100);
                    bool widePosDrift = rmsDelta >= 2.5f && drifted5 >= minWideDrifted5;
                    bool significantPosExceed = exceededThreshold && (drifted5 >= 10 || drifted1 >= 25);
                    bool countsAsPosExceed = IsStrictMode ? (significantPosExceed || widePosDrift) : widePosDrift;
                    if (countsAsPosExceed)
                        _consecutivePosExceeded++;
                    else
                        _consecutivePosExceeded = 0;

                    if (missingOnClient >= 10)
                        _consecutiveMissingOnClient++;
                    else
                        _consecutiveMissingOnClient = 0;

                    _lastClientMissingOnClient = missingOnClient;
                    _lastClientMissingOnHost = missingOnHost;

                    double statsNow = DXHelper_Environment.Ticks;
                    bool notablePosDrift = killsApplied > 0 || lifeApplied > 0
                        || missingOnClient >= 5 || missingOnHost >= 5
                        || maxDelta >= 2.0f || exceededThreshold
                        || _consecutiveMissingOnClient > 0 || _consecutivePosExceeded > 0
                        || ModLogger.IsVerbose;
                    if (notablePosDrift || statsNow < _lastPosDriftStatsLogMs || (statsNow - _lastPosDriftStatsLogMs) >= PosAlignLogIntervalMs)
                    {
                        _lastPosDriftStatsLogMs = statsNow;
                        ModLogger.Info(
                            $"[PosDriftStats] Client({senderId}) Tick={pTick} Applied={wasApplied}({appliedCount}/{comparedCount}) " +
                            $"Drifted(>0.001={drifted001}, >0.01={drifted01}, >0.1={drifted1}, >0.5={drifted5}) " +
                            $"MaxDelta={maxDelta:F4} RmsDelta={rmsDelta:F4} Type={maxType} Exceeded={exceededThreshold} Streak={_consecutivePosExceeded} " +
                            $"LifeDiff={lifeDiffCount} LifeFix={lifeApplied} Kills={killsApplied} MissingOnHost={missingOnHost} MissingOnClient={missingOnClient} MissingStreak={_consecutiveMissingOnClient}");
                    }

                    bool triggerMissingResync = !IsStrictMode && _consecutiveMissingOnClient >= MissingOnClientReloadStreak;
                    bool triggerPosResync = IsStrictMode
                        ? (widePosDrift || _consecutivePosExceeded >= 3)
                        : (_consecutivePosExceeded >= WidePosReloadStreak);
                    if ((triggerPosResync || triggerMissingResync) && EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport)
                    {
                        double now = DXHelper_Environment.Ticks;
                        double requiredCooldown = triggerMissingResync ? StructuralResyncCooldownMs : AutoResyncCooldownMs;
                        if (now - _lastAutoResyncTicks >= requiredCooldown)
                        {
                            _lastAutoResyncTicks = now;
                            _consecutivePosExceeded = 0;
                            _consecutiveMissingOnClient = 0;
                            if (triggerMissingResync)
                                ModLogger.WarnImmediate($"[CoopSessionManager] Auto-resync triggered after MissingOnClient at Tick {pTick} (MissingOnClient={missingOnClient}, MissingOnHost={missingOnHost}).");
                            else
                                ModLogger.WarnImmediate($"[CoopSessionManager] Auto-resync triggered after PosDrift at Tick {pTick} (MaxDelta={maxDelta:F4}, RmsDelta={rmsDelta:F4}, Drifted5={drifted5}).");
                            RequestSnapshotBroadcast();
                        }
                    }
                    break;
                }

                case PacketType.AllyCursorAndCamera:
                {
                    byte hdr = r.ReadByte();
                    bool isCompact = (hdr & 0x80) != 0;
                    byte pid = (byte)(hdr & 0x7F);
                    if (pid == Transport.LocalPlayerId) break;

                    PointF mouseWorld;
                    RectangleF camWorld;
                    int peerTick = 0;
                    bool hasPeerTick = false;
                    if (isCompact)
                    {
                        mouseWorld = new PointF(DequantizeCursorCoord(r.ReadUInt16()), DequantizeCursorCoord(r.ReadUInt16()));
                        camWorld = new RectangleF(
                            DequantizeCursorCoord(r.ReadUInt16()),
                            DequantizeCursorCoord(r.ReadUInt16()),
                            DequantizeCursorCoord(r.ReadUInt16()),
                            DequantizeCursorCoord(r.ReadUInt16()));
                        if (r.BaseStream.Position < r.BaseStream.Length)
                        {
                            peerTick = (int)NetCommand.ReadVarUInt32(r);
                            hasPeerTick = true;
                        }
                    }
                    else
                    {
                        mouseWorld = new PointF(r.ReadSingle(), r.ReadSingle());
                        camWorld = new RectangleF(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                        {
                            peerTick = r.ReadInt32();
                            hasPeerTick = true;
                        }
                    }

                    var state = new AllyVisualState
                    {
                        PlayerId = pid,
                        MouseWorldPosition = mouseWorld,
                        CameraWorldArea = camWorld,
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    AllyStates[pid] = state;

                    if (Transport.IsHost && hasPeerTick)
                        UpdatePeerSimTick(senderId, peerTick);

                    // If Host, relay to other clients in 3-player session
                    if (Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                        {
                            if (_useCompactWireProtocol)
                            {
                                w.Write((byte)(state.PlayerId | 0x80));
                                w.Write(QuantizeCursorCoord(state.MouseWorldPosition.X));
                                w.Write(QuantizeCursorCoord(state.MouseWorldPosition.Y));
                                w.Write(QuantizeCursorCoord(state.CameraWorldArea.X));
                                w.Write(QuantizeCursorCoord(state.CameraWorldArea.Y));
                                w.Write(QuantizeCursorCoord(state.CameraWorldArea.Width));
                                w.Write(QuantizeCursorCoord(state.CameraWorldArea.Height));
                            }
                            else
                            {
                                w.Write(state.PlayerId);
                                w.Write(state.MouseWorldPosition.X);
                                w.Write(state.MouseWorldPosition.Y);
                                w.Write(state.CameraWorldArea.X);
                                w.Write(state.CameraWorldArea.Y);
                                w.Write(state.CameraWorldArea.Width);
                                w.Write(state.CameraWorldArea.Height);
                            }
                        });
                    }
                    break;
                }

                case PacketType.TacticalPing:
                {
                    byte pid = r.ReadByte();
                    int cellX = r.ReadInt32();
                    int cellY = r.ReadInt32();
                    string label = r.ReadString();
                    if (Transport != null && pid == Transport.LocalPlayerId) break;

                    AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, new Point(cellX, cellY), label);

                    // If Host, relay to other clients in 3-player session
                    if (Transport != null && Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TacticalPing, w =>
                        {
                            w.Write(pid);
                            w.Write(cellX);
                            w.Write(cellY);
                            w.Write(label);
                        });
                    }
                    break;
                }

                case PacketType.RttPing:
                {
                    if (Transport == null || Transport.IsHost) break;
                    uint seq = r.ReadUInt32();
                    long hostTs = r.ReadInt64();
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        float hostRtt = r.ReadSingle();
                        if (hostRtt >= 0f && hostRtt < 5000f)
                            CurrentSmoothedRttMs = hostRtt;
                    }
                    Transport.SendToPeer(1, SendChannel.UnreliableSequenced, PacketType.RttPong, w =>
                    {
                        w.Write(seq);
                        w.Write(hostTs);
                        w.Write(DXGame.Current?.IAIterationInt ?? 0);
                    });
                    break;
                }

                case PacketType.RttPong:
                {
                    if (Transport == null || !Transport.IsHost) break;
                    uint seq = r.ReadUInt32();
                    long hostTs = r.ReadInt64();
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        int clientTick = r.ReadInt32();
                        UpdatePeerSimTick(senderId, clientTick);
                    }
                    double rttMs = (Stopwatch.GetTimestamp() - hostTs) * 1000.0 / Stopwatch.Frequency;
                    if (rttMs < 0.0 || rttMs > 2000.0 || double.IsNaN(rttMs) || double.IsInfinity(rttMs))
                        break;

                    double sample = Math.Min(rttMs, 1500.0);
                    if (_peerSmoothedRttMs.TryGetValue(senderId, out double prev))
                    {
                        // Bias toward faster samples so occasional slow Client render frames don't inflate InputDelayTicks.
                        double alpha = sample <= prev ? 0.40 : 0.14;
                        _peerSmoothedRttMs[senderId] = (1.0 - alpha) * prev + alpha * sample;
                    }
                    else
                    {
                        _peerSmoothedRttMs[senderId] = sample;
                    }

                    _peerRttSampleTicks[senderId] = DXHelper_Environment.Ticks;
                    UpdateAdaptiveInputDelay();
                    break;
                }
            }
        }

        private static void CacheTickPositions(
            int tick,
            List<StateHasher.EntityPosSample> samples,
            in StateHasher.LevelStateSnapshotDigest digest,
            List<StateHasher.ProdQueueSample> prodQueues)
        {
            if (samples == null) return;
            _tickPosCache[tick] = samples;
            _tickEconCache[tick] = new EconSnapshot
            {
                Gold = digest.Gold,
                Wood = digest.Wood,
                Stone = digest.Stone,
                Iron = digest.Iron,
                Oil = digest.Oil
            };
            if (prodQueues != null && prodQueues.Count > 0)
                _tickProdCache[tick] = prodQueues;
            else
                _tickProdCache.Remove(tick);

            _tickPosCacheOrder.Enqueue(tick);
            while (_tickPosCacheOrder.Count > 16)
            {
                int old = _tickPosCacheOrder.Dequeue();
                _tickPosCache.Remove(old);
                _tickEconCache.Remove(old);
                _tickProdCache.Remove(old);
            }
        }

        private static void WriteStage1DigestReport(BinaryWriter w, in StateHasher.LevelStateSnapshotDigest d, List<OwnedUnitPosSample> ownedUnits, long idCounter)
        {
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.SimTick));
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.GameTimeMs));
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.EntityCount));
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.AggroZombieCount));
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.ProjectileCount));
            NetCommand.WriteVarUInt32(w, (uint)Math.Max(0, d.QueuedOrderCount));
            w.Write(d.FullHash);
            w.Write(d.HardHash);
            w.Write(d.PosChecksum);
            w.Write(d.LifeChecksum);
            w.Write(d.CoarsePosChecksum);
            w.Write(ComputeDetailMixHash(in d, ownedUnits));
            w.Write(idCounter);
        }

        private static TickDigestRecord ReadStage1DigestReport(BinaryReader r)
        {
            var d = new StateHasher.LevelStateSnapshotDigest
            {
                SimTick = (int)NetCommand.ReadVarUInt32(r),
                GameTimeMs = (int)NetCommand.ReadVarUInt32(r),
                EntityCount = (int)NetCommand.ReadVarUInt32(r),
                AggroZombieCount = (int)NetCommand.ReadVarUInt32(r),
                ProjectileCount = (int)NetCommand.ReadVarUInt32(r),
                QueuedOrderCount = (int)NetCommand.ReadVarUInt32(r),
                FullHash = r.ReadUInt64(),
                HardHash = r.ReadUInt64(),
                PosChecksum = r.ReadInt64(),
                LifeChecksum = r.ReadInt64(),
                CoarsePosChecksum = r.ReadInt64(),
            };
            ulong detailMix = r.ReadUInt64();
            long idCounter = r.ReadInt64();
            return new TickDigestRecord
            {
                Digest = d,
                Summary = null,
                OwnedUnits = null,
                SyncedIdCounter = idCounter,
                HasFullDetail = false,
                DetailMixHash = detailMix
            };
        }

        private static void WriteStage2DetailResponse(BinaryWriter w, int tick, ClientEpochDetailEntry entry)
        {
            w.Write(tick);
            bool found = entry != null;
            w.Write(found);
            if (!found) return;

            var d = entry.Digest;
            NetCommand.WriteZigZagVarInt32(w, d.Gold);
            NetCommand.WriteZigZagVarInt32(w, d.Wood);
            NetCommand.WriteZigZagVarInt32(w, d.Stone);
            NetCommand.WriteZigZagVarInt32(w, d.Iron);
            NetCommand.WriteZigZagVarInt32(w, d.Oil);
            w.Write(d.ZombieAggroChecksum);
            w.Write(d.ProjectileChecksum);
            w.Write(d.ProductionChecksum);
            w.Write(d.ReservationChecksum);

            WriteBucketArray(w, d.PosBuckets);
            WriteBucketArray(w, d.LifeBuckets);
            WriteBucketArray(w, d.EntityBuckets);

            var owned = entry.OwnedUnits;
            int ownedCount = owned != null ? Math.Min(owned.Count, MaxReverseAlignUnitsPerReport) : 0;
            w.Write((ushort)ownedCount);
            for (int i = 0; i < ownedCount; i++)
            {
                w.Write(owned[i].Id);
                w.Write(owned[i].X);
                w.Write(owned[i].Y);
            }
        }

        private static void WriteBucketArray(BinaryWriter w, ulong[] arr)
        {
            int len = arr?.Length ?? 0;
            w.Write((ushort)len);
            for (int i = 0; i < len; i++)
                w.Write(arr[i]);
        }

        private static void MergeStage2DetailResponse(BinaryReader r, out int tick, byte senderId)
        {
            tick = r.ReadInt32();
            bool found = r.ReadBoolean();
            if (!_clientTickDigests.TryGetValue(tick, out var byPeer) || !byPeer.TryGetValue(senderId, out var rec))
                return;

            if (found)
            {
                rec.Digest.Gold = NetCommand.ReadZigZagVarInt32(r);
                rec.Digest.Wood = NetCommand.ReadZigZagVarInt32(r);
                rec.Digest.Stone = NetCommand.ReadZigZagVarInt32(r);
                rec.Digest.Iron = NetCommand.ReadZigZagVarInt32(r);
                rec.Digest.Oil = NetCommand.ReadZigZagVarInt32(r);
                rec.Digest.ZombieAggroChecksum = r.ReadInt64();
                rec.Digest.ProjectileChecksum = r.ReadInt64();
                rec.Digest.ProductionChecksum = r.ReadInt64();
                rec.Digest.ReservationChecksum = r.ReadInt64();
                rec.Digest.PosBuckets = ReadBucketArray(r);
                rec.Digest.LifeBuckets = ReadBucketArray(r);
                rec.Digest.EntityBuckets = ReadBucketArray(r);

                if (r.BaseStream.Position + 2 <= r.BaseStream.Length)
                {
                    ushort ownedCount = r.ReadUInt16();
                    if (ownedCount > 0 && r.BaseStream.Position + ownedCount * 16L <= r.BaseStream.Length)
                    {
                        var ownedUnits = new List<OwnedUnitPosSample>(ownedCount);
                        for (int i = 0; i < ownedCount; i++)
                        {
                            ownedUnits.Add(new OwnedUnitPosSample
                            {
                                Id = r.ReadUInt64(),
                                X = r.ReadSingle(),
                                Y = r.ReadSingle()
                            });
                        }
                        rec.OwnedUnits = ownedUnits;
                        if (EnableAsyncPosAlign)
                        {
                            _incomingReverseAlignQueue.Enqueue(new PendingReverseAlignApply
                            {
                                Generation = _asyncGeneration,
                                SenderPlayerId = senderId,
                                Tick = tick,
                                Units = ownedUnits
                            });
                        }
                    }
                }
            }
            rec.HasFullDetail = true;
        }

        private static void WriteDigestReport(BinaryWriter w, in StateHasher.LevelStateSnapshotDigest d, string summary, List<OwnedUnitPosSample> ownedUnits = null, long idCounter = 0L)
        {
            w.Write(d.SimTick);
            w.Write(d.GameTimeMs);
            w.Write(d.Gold);
            w.Write(d.Wood);
            w.Write(d.Stone);
            w.Write(d.Iron);
            w.Write(d.Oil);
            w.Write(d.EntityCount);
            w.Write(d.ProjectileCount);
            w.Write(d.QueuedOrderCount);
            w.Write(d.AggroZombieCount);
            w.Write(d.PosChecksum);
            w.Write(d.CoarsePosChecksum);
            w.Write(d.LifeChecksum);
            w.Write(d.ZombieAggroChecksum);
            w.Write(d.ProjectileChecksum);
            w.Write(d.ProductionChecksum);
            w.Write(d.ReservationChecksum);
            w.Write(d.HardHash);
            w.Write(d.FullHash);
            w.Write(summary ?? string.Empty);

            int bucketLen = d.PosBuckets?.Length ?? 0;
            w.Write((ushort)bucketLen);
            for (int i = 0; i < bucketLen; i++)
                w.Write(d.PosBuckets[i]);

            int lifeLen = d.LifeBuckets?.Length ?? 0;
            w.Write((ushort)lifeLen);
            for (int i = 0; i < lifeLen; i++)
                w.Write(d.LifeBuckets[i]);

            int entLen = d.EntityBuckets?.Length ?? 0;
            w.Write((ushort)entLen);
            for (int i = 0; i < entLen; i++)
                w.Write(d.EntityBuckets[i]);

            int ownedCount = ownedUnits != null ? Math.Min(ownedUnits.Count, MaxReverseAlignUnitsPerReport) : 0;
            w.Write((ushort)ownedCount);
            for (int i = 0; i < ownedCount; i++)
            {
                w.Write(ownedUnits[i].Id);
                w.Write(ownedUnits[i].X);
                w.Write(ownedUnits[i].Y);
            }
            w.Write(idCounter);
        }

        private static TickDigestRecord ReadDigestReport(BinaryReader r)
        {
            var d = new StateHasher.LevelStateSnapshotDigest
            {
                SimTick = r.ReadInt32(),
                GameTimeMs = r.ReadInt32(),
                Gold = r.ReadInt32(),
                Wood = r.ReadInt32(),
                Stone = r.ReadInt32(),
                Iron = r.ReadInt32(),
                Oil = r.ReadInt32(),
                EntityCount = r.ReadInt32(),
                ProjectileCount = r.ReadInt32(),
                QueuedOrderCount = r.ReadInt32(),
                AggroZombieCount = r.ReadInt32(),
                PosChecksum = r.ReadInt64(),
                CoarsePosChecksum = r.ReadInt64(),
                LifeChecksum = r.ReadInt64(),
                ZombieAggroChecksum = r.ReadInt64(),
                ProjectileChecksum = r.ReadInt64(),
                ProductionChecksum = r.ReadInt64(),
                ReservationChecksum = r.ReadInt64(),
                HardHash = r.ReadUInt64(),
                FullHash = r.ReadUInt64(),
            };
            string summary = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : string.Empty;
            d.PosBuckets = ReadBucketArray(r);
            d.LifeBuckets = ReadBucketArray(r);
            d.EntityBuckets = ReadBucketArray(r);

            List<OwnedUnitPosSample> ownedUnits = null;
            if (r.BaseStream.Position + 2 <= r.BaseStream.Length)
            {
                ushort ownedCount = r.ReadUInt16();
                if (ownedCount > 0 && r.BaseStream.Position + ownedCount * 16L <= r.BaseStream.Length)
                {
                    ownedUnits = new List<OwnedUnitPosSample>(ownedCount);
                    for (int i = 0; i < ownedCount; i++)
                    {
                        ownedUnits.Add(new OwnedUnitPosSample
                        {
                            Id = r.ReadUInt64(),
                            X = r.ReadSingle(),
                            Y = r.ReadSingle()
                        });
                    }
                }
            }
            long idCounter = 0L;
            if (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                idCounter = r.ReadInt64();
            return new TickDigestRecord { Digest = d, Summary = summary, OwnedUnits = ownedUnits, SyncedIdCounter = idCounter };
        }

        private static ulong[] ReadBucketArray(BinaryReader r)
        {
            if (r.BaseStream.Position + 2 > r.BaseStream.Length) return null;
            ushort bucketLen = r.ReadUInt16();
            if (bucketLen > 0 && r.BaseStream.Position + bucketLen * 8L <= r.BaseStream.Length)
            {
                var arr = new ulong[bucketLen];
                for (int i = 0; i < bucketLen; i++)
                    arr[i] = r.ReadUInt64();
                return arr;
            }
            return null;
        }

        private static ushort ComputeDiffBucketMask(
            in StateHasher.LevelStateSnapshotDigest host,
            in StateHasher.LevelStateSnapshotDigest client)
        {
            return DiffMask(host.PosBuckets, client.PosBuckets);
        }

        private static ushort ComputeLifeOrEntityDiffBucketMask(
            in StateHasher.LevelStateSnapshotDigest host,
            in StateHasher.LevelStateSnapshotDigest client)
        {
            return (ushort)(DiffMask(host.LifeBuckets, client.LifeBuckets)
                | DiffMask(host.EntityBuckets, client.EntityBuckets));
        }

        private static ushort DiffMask(ulong[] a, ulong[] b)
        {
            if (a == null || b == null ||
                a.Length != StateHasher.BucketCount ||
                b.Length != StateHasher.BucketCount)
            {
                return 0xFFFF;
            }

            ushort mask = 0;
            for (int i = 0; i < StateHasher.BucketCount; i++)
            {
                if (a[i] != b[i])
                    mask |= (ushort)(1 << i);
            }
            return mask;
        }

        private static void VerifyTickChecksum(int tick)
        {
            if (!_hostTickDigests.TryGetValue(tick, out TickDigestRecord hostRec)) return;
            if (!_clientTickDigests.TryGetValue(tick, out var byPeer) || byPeer.Count == 0) return;

            var host = hostRec.Digest;
            var resolvedPeers = new List<byte>();

            foreach (var kv in byPeer)
            {
                byte senderId = kv.Key;
                var client = kv.Value.Digest;

                if (host.FullHash == client.FullHash)
                {
                    resolvedPeers.Add(senderId);
                    _verifiedChecksumCycles++;
                    _consecutiveProjOnlyMismatch = 0;
                    _consecutiveHardMismatch = 0;
                    _consecutivePosExceeded = 0;
                    ResetTieredResyncState();
                    double matchNow = DXHelper_Environment.Ticks;
                    bool stateRecovered = !string.Equals(_lastChecksumLogKind, "MATCH", StringComparison.Ordinal);
                    if (stateRecovered || ModLogger.IsVerbose || matchNow < _lastChecksumMatchLogMs || (matchNow - _lastChecksumMatchLogMs) >= ChecksumSummaryIntervalMs)
                    {
                        string suffix = _suppressedChecksumMatchCount > 0 ? $" (+{_suppressedChecksumMatchCount} consecutive MATCH)" : string.Empty;
                        _suppressedChecksumMatchCount = 0;
                        _lastChecksumLogKind = "MATCH";
                        _lastChecksumMatchLogMs = matchNow;
                        ModLogger.Info($"[StateChecksum] Tick {tick} MATCH (0x{host.FullHash:X16}) with Client({senderId}).{suffix}");
                    }
                    else
                    {
                        _suppressedChecksumMatchCount++;
                    }
                    continue;
                }

                // Stage 2 pull: if compact Stage 1 detected a hash difference and buckets/res detail
                // have not yet arrived, request full diagnostic + bucket payload before running triage.
                if (!kv.Value.HasFullDetail)
                {
                    double nowReq = DXHelper_Environment.Ticks;
                    if (!kv.Value.DetailRequested)
                    {
                        kv.Value.DetailRequested = true;
                        kv.Value.DetailRequestedAtMs = nowReq;
                        Transport.SendToPeer(senderId, SendChannel.ReliableCommand, PacketType.StateChecksumDetailRequest, w => w.Write(tick));
                        continue;
                    }
                    if (nowReq >= kv.Value.DetailRequestedAtMs && (nowReq - kv.Value.DetailRequestedAtMs) < 3000.0)
                        continue;
                }

                resolvedPeers.Add(senderId);
                _verifiedChecksumCycles++;
                string hostSummary = hostRec.Summary ?? FormatDigestSummary(in host);
                string clientSummary = kv.Value.Summary ?? FormatDigestSummary(in client);

                if (hostRec.SyncedIdCounter != 0L && kv.Value.SyncedIdCounter != 0L
                    && hostRec.SyncedIdCounter != kv.Value.SyncedIdCounter)
                {
                    ModLogger.Warn($"[StateChecksum] ID COUNTER DRIFT at Tick {tick}: Host={hostRec.SyncedIdCounter}, Client({senderId})={kv.Value.SyncedIdCounter}, delta={hostRec.SyncedIdCounter - kv.Value.SyncedIdCounter}");
                }

                bool lifeDiff = host.LifeChecksum != client.LifeChecksum;
                bool aggroDiff = host.ZombieAggroChecksum != client.ZombieAggroChecksum || host.AggroZombieCount != client.AggroZombieCount;
                bool aggroCountDiff = host.AggroZombieCount != client.AggroZombieCount;
                bool entityDiff = host.EntityCount != client.EntityCount;
                bool timeDiff = host.GameTimeMs != client.GameTimeMs;
                bool resAmountDiff = host.Gold != client.Gold || host.Wood != client.Wood || host.Stone != client.Stone
                    || host.Iron != client.Iron || host.Oil != client.Oil;
                bool resvDiff = host.ReservationChecksum != client.ReservationChecksum;
                bool prodDiff = host.ProductionChecksum != client.ProductionChecksum || host.QueuedOrderCount != client.QueuedOrderCount;
                bool econAsyncDiff = resAmountDiff || resvDiff || prodDiff;
                bool econCoreDiff = timeDiff || econAsyncDiff;
                bool econDiff = econCoreDiff;
                bool projDiff = host.ProjectileCount != client.ProjectileCount || host.ProjectileChecksum != client.ProjectileChecksum;
                bool posDiff = host.PosChecksum != client.PosChecksum;
                bool coarsePosDiff = host.CoarsePosChecksum != client.CoarsePosChecksum;

                int entityTol = Math.Min(30, Math.Max(12, host.EntityCount / 1000));
                int aggroTol = Math.Max(15, host.AggroZombieCount / 10);
                int absEntityDelta = Math.Abs(host.EntityCount - client.EntityCount);
                int absAggroDelta = Math.Abs(host.AggroZombieCount - client.AggroZombieCount);

                // In async_align mode, sub-tile solver nudges or minor combat timing can briefly
                // shift alerted zombie states within aggroTol without altering HP, entity counts,
                // coarse grid cells, or economy. Treat that as soft drift for async pos alignment
                // instead of stalling both players with a 6.5s full-game snapshot reload.
                bool softAggroDrift = !IsStrictMode
                    && aggroDiff && absAggroDelta <= aggroTol
                    && !lifeDiff && !entityDiff && !econDiff && !coarsePosDiff;

                bool hardCoreDiff = lifeDiff || entityDiff || econDiff || (aggroDiff && !softAggroDrift);
                bool majorStateDiff = econCoreDiff
                    || absEntityDelta > entityTol
                    || absAggroDelta > aggroTol;
                double now = DXHelper_Environment.Ticks;

                // Tiered policy (async_align):
                //  - Pos / HP / aggro / projectile / small entity-count deltas are corrected continuously by
                //    host-authoritative AsyncPosAlign (positions + HP + host-side kills) with no pause.
                //  - Economy (resources, production, reservations, game time) is irreversible -> reload
                //    after a short persistent streak.
                //  - Entity-count delta beyond tolerance (spawns/deaths that cannot be corrected) -> reload
                //    only when sustained for ~30s.
                //  - Any residual divergence is realigned by a long-period reload (LongPeriodRealignMs).
                bool structuralDiff = absEntityDelta > entityTol;
                bool longPeriodDue = false;
                if (!IsStrictMode)
                {
                    _consecutiveEconMismatch = timeDiff ? _consecutiveEconMismatch + 1 : 0;
                    _consecutiveAsyncEconMismatch = econAsyncDiff ? _consecutiveAsyncEconMismatch + 1 : 0;
                    _consecutiveStructuralMismatch = structuralDiff ? _consecutiveStructuralMismatch + 1 : 0;
                    bool substantiveDiff = timeDiff || _consecutiveAsyncEconMismatch >= 3 || absEntityDelta > entityTol;
                    if (substantiveDiff)
                    {
                        if (_divergedSinceTicks < 0) _divergedSinceTicks = now;
                        longPeriodDue = now - _divergedSinceTicks >= LongPeriodRealignMs;
                    }
                    else
                    {
                        _divergedSinceTicks = -1.0;
                    }
                }
                bool earlyEconBypass = !IsStrictMode && !_econEarlyBypassUsed
                    && _verifiedChecksumCycles <= 3
                    && (_consecutiveEconMismatch >= EconReloadStreak || _consecutiveAsyncEconMismatch >= AsyncEconFallbackStreak);
                bool structuralCooldownElapsed = !IsStrictMode
                    && _consecutiveStructuralMismatch >= StructuralReloadStreak
                    && (now - _lastAutoResyncTicks >= StructuralResyncCooldownMs);
                bool cooldownElapsed = (now - _lastAutoResyncTicks >= AutoResyncCooldownMs) || earlyEconBypass || structuralCooldownElapsed;
                bool canReloadNow = EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport
                    && cooldownElapsed;

                bool triggerHardDesync = IsStrictMode
                    ? (hardCoreDiff || projDiff)
                    : canReloadNow && (_consecutiveEconMismatch >= EconReloadStreak
                        || _consecutiveAsyncEconMismatch >= AsyncEconFallbackStreak
                        || _consecutiveStructuralMismatch >= StructuralReloadStreak
                        || longPeriodDue);

                if (triggerHardDesync)
                {
                    if (projDiff && !hardCoreDiff) _consecutiveProjOnlyMismatch++;
                    else _consecutiveProjOnlyMismatch = 0;
                    _consecutiveHardMismatch = 0;

                    string reason = IsStrictMode ? "strict"
                        : _consecutiveEconMismatch >= EconReloadStreak ? $"time/econ x{_consecutiveEconMismatch}"
                        : _consecutiveAsyncEconMismatch >= AsyncEconFallbackStreak ? $"asyncEconFallback x{_consecutiveAsyncEconMismatch}"
                        : _consecutiveStructuralMismatch >= StructuralReloadStreak ? $"entityDelta={absEntityDelta} x{_consecutiveStructuralMismatch}"
                        : $"long-period realign after {(now - _divergedSinceTicks) / 1000.0:F0}s";
                    ModLogger.Error($"[StateChecksum] HARD DESYNC DETECTED at Tick {tick} ({reason})! Life={lifeDiff} Aggro={aggroDiff} Entity={entityDiff} Econ={econDiff} Proj={projDiff} Pos={posDiff} (Host=0x{host.FullHash:X16}, Client({senderId})=0x{client.FullHash:X16})");
                    ModLogger.Error($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                    ModLogger.Error($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    if (!_desyncDumped)
                    {
                        _desyncDumped = true;
                        DumpHostEntitiesOnDesync(tick);
                    }
                    TrySendPosDriftSync(senderId, tick, applyLifeCorrection: false, applyPosCorrection: false, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    GameBridge.ShowTopBanner("State Drift Detected (see log)", Color.Orange, 2500);

                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && cooldownElapsed)
                    {
                        if (earlyEconBypass)
                        {
                            _econEarlyBypassUsed = true;
                            ModLogger.Warn($"[CoopSessionManager] Early-epoch Econ divergence (cycle={_verifiedChecksumCycles}): bypassing resync cooldown once.");
                        }
                        _lastAutoResyncTicks = now;
                        ResetTieredResyncState();
                        ModLogger.WarnImmediate($"[CoopSessionManager] Auto-resync triggered after HARD DESYNC at Tick {tick} (Life={lifeDiff}, Aggro={aggroDiff}, Entity={entityDiff}, Econ={econDiff}, Proj={projDiff}).");
                        RequestSnapshotBroadcast();
                    }
                    continue;
                }

                if (hardCoreDiff)
                {
                    // Combat-level drift (HP / aggro / small entity delta): correct host-authoritatively
                    // in the background; the tiered policy above decides if a reload is ever needed.
                    // Decouple fast HP/kill sync (2s) from slow position realignments (8s).
                    _consecutiveHardMismatch++;
                    _consecutiveProjOnlyMismatch = 0;
                    bool firstTransient = !string.Equals(_lastChecksumLogKind, "TRANSIENT", StringComparison.Ordinal) || _consecutiveHardMismatch == 1;
                    if (firstTransient || ModLogger.IsVerbose || (_consecutiveHardMismatch % 5) == 0 || now < _lastChecksumTransientLogMs || (now - _lastChecksumTransientLogMs) >= 10000.0)
                    {
                        _lastChecksumLogKind = "TRANSIENT";
                        _lastChecksumTransientLogMs = now;
                        ModLogger.Warn($"[StateChecksum] TRANSIENT STATE DRIFT at Tick {tick} (Life={lifeDiff} Aggro={aggroDiff} Entity={entityDiff}(d={absEntityDelta}) Econ={econDiff} CoarseDiff={coarsePosDiff} streak={_consecutiveHardMismatch} econStreak={_consecutiveEconMismatch}/{_consecutiveAsyncEconMismatch} entityStreak={_consecutiveStructuralMismatch}).");
                        if (firstTransient || ModLogger.IsVerbose)
                        {
                            ModLogger.Info($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                            ModLogger.Info($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                        }
                    }
                    bool needEcon = resAmountDiff && !IsStrictMode;
                    bool needProd = prodDiff && !IsStrictMode;
                    bool needStructMeta = (entityDiff || resvDiff) && !IsStrictMode;
                    bool needSpawnMeta = (entityDiff || _lastClientMissingOnClient > 0) && !IsStrictMode;
                    bool needAnySync = posDiff || lifeDiff || entityDiff || needEcon || needProd || resvDiff || aggroDiff;
                    double effectivePosCooldownMs = host.EntityCount > 8000 ? AsyncPosAlignCooldownMs * 2.5 : AsyncPosAlignCooldownMs;
                    double effectiveSoftCooldownMs = host.EntityCount > 8000 ? AsyncSoftPosAlignCooldownMs * 2.5 : AsyncSoftPosAlignCooldownMs;
                    if (needAnySync && (now - _lastPosSyncSentTicks >= effectivePosCooldownMs))
                    {
                        _lastPosSyncSentTicks = now;
                        bool applyLife = EnableAsyncPosAlign || !IsStrictMode;
                        bool applyPos = applyLife && (posDiff || aggroDiff || entityDiff)
                            && (now - _lastFullPosAlignSentTicks >= effectiveSoftCooldownMs);
                        if (applyPos)
                            _lastFullPosAlignSentTicks = now;
                        bool needTargetMeta = (EnableAsyncPosAlign || !IsStrictMode) && (applyPos || aggroDiff);
                        ushort lifeEntMask = (lifeDiff || entityDiff || resvDiff) ? ComputeLifeOrEntityDiffBucketMask(in host, in client) : (ushort)0;
                        ushort posMask = (posDiff && (applyPos || needTargetMeta)) ? ComputeDiffBucketMask(in host, in client) : (ushort)0;
                        ushort mask = (ushort)(lifeEntMask | posMask);
                        if (mask == 0 && (lifeDiff || entityDiff || resvDiff || aggroDiff)) mask = 0xFFFF;
                        TrySendPosDriftSync(
                            senderId,
                            tick,
                            applyLifeCorrection: applyLife,
                            applyPosCorrection: applyPos,
                            bucketMask: mask,
                            includeStatic: (lifeDiff || entityDiff || resvDiff),
                            applyEcon: needEcon,
                            applyProd: needProd,
                            includeStructMeta: needStructMeta,
                            includeTargetMeta: needTargetMeta,
                            includeSpawnMeta: needSpawnMeta);
                    }
                    continue;
                }

                if (projDiff)
                {
                    // Projectiles are short-lived; their effect shows up as HP, which is corrected above.
                    _consecutiveProjOnlyMismatch++;
                    _consecutiveHardMismatch = 0;
                    if (_consecutiveProjOnlyMismatch == 1 || ModLogger.IsVerbose || now < _lastChecksumSoftLogMs || (now - _lastChecksumSoftLogMs) >= ChecksumSummaryIntervalMs)
                    {
                        _lastChecksumLogKind = "PROJ";
                        _lastChecksumSoftLogMs = now;
                        ModLogger.Warn($"[StateChecksum] TRANSIENT PROJ DIFF at Tick {tick} (Life/Aggro/Entity/Econ MATCH; PosDiff={posDiff} CoarseDiff={coarsePosDiff}, streak={_consecutiveProjOnlyMismatch}).");
                    }
                    if (coarsePosDiff && (now - _lastFullPosAlignSentTicks >= AsyncSoftPosAlignCooldownMs))
                    {
                        _lastFullPosAlignSentTicks = now;
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(
                            senderId,
                            tick,
                            applyLifeCorrection: EnableAsyncPosAlign,
                            applyPosCorrection: EnableAsyncPosAlign,
                            bucketMask: ComputeDiffBucketMask(in host, in client),
                            includeStatic: false,
                            includeTargetMeta: false);
                    }
                    continue;
                }

                // Pure position drift (or same-count sub-tile aggro oscillation): NO real combat/economy divergence!
                _consecutiveProjOnlyMismatch = 0;
                _consecutiveHardMismatch = 0;
                bool firstSoft = !string.Equals(_lastChecksumLogKind, "SOFT", StringComparison.Ordinal);
                if (firstSoft || ModLogger.IsVerbose || now < _lastChecksumSoftLogMs || (now - _lastChecksumSoftLogMs) >= ChecksumSummaryIntervalMs)
                {
                    string softSuffix = _suppressedChecksumSoftCount > 0 ? $" (+{_suppressedChecksumSoftCount} soft checks)" : string.Empty;
                    _suppressedChecksumSoftCount = 0;
                    _lastChecksumLogKind = "SOFT";
                    _lastChecksumSoftLogMs = now;
                    if (softAggroDrift)
                        ModLogger.Warn($"[StateChecksum] SOFT AGGRO/POS DRIFT at Tick {tick} (AggroCount={host.AggroZombieCount}/{client.AggroZombieCount}; Life/Entity/Proj/Econ/Coarse MATCH, mode={EffectiveResyncMode}).{softSuffix}");
                    else
                        ModLogger.Warn($"[StateChecksum] SOFT POS DRIFT at Tick {tick} (Life/Aggro/Entity/Proj/Econ MATCH! CoarseDiff={coarsePosDiff}, mode={EffectiveResyncMode}).{softSuffix}");
                    if (ModLogger.IsVerbose)
                    {
                        ModLogger.Info($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                        ModLogger.Info($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    }
                }
                else
                {
                    _suppressedChecksumSoftCount++;
                }

                if (IsStrictMode)
                {
                    if (now - _lastPosSyncSentTicks >= 2000.0)
                    {
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyLifeCorrection: false, applyPosCorrection: false, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    }
                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && (now - _lastAutoResyncTicks >= AutoResyncCooldownMs))
                    {
                        _lastAutoResyncTicks = now;
                        ModLogger.WarnImmediate($"[CoopSessionManager] Auto-resync triggered after DESYNC (strict pos) at Tick {tick}.");
                        RequestSnapshotBroadcast();
                    }
                }
                else
                {
                    bool shouldSendSoftSync;
                    if (!coarsePosDiff && !softAggroDrift)
                    {
                        _consecutiveSubCoarsePosDrift++;
                        shouldSendSoftSync = _consecutiveSubCoarsePosDrift >= 3;
                    }
                    else
                    {
                        _consecutiveSubCoarsePosDrift = 0;
                        shouldSendSoftSync = true;
                    }

                    if (shouldSendSoftSync && (now - _lastFullPosAlignSentTicks >= AsyncSoftPosAlignCooldownMs))
                    {
                        _lastFullPosAlignSentTicks = now;
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(
                            senderId,
                            tick,
                            applyLifeCorrection: EnableAsyncPosAlign,
                            applyPosCorrection: EnableAsyncPosAlign,
                            bucketMask: ComputeDiffBucketMask(in host, in client),
                            includeStatic: false,
                            includeTargetMeta: EnableAsyncPosAlign && (softAggroDrift || coarsePosDiff));
                    }
                }
            }

            foreach (var pid in resolvedPeers)
                byPeer.Remove(pid);
            if (byPeer.Count == 0)
                _clientTickDigests.Remove(tick);
        }

        private static bool IntArraysEqual(int[] a, int[] b)
        {
            int alen = a != null ? a.Length : 0;
            int blen = b != null ? b.Length : 0;
            if (alen != blen) return false;
            for (int i = 0; i < alen; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static void DrainAsyncQueues()
        {
            int curGen = _asyncGeneration;

            while (_outgoingPosSyncQueue.TryDequeue(out var batch))
            {
                if (batch.Generation != curGen || Transport == null) continue;
                try
                {
                    int totalChunks = batch.Chunks.Count;
                    for (int idx = 0; idx < totalChunks; idx++)
                    {
                        byte[] chunk = batch.Chunks[idx];
                        int len = chunk.Length;
                        Transport.SendToPeer(batch.TargetPlayerId, SendChannel.ReliableOrdered, PacketType.PosDriftSyncChunk, w =>
                        {
                            w.Write(batch.Seq);
                            w.Write(batch.Tick);
                            w.Write(batch.ApplyCorrection);
                            w.Write(totalChunks);
                            w.Write(idx);
                            w.Write(batch.TotalCompressedLen);
                            w.Write(len);
                            w.Write(chunk, 0, len);
                        });
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Drain outgoing failed: {ex.Message}");
                }
            }

            while (_incomingPosAlignQueue.TryDequeue(out var item))
            {
                if (item.Generation != curGen || Transport == null) continue;

                var game = DXGame.Current;
                int nCells = DXWorldGrid.NCells;
                float maxCoord = nCells > 1 ? nCells - 0.5f : 10000f;

                // Phase 2A: Resource deltas (same-tick diff preserved across transit)
                // Note: StateHasher hashes P1's authoritative ledger in Multi-Faction mode, so Client must
                // push P1's context before applying deltas (never overwrite Client's resting P2/P3 ledger!).
                if (item.ApplyEconCorrection && game != null
                    && (item.DeltaGold != 0 || item.DeltaWood != 0 || item.DeltaStone != 0 || item.DeltaIron != 0 || item.DeltaOil != 0))
                {
                    bool pushedP1 = IsMultiFactionActive && FactionContextManager.IsInitialized;
                    if (pushedP1) FactionContextManager.PushContext(1);
                    try
                    {
                        if (item.DeltaGold != 0) GameBridge.AddLevelResourceDelta("Gold", item.DeltaGold);
                        if (item.DeltaWood != 0) GameBridge.AddLevelResourceDelta("Wood", item.DeltaWood);
                        if (item.DeltaStone != 0) GameBridge.AddLevelResourceDelta("Stone", item.DeltaStone);
                        if (item.DeltaIron != 0) GameBridge.AddLevelResourceDelta("Iron", item.DeltaIron);
                        if (item.DeltaOil != 0) GameBridge.AddLevelResourceDelta("Oil", item.DeltaOil);
                    }
                    finally
                    {
                        if (pushedP1) FactionContextManager.PopContext();
                    }
                    ModLogger.Info(
                        $"[AsyncEconAlign] Tick={item.Tick} applied deltas{(pushedP1 ? " (P1)" : "")}: Gold={item.DeltaGold:+#;-#;0} Wood={item.DeltaWood:+#;-#;0} Stone={item.DeltaStone:+#;-#;0} Iron={item.DeltaIron:+#;-#;0} Oil={item.DeltaOil:+#;-#;0}");
                }

                // Phase 2B: Production queue alignment (guarded against stale transit overwrites)
                int prodFixed = 0;
                if (item.ProdQueueFixes != null && item.ProdQueueFixes.Count > 0 && game != null)
                {
                    for (int i = 0; i < item.ProdQueueFixes.Count; i++)
                    {
                        var pf = item.ProdQueueFixes[i];
                        DXEntity e = game.GetEntityByID(pf.EntityId);
                        if (e == null || e.Disposed) continue;
                        int[] liveCmds = GameBridge.ReadOrdersQueuedCommandIds(e);
                        if (!IntArraysEqual(liveCmds, pf.ExpectedClientCommandIds))
                            continue;
                        if (GameBridge.ApplyOrdersQueuedCommandIds(e, pf.HostCommandIds))
                            prodFixed++;
                    }
                    if (prodFixed > 0)
                    {
                        GameBridge.RefreshCommandPanel();
                        ModLogger.Info($"[AsyncProdAlign] Tick={item.Tick} aligned {prodFixed}/{item.ProdQueueFixes.Count} production queues.");
                    }
                }

                // Phase 2C: Small static structure alignment (missing/extra <= 8)
                int structBuilt = 0, structDestroyed = 0;
                if (item.StructDestroys != null && item.StructDestroys.Count > 0 && game != null)
                {
                    for (int i = 0; i < item.StructDestroys.Count; i++)
                    {
                        DXEntity e = game.GetEntityByID(item.StructDestroys[i]);
                        if (e == null || e.Disposed || !GameBridge.IsStructureType(e.GetType())) continue;
                        if (GameBridge.DestroySyncedExtraStructure(e))
                            structDestroyed++;
                    }
                }
                if (item.StructBuilds != null && item.StructBuilds.Count > 0 && game != null)
                {
                    for (int i = 0; i < item.StructBuilds.Count; i++)
                    {
                        var sb = item.StructBuilds[i];
                        if (game.GetEntityByID(sb.Id) != null) continue;
                        if (GameBridge.PlaceSyncedStructure(sb.Id, sb.TemplateId, sb.CellX, sb.CellY, sb.Width, sb.Height, sb.Life, sb.BuildFactor, sb.OwnerPlayerId))
                            structBuilt++;
                    }
                }
                if (structBuilt > 0 || structDestroyed > 0)
                    ModLogger.Info($"[AsyncStructAlign] Tick={item.Tick} built={structBuilt} destroyed={structDestroyed}");

                if ((item.HostMaxId & 0xC000000000000000UL) == 0
                    && item.HostMaxId < 0x3FFFFFFFFFFFFFFFUL - 1024UL
                    && (long)item.HostMaxId > LockstepClock.CurrentSyncedEntityIdCounter)
                {
                    LockstepClock.SetSyncedEntityIdCounter((long)item.HostMaxId);
                }

                // Phase 3B: Small dynamic spawn alignment (1..32 missing entities, sorted by ID)
                int dynSpawned = 0;
                if (item.DynamicSpawns != null && item.DynamicSpawns.Count > 0 && game != null)
                {
                    for (int i = 0; i < item.DynamicSpawns.Count; i++)
                    {
                        var ds = item.DynamicSpawns[i];
                        if (game.GetEntityByID(ds.Id) != null) continue;
                        if (GameBridge.SpawnSyncedDynamicEntity(ds.Id, ds.TemplateId, ds.X, ds.Y, ds.Life, ds.TeamRaw, ds.TargetEntityId, ds.OwnerPlayerId))
                            dynSpawned++;
                    }
                    if (dynSpawned > 0)
                        ModLogger.Info($"[AsyncSpawnAlign] Tick={item.Tick} spawned={dynSpawned}/{item.DynamicSpawns.Count} dynamic entities.");
                }

                int appliedCount = 0;
                int protectedSelfOwned = 0;
                int targetAligned = 0;
                HashSet<ulong> targetAlignedIds = null;
                byte localPid = Transport.LocalPlayerId;
                if (item.ApplyPosCorrection && item.Deltas != null && game != null)
                {
                    if (_lastEntityPosCorrectedTick.Count > 512)
                    {
                        var expiredIds = new List<ulong>();
                        foreach (var kv in _lastEntityPosCorrectedTick)
                        {
                            int age = item.Tick - kv.Value;
                            if (age < 0 || age >= EntityPosRealignCooldownTicks)
                                expiredIds.Add(kv.Key);
                        }
                        for (int k = 0; k < expiredIds.Count; k++)
                            _lastEntityPosCorrectedTick.Remove(expiredIds[k]);
                    }

                    for (int i = 0; i < item.Deltas.Count; i++)
                    {
                        var d = item.Deltas[i];
                        if (_lastEntityPosCorrectedTick.TryGetValue(d.Id, out int prevTick))
                        {
                            int elapsedTicks = item.Tick - prevTick;
                            if (elapsedTicks >= 0 && elapsedTicks < EntityPosRealignCooldownTicks)
                                continue;
                        }

                        DXEntity e = game.GetEntityByID(d.Id);
                        if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;

                        bool isZombie = GameBridge.IsZombieType(e.GetType());
                        float dist = (float)Math.Sqrt(d.Dx * d.Dx + d.Dy * d.Dy);
                        if (!isZombie && dist > MaxAsyncNudgeCells)
                            continue;
                        if (TryGetActiveUnitOwner(e.ID, item.Tick, e, out byte ownerId)
                            && ownerId == localPid
                            && dist < SelfOwnedHostOverrideThreshold)
                        {
                            protectedSelfOwned++;
                            continue;
                        }

                        float minThreshold = GameBridge.GetEntityPositionSnapThreshold(e, out float damping);
                        if (dist < minThreshold) continue;

                        float applyDx = dist < 2.2f ? d.Dx * damping : d.Dx;
                        float applyDy = dist < 2.2f ? d.Dy * damping : d.Dy;

                        float nx = LockstepClock.QuantizeCoord(e.Position.X + applyDx);
                        float ny = LockstepClock.QuantizeCoord(e.Position.Y + applyDy);
                        if (float.IsNaN(nx) || float.IsInfinity(nx) || float.IsNaN(ny) || float.IsInfinity(ny)) continue;

                        nx = Math.Max(0.5f, Math.Min(maxCoord, nx));
                        ny = Math.Max(0.5f, Math.Min(maxCoord, ny));
                        float actualDx = nx - e.Position.X;
                        float actualDy = ny - e.Position.Y;
                        var newPos = new PointF(nx, ny);
                        e.LastPosition = newPos;
                        e.Position = newPos;
                        var body = e.Body;
                        if (body != null && body.Handle != IntPtr.Zero)
                        {
                            body.LastPositionComputed = newPos;
                            body.PositionComputed = newPos;
                            body.PhysicPosition = newPos;
                        }
                        if (isZombie && dist > MaxAsyncNudgeCells)
                            GameBridge.RefreshEntityFogVisibility(e);
                        _lastEntityPosCorrectedTick[e.ID] = item.Tick;
                        if (dist <= 3.5f)
                            RenderVisualInterpolationPatch.RecordVisualSnapOffset(e.ID, -actualDx, -actualDy);
                        if (item.TargetFixes != null && item.TargetFixes.TryGetValue(e.ID, out var snappedTf))
                        {
                            if (GameBridge.TryAlignEntityCombatAndMoveTarget(e, snappedTf.TargetEntityId, snappedTf.MoveX, snappedTf.MoveY))
                                targetAligned++;
                            if (targetAlignedIds == null) targetAlignedIds = new HashSet<ulong>();
                            targetAlignedIds.Add(e.ID);
                        }
                        else if (item.HasTargetFixesPayload && isZombie && dist >= 2.5f)
                        {
                            // Host omits idle zombies (tgt==0 && NaN move) from TargetFixes; clear stale Client aggro.
                            if (GameBridge.TryAlignEntityCombatAndMoveTarget(e, 0UL, float.NaN, float.NaN, alignMoveTarget: true, onlyIfLocalIdle: false))
                                targetAligned++;
                        }
                        appliedCount++;
                    }
                }

                // Phase 3A: Align combat targets ONLY for non-snapped dynamic entities where Host holds
                // an active TargetEntityId != 0UL. Never overwrite CMovable.TargetPosition here (alignMoveTarget: false),
                // because Phase 2D left e.Position unsnapped (dist < minThreshold) and rewinding waypoints 15-40 ticks
                // into the past clears active paths and amplifies sub-threshold position drift.
                if (item.TargetFixes != null && item.TargetFixes.Count > 0 && game != null)
                {
                    foreach (var kv in item.TargetFixes)
                    {
                        if (targetAlignedIds != null && targetAlignedIds.Contains(kv.Key)) continue;
                        if (kv.Value.TargetEntityId == 0UL) continue;
                        if (_lastEntityPosCorrectedTick.TryGetValue(kv.Key, out int prevCorrTick))
                        {
                            int elapsedTicks = item.Tick - prevCorrTick;
                            if (elapsedTicks >= 0 && elapsedTicks < EntityPosRealignCooldownTicks)
                                continue;
                        }

                        bool posDrifted = item.DriftedPosIds15 != null && item.DriftedPosIds15.Contains(kv.Key);
                        DXEntity e = game.GetEntityByID(kv.Key);
                        if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;
                        if (TryGetActiveUnitOwner(e.ID, item.Tick, e, out byte ownerId) && ownerId == localPid)
                            continue;
                        if (GameBridge.TryAlignEntityCombatAndMoveTarget(
                                e,
                                kv.Value.TargetEntityId,
                                kv.Value.MoveX,
                                kv.Value.MoveY,
                                alignMoveTarget: false,
                                onlyIfLocalIdle: !posDrifted))
                            targetAligned++;
                    }
                }

                int lifeApplied = 0, killsApplied = 0;
                if (item.ApplyCorrection && item.LifeFixes != null && game != null)
                {
                    for (int i = 0; i < item.LifeFixes.Count; i++)
                    {
                        var lf = item.LifeFixes[i];
                        DXEntity e = game.GetEntityByID(lf.Id);
                        if (e == null || e.Disposed) continue;
                        // Missing-on-Host kills (Delta==0) are limited to zombies so an ID mismatch can
                        // never delete a player unit or building.
                        if (lf.Kill && lf.Delta == 0 && !GameBridge.IsZombieType(e.GetType())) continue;
                        if (lf.ExpectedTeamFilter == 1 && !GameBridge.IsPlayerEntity(e)) continue;
                        if (lf.ExpectedTeamFilter == 2 && GameBridge.IsPlayerEntity(e)) continue;
                        if (GameBridge.TryCorrectLife(e, lf.Delta, lf.Kill))
                        {
                            if (lf.Kill) killsApplied++;
                            else lifeApplied++;
                        }
                    }
                }

                DXEntity maxEnt = item.MaxDeltaEntityId != 0 ? game?.GetEntityByID(item.MaxDeltaEntityId) : null;
                string maxType = maxEnt != null ? maxEnt.GetType().Name : "None";
                if (!_driftOnsetLogged && item.MaxDelta >= 0.05f && maxEnt != null)
                {
                    _driftOnsetLogged = true;
                    ModLogger.Info($"[DriftOnset] First observed pos drift at Tick={item.Tick}: EntityId={item.MaxDeltaEntityId}, Type={maxType}, Pos=({maxEnt.Position.X:F3},{maxEnt.Position.Y:F3}), MaxDelta={item.MaxDelta:F4}, Drifted001={item.Drifted001}, Rms={item.RmsDelta:F4}");
                }

                double alignNow = DXHelper_Environment.Ticks;
                bool notableAlign = killsApplied > 0 || lifeApplied > 0
                    || item.MissingOnClient >= 5 || item.MissingOnHost >= 5
                    || item.MaxDelta >= 2.0f || item.ExceededThreshold
                    || ModLogger.IsVerbose;
                if (notableAlign || targetAligned > 0 || alignNow < _lastAsyncPosAlignLogMs || (alignNow - _lastAsyncPosAlignLogMs) >= PosAlignLogIntervalMs)
                {
                    _lastAsyncPosAlignLogMs = alignNow;
                    ModLogger.Info(
                        $"[AsyncPosAlign] Tick={item.Tick} Apply={item.ApplyCorrection}(pos={(item.ApplyPosCorrection ? 1 : 0)}:{appliedCount}/{item.ComparedCount}, selfProt={protectedSelfOwned}, tgt={targetAligned}) " +
                        $"Drifted(>0.001={item.Drifted001}, >0.01={item.Drifted01}, >0.1={item.Drifted1}, >0.5={item.Drifted5}) " +
                        $"MaxDelta={item.MaxDelta:F4} Rms={item.RmsDelta:F4} MaxType={maxType} Exceeded={item.ExceededThreshold} " +
                        $"LifeDiff={item.LifeDiffCount} LifeFix={lifeApplied} Kills={killsApplied} MissingOnHost={item.MissingOnHost} MissingOnClient={item.MissingOnClient}");
                }

                Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.PosDriftStatsReport, w =>
                {
                    w.Write(item.Tick);
                    w.Write(item.ApplyCorrection);
                    w.Write(appliedCount);
                    w.Write(item.ComparedCount);
                    w.Write(item.Drifted001);
                    w.Write(item.Drifted01);
                    w.Write(item.Drifted1);
                    w.Write(item.Drifted5);
                    w.Write(item.MaxDelta);
                    w.Write(item.RmsDelta);
                    w.Write(item.ExceededThreshold);
                    w.Write(maxType ?? "Unknown");
                    w.Write(item.LifeDiffCount);
                    w.Write(lifeApplied);
                    w.Write(killsApplied);
                    w.Write(item.MissingOnHost);
                    w.Write(item.MissingOnClient);
                });
            }

            while (_incomingReverseAlignQueue.TryDequeue(out var rev))
            {
                if (rev.Generation != curGen || Transport == null || !Transport.IsHost) continue;
                if (!EnableAsyncPosAlign || rev.Units == null || rev.Units.Count == 0) continue;
                if (!_tickPosCache.TryGetValue(rev.Tick, out var hostSamples) || hostSamples == null || hostSamples.Count == 0)
                    continue;

                var game = DXGame.Current;
                if (game == null) continue;
                int curTick = game.IAIterationInt;
                int nCells = DXWorldGrid.NCells;
                float maxCoord = nCells > 1 ? nCells - 0.5f : 10000f;
                int applied = 0;

                var revMap = new Dictionary<ulong, OwnedUnitPosSample>(rev.Units.Count);
                for (int i = 0; i < rev.Units.Count; i++)
                    revMap[rev.Units[i].Id] = rev.Units[i];

                for (int i = 0; i < hostSamples.Count; i++)
                {
                    var hs = hostSamples[i];
                    if (!hs.IsDynamic || !revMap.TryGetValue(hs.Id, out var u))
                        continue;

                    if (_lastEntityPosCorrectedTick.TryGetValue(u.Id, out int prevTick))
                    {
                        int elapsed = curTick - prevTick;
                        if (elapsed >= 0 && elapsed < EntityPosRealignCooldownTicks)
                            continue;
                    }

                    DXEntity e = game.GetEntityByID(u.Id);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;
                    if (!TryGetActiveUnitOwner(e.ID, curTick, e, out byte ownerId) || ownerId != rev.SenderPlayerId)
                        continue;

                    // Compare same-tick epoch samples (u@rev.Tick vs hs@rev.Tick) to eliminate tick-phase skew.
                    float dx = u.X - hs.X;
                    float dy = u.Y - hs.Y;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist < ReverseAlignMinThreshold || dist > MaxAsyncTeleportCells) continue;

                    float damping = dist < 2.2f ? 0.65f : 1.0f;
                    float nx = LockstepClock.QuantizeCoord(e.Position.X + dx * damping);
                    float ny = LockstepClock.QuantizeCoord(e.Position.Y + dy * damping);
                    if (float.IsNaN(nx) || float.IsInfinity(nx) || float.IsNaN(ny) || float.IsInfinity(ny)) continue;

                    nx = Math.Max(0.5f, Math.Min(maxCoord, nx));
                    ny = Math.Max(0.5f, Math.Min(maxCoord, ny));
                    float actualDx = nx - e.Position.X;
                    float actualDy = ny - e.Position.Y;
                    var newPos = new PointF(nx, ny);
                    e.LastPosition = newPos;
                    e.Position = newPos;
                    var body = e.Body;
                    if (body != null && body.Handle != IntPtr.Zero)
                    {
                        body.LastPositionComputed = newPos;
                        body.PositionComputed = newPos;
                        body.PhysicPosition = newPos;
                    }
                    _lastEntityPosCorrectedTick[e.ID] = curTick;
                    if (dist <= 3.5f)
                        RenderVisualInterpolationPatch.RecordVisualSnapOffset(e.ID, -actualDx, -actualDy);
                    applied++;
                }

                if (applied > 0)
                    ModLogger.Info($"[AsyncReverseAlign] Client({rev.SenderPlayerId}) Tick={rev.Tick} applied={applied}/{rev.Units.Count}");
            }
        }

        private static void TrySendPosDriftSync(
            byte targetPlayerId,
            int tick,
            bool applyLifeCorrection,
            bool applyPosCorrection,
            ushort bucketMask,
            bool includeStatic = true,
            bool applyEcon = false,
            bool applyProd = false,
            bool includeStructMeta = false,
            bool includeTargetMeta = false,
            bool includeSpawnMeta = false)
        {
            if (Transport == null || !Transport.IsHost) return;
            if (!_tickPosCache.TryGetValue(tick, out var samples) || samples == null || samples.Count == 0)
                return;

            ushort effectiveMask = bucketMask == 0 ? (ushort)0xFFFF : bucketMask;
            int gen = _asyncGeneration;
            if (Interlocked.CompareExchange(ref _posSyncBuildInFlight, 1, 0) != 0)
                return;

            bool wantStatic = includeStatic || includeStructMeta;
            List<StateHasher.EntityPosSample> filtered = new List<StateHasher.EntityPosSample>(
                effectiveMask == 0xFFFF ? samples.Count : (samples.Count / 4 + 16));
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (!wantStatic && !s.IsDynamic) continue;
                if (effectiveMask == 0xFFFF || (effectiveMask & (1 << (int)(s.Id & 0xFUL))) != 0)
                    filtered.Add(s);
            }

            EconSnapshot econSnap = default;
            bool hasEcon = applyEcon && _tickEconCache.TryGetValue(tick, out econSnap);
            bool hasProd = applyProd;
            StateHasher.ProdQueueSample[] prodSnap = null;
            if (hasProd)
            {
                prodSnap = _tickProdCache.TryGetValue(tick, out var pqList) && pqList != null
                    ? pqList.ToArray()
                    : Array.Empty<StateHasher.ProdQueueSample>();
            }

            if (filtered.Count == 0 && !hasEcon && !hasProd)
            {
                Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
                return;
            }

            Dictionary<ulong, StructSampleMeta> structMetaMap = null;
            Dictionary<ulong, DynamicTargetSample> targetMetaMap = null;
            Dictionary<ulong, DynamicSpawnMeta> spawnMetaMap = null;
            bool needMainLoopForMeta = includeStructMeta || includeSpawnMeta;
            bool needFallbackTargetScan = false;
            if (includeTargetMeta && !needMainLoopForMeta)
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (filtered[i].IsDynamic && !filtered[i].HasTargetMeta)
                    {
                        needFallbackTargetScan = true;
                        break;
                    }
                }
            }
            if (needMainLoopForMeta || needFallbackTargetScan)
            {
                var game = DXGame.Current;
                if (game != null)
                {
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        var s = filtered[i];
                        bool wantStruct = includeStructMeta && s.IsStructure;
                        bool wantSpawn = includeSpawnMeta && s.IsDynamic;
                        bool wantFallbackTarget = includeTargetMeta && s.IsDynamic && !s.HasTargetMeta;
                        if (!wantStruct && !wantSpawn && !wantFallbackTarget)
                            continue;

                        DXEntity ent = game.GetEntityByID(s.Id);
                        if (ent == null || ent.Disposed) continue;

                        if (wantStruct)
                        {
                            if (GameBridge.TryCaptureStructureMeta(ent, out ulong tplId, out int cx, out int cy, out byte w, out byte h, out float bf))
                            {
                                if (structMetaMap == null) structMetaMap = new Dictionary<ulong, StructSampleMeta>(32);
                                structMetaMap[s.Id] = new StructSampleMeta
                                {
                                    Id = s.Id,
                                    TemplateId = tplId,
                                    CellX = cx,
                                    CellY = cy,
                                    Width = w,
                                    Height = h,
                                    Life = s.Life,
                                    BuildFactor = bf,
                                    OwnerPlayerId = IsMultiFactionActive ? FactionOwnershipTracker.GetOwner(s.Id) : (byte)0
                                };
                            }
                        }

                        if (wantFallbackTarget)
                        {
                            GameBridge.GetEntityCombatAndMoveTargets(ent, out ulong tgtId, out float mx, out float my);
                            if (tgtId != 0 || !float.IsNaN(mx) || !GameBridge.IsZombieType(ent.GetType()))
                            {
                                if (targetMetaMap == null) targetMetaMap = new Dictionary<ulong, DynamicTargetSample>(64);
                                targetMetaMap[s.Id] = new DynamicTargetSample
                                {
                                    Id = s.Id,
                                    TargetEntityId = tgtId,
                                    MoveX = mx,
                                    MoveY = my
                                };
                            }
                        }

                        if (wantSpawn)
                        {
                            if (GameBridge.TryCaptureDynamicSpawnMeta(ent, out ulong dTplId, out byte teamRaw, out ulong dTgtId))
                            {
                                if (spawnMetaMap == null) spawnMetaMap = new Dictionary<ulong, DynamicSpawnMeta>(64);
                                spawnMetaMap[s.Id] = new DynamicSpawnMeta
                                {
                                    Id = s.Id,
                                    TemplateId = dTplId,
                                    TeamRaw = teamRaw,
                                    TargetEntityId = dTgtId,
                                    OwnerPlayerId = IsMultiFactionActive ? FactionOwnershipTracker.GetOwner(s.Id) : (byte)0
                                };
                            }
                        }
                    }
                }
            }

            bool hasStructMeta = includeStructMeta && structMetaMap != null && structMetaMap.Count > 0;
            bool hasSpawnMeta = includeSpawnMeta && spawnMetaMap != null && spawnMetaMap.Count > 0;
            bool useCompactV2 = _useCompactWireProtocol;
            int seq = ++_posSyncSeq;
            Task.Run(() =>
            {
                try
                {
                    if (gen != _asyncGeneration) return;

                    if (includeTargetMeta)
                    {
                        for (int i = 0; i < filtered.Count; i++)
                        {
                            var fs = filtered[i];
                            if (!fs.IsDynamic || !fs.HasTargetMeta) continue;
                            if (fs.TargetEntityId != 0UL || !float.IsNaN(fs.MoveX) || !fs.IsZombie)
                            {
                                if (targetMetaMap == null)
                                    targetMetaMap = new Dictionary<ulong, DynamicTargetSample>(64);
                                targetMetaMap[fs.Id] = new DynamicTargetSample
                                {
                                    Id = fs.Id,
                                    TargetEntityId = fs.TargetEntityId,
                                    MoveX = fs.MoveX,
                                    MoveY = fs.MoveY
                                };
                            }
                        }
                    }

                    bool hasTargetMeta = includeTargetMeta && targetMetaMap != null && targetMetaMap.Count > 0;
                    ushort sentMask = effectiveMask;
                    byte flags = (byte)(
                        (applyLifeCorrection ? 1 : 0)
                        | (applyPosCorrection ? 2 : 0)
                        | (wantStatic ? 4 : 0)
                        | (hasEcon ? 8 : 0)
                        | (hasProd ? 16 : 0)
                        | (hasStructMeta ? 32 : 0)
                        | (hasTargetMeta ? 64 : 0)
                        | (hasSpawnMeta ? 128 : 0));

                    byte[] compressed;
                    if (useCompactV2)
                    {
                        filtered.Sort((a, b) => a.Id.CompareTo(b.Id));
                        int baselineSeq = 0;
                        Dictionary<ulong, DriftBaselineEntry> baselineSamples = null;
                        lock (_driftBaselineLock)
                        {
                            if (_hostAckedBaselineByPeer.TryGetValue(targetPlayerId, out var acked) && acked.Seq > 0 && acked.Samples != null)
                            {
                                baselineSeq = acked.Seq;
                                baselineSamples = acked.Samples;
                            }
                        }
                        var nextBaseline = new Dictionary<ulong, DriftBaselineEntry>(filtered.Count);

                        int estCap = filtered.Count * 10 + 128
                            + (hasStructMeta ? structMetaMap.Count * 24 : 0)
                            + (hasTargetMeta ? targetMetaMap.Count * 16 : 0)
                            + (hasSpawnMeta ? spawnMetaMap.Count * 18 : 0);
                        using (var rawMs = new MemoryStream(estCap))
                        using (var bw = new BinaryWriter(rawMs))
                        {
                            bw.Write((ushort)0x0000); // v2 compact sentinel (sentMask is always non-zero)
                            bw.Write((byte)2);
                            bw.Write(sentMask);
                            bw.Write(flags);
                            bw.Write(seq);
                            bw.Write(baselineSeq);
                            NetCommand.WriteVarUInt32(bw, (uint)filtered.Count);

                            ulong prevId = 0UL;
                            for (int i = 0; i < filtered.Count; i++)
                            {
                                var fs = filtered[i];
                                ulong id = fs.Id;
                                NetCommand.WriteVarUInt64(bw, id - prevId);
                                prevId = id;
                                nextBaseline[id] = new DriftBaselineEntry { X = fs.X, Y = fs.Y, Life = fs.Life };

                                DynamicTargetSample tm = default;
                                StructSampleMeta sm = default;
                                DynamicSpawnMeta dsm = default;
                                bool hasT = hasTargetMeta && targetMetaMap.TryGetValue(id, out tm);
                                bool hasS = hasStructMeta && structMetaMap.TryGetValue(id, out sm);
                                bool hasD = hasSpawnMeta && spawnMetaMap.TryGetValue(id, out dsm);

                                if (baselineSamples != null && !hasT && !hasS && !hasD
                                    && baselineSamples.TryGetValue(id, out var prevS)
                                    && prevS.Life == fs.Life
                                    && Math.Abs(prevS.X - fs.X) < 0.002f
                                    && Math.Abs(prevS.Y - fs.Y) < 0.002f)
                                {
                                    bw.Write((byte)0x01); // FlagUnchangedFromBaseline
                                    continue;
                                }

                                ulong packedXY = 0UL;
                                bool isPacked = TryPackPos5B(fs.X, fs.Y, out packedXY);
                                byte eFlags = 0;
                                if (fs.Life != 0) eFlags |= 0x02;
                                eFlags |= isPacked ? (byte)0x04 : (byte)0x40;
                                if (hasT) eFlags |= 0x08;
                                if (hasS) eFlags |= 0x10;
                                if (hasD) eFlags |= 0x20;
                                bw.Write(eFlags);

                                if ((eFlags & 0x02) != 0) NetCommand.WriteZigZagVarInt32(bw, fs.Life);
                                if (isPacked)
                                    WritePacked5B(bw, packedXY);
                                else
                                {
                                    bw.Write(fs.X);
                                    bw.Write(fs.Y);
                                }

                                if (hasT)
                                {
                                    bool hasMove = !float.IsNaN(tm.MoveX) && !float.IsNaN(tm.MoveY);
                                    byte tf = (byte)((tm.TargetEntityId != 0UL ? 1 : 0) | (hasMove ? 2 : 0));
                                    bw.Write(tf);
                                    if ((tf & 1) != 0) bw.Write(tm.TargetEntityId);
                                    if ((tf & 2) != 0) { bw.Write(tm.MoveX); bw.Write(tm.MoveY); }
                                }
                                if (hasS)
                                {
                                    bw.Write(sm.TemplateId);
                                    NetCommand.WriteZigZagVarInt32(bw, sm.CellX);
                                    NetCommand.WriteZigZagVarInt32(bw, sm.CellY);
                                    bw.Write(sm.Width);
                                    bw.Write(sm.Height);
                                    bw.Write(sm.BuildFactor);
                                    bw.Write(sm.OwnerPlayerId);
                                }
                                if (hasD)
                                {
                                    bw.Write(dsm.TemplateId);
                                    bw.Write(dsm.TeamRaw);
                                    bw.Write(dsm.TargetEntityId);
                                    bw.Write(dsm.OwnerPlayerId);
                                }
                            }

                            if (hasEcon)
                            {
                                NetCommand.WriteZigZagVarInt32(bw, econSnap.Gold);
                                NetCommand.WriteZigZagVarInt32(bw, econSnap.Wood);
                                NetCommand.WriteZigZagVarInt32(bw, econSnap.Stone);
                                NetCommand.WriteZigZagVarInt32(bw, econSnap.Iron);
                                NetCommand.WriteZigZagVarInt32(bw, econSnap.Oil);
                            }
                            if (hasProd)
                            {
                                int pCount = prodSnap != null ? prodSnap.Length : 0;
                                NetCommand.WriteVarUInt32(bw, (uint)pCount);
                                for (int i = 0; i < pCount; i++)
                                {
                                    var pq = prodSnap[i];
                                    bw.Write(pq.EntityId);
                                    int len = pq.CommandIds != null ? Math.Min(255, pq.CommandIds.Length) : 0;
                                    bw.Write((byte)len);
                                    for (int j = 0; j < len; j++)
                                        NetCommand.WriteZigZagVarInt32(bw, pq.CommandIds[j]);
                                }
                            }
                            bw.Flush();
                            byte[] rawBytes = rawMs.ToArray();
                            using (var outMs = new MemoryStream(Math.Max(64, rawBytes.Length / 3)))
                            {
                                using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
                                    ds.Write(rawBytes, 0, rawBytes.Length);
                                compressed = outMs.ToArray();
                            }
                        }

                        if (gen != _asyncGeneration) return;
                        lock (_driftBaselineLock)
                        {
                            _hostPendingBaselinesBySeq[seq] = new HostPendingBaseline
                            {
                                PeerId = targetPlayerId,
                                Tick = tick,
                                Seq = seq,
                                Samples = nextBaseline
                            };
                        }
                    }
                    else
                    {
                        int estCap = filtered.Count * 20 + 64
                            + (hasStructMeta ? structMetaMap.Count * 32 : 0)
                            + (hasTargetMeta ? targetMetaMap.Count * 24 : 0)
                            + (hasSpawnMeta ? spawnMetaMap.Count * 25 : 0);
                        using (var rawMs = new MemoryStream(estCap))
                        using (var bw = new BinaryWriter(rawMs))
                        {
                            bw.Write(sentMask);
                            bw.Write(flags);
                            bw.Write(filtered.Count);
                            for (int i = 0; i < filtered.Count; i++)
                            {
                                bw.Write(filtered[i].Id);
                                bw.Write(filtered[i].X);
                                bw.Write(filtered[i].Y);
                                bw.Write(filtered[i].Life);
                            }
                            if (hasEcon)
                            {
                                bw.Write(econSnap.Gold);
                                bw.Write(econSnap.Wood);
                                bw.Write(econSnap.Stone);
                                bw.Write(econSnap.Iron);
                                bw.Write(econSnap.Oil);
                            }
                            if (hasProd)
                            {
                                int pCount = prodSnap != null ? prodSnap.Length : 0;
                                bw.Write(pCount);
                                for (int i = 0; i < pCount; i++)
                                {
                                    var pq = prodSnap[i];
                                    bw.Write(pq.EntityId);
                                    int len = pq.CommandIds != null ? Math.Min(255, pq.CommandIds.Length) : 0;
                                    bw.Write((byte)len);
                                    for (int j = 0; j < len; j++)
                                        bw.Write(pq.CommandIds[j]);
                                }
                            }
                            if (hasStructMeta)
                            {
                                bw.Write(structMetaMap.Count);
                                foreach (var sm in structMetaMap.Values)
                                {
                                    bw.Write(sm.Id);
                                    bw.Write(sm.TemplateId);
                                    bw.Write(sm.CellX);
                                    bw.Write(sm.CellY);
                                    bw.Write(sm.Width);
                                    bw.Write(sm.Height);
                                    bw.Write(sm.Life);
                                    bw.Write(sm.BuildFactor);
                                    bw.Write(sm.OwnerPlayerId);
                                }
                            }
                            if (hasTargetMeta)
                            {
                                bw.Write(targetMetaMap.Count);
                                foreach (var tm in targetMetaMap.Values)
                                {
                                    bw.Write(tm.Id);
                                    bw.Write(tm.TargetEntityId);
                                    bw.Write(tm.MoveX);
                                    bw.Write(tm.MoveY);
                                }
                            }
                            if (hasSpawnMeta)
                            {
                                bw.Write(spawnMetaMap.Count);
                                foreach (var dsm in spawnMetaMap.Values)
                                {
                                    bw.Write(dsm.Id);
                                    bw.Write(dsm.TemplateId);
                                    bw.Write(dsm.TeamRaw);
                                    bw.Write(dsm.TargetEntityId);
                                    bw.Write(dsm.OwnerPlayerId);
                                }
                            }
                            bw.Flush();
                            byte[] rawBytes = rawMs.ToArray();
                            using (var outMs = new MemoryStream(Math.Max(64, rawBytes.Length / 2)))
                            {
                                using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
                                    ds.Write(rawBytes, 0, rawBytes.Length);
                                compressed = outMs.ToArray();
                            }
                        }
                    }

                    if (gen != _asyncGeneration) return;

                    int totalLen = compressed.Length;
                    int totalChunks = Math.Max(1, (totalLen + ChunkSizeBytes - 1) / ChunkSizeBytes);
                    var chunks = new List<byte[]>(totalChunks);
                    for (int idx = 0; idx < totalChunks; idx++)
                    {
                        int offset = idx * ChunkSizeBytes;
                        int len = Math.Min(ChunkSizeBytes, totalLen - offset);
                        var chunk = new byte[len];
                        Buffer.BlockCopy(compressed, offset, chunk, 0, len);
                        chunks.Add(chunk);
                    }

                    _outgoingPosSyncQueue.Enqueue(new OutgoingPosSyncBatch
                    {
                        Generation = gen,
                        TargetPlayerId = targetPlayerId,
                        Seq = seq,
                        Tick = tick,
                        ApplyCorrection = applyLifeCorrection,
                        TotalCompressedLen = totalLen,
                        Chunks = chunks
                    });
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Async compress failed: {ex.Message}");
                }
                finally
                {
                    if (gen == _asyncGeneration)
                        Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
                }
            });
        }

        private static void OnPosDriftSyncChunkReceived(BinaryReader r)
        {
            int seq = r.ReadInt32();
            int tick = r.ReadInt32();
            bool applyCorrection = r.ReadBoolean();
            int totalChunks = r.ReadInt32();
            int chunkIdx = r.ReadInt32();
            int compressedTotalLen = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] part = r.ReadBytes(len);

            if (!_posSyncIncoming.TryGetValue(seq, out var buf))
            {
                buf = new PosSyncReceiveBuffer
                {
                    SimTick = tick,
                    ApplyCorrection = applyCorrection,
                    TotalChunks = totalChunks,
                    CompressedTotalLen = compressedTotalLen
                };
                _posSyncIncoming[seq] = buf;
            }
            buf.Chunks[chunkIdx] = part;
            if (buf.Chunks.Count < buf.TotalChunks) return;

            _posSyncIncoming.Remove(seq);
            // Prune stale incomplete sequences if any
            if (_posSyncIncoming.Count > 4) _posSyncIncoming.Clear();

            byte[] compressed = new byte[buf.CompressedTotalLen];
            int offset = 0;
            for (int i = 0; i < buf.TotalChunks; i++)
            {
                if (!buf.Chunks.TryGetValue(i, out byte[] p)) return;
                Buffer.BlockCopy(p, 0, compressed, offset, p.Length);
                offset += p.Length;
            }

            if (!_tickPosCache.TryGetValue(buf.SimTick, out var clientSamples) || clientSamples == null)
            {
                ModLogger.Warn($"[AsyncPosAlign] No cached client positions for Tick {buf.SimTick}; cannot measure/apply delta.");
                return;
            }

            var clientSnapshot = clientSamples.ToArray();
            bool hasClientEcon = _tickEconCache.TryGetValue(buf.SimTick, out EconSnapshot clientEcon);
            StateHasher.ProdQueueSample[] clientProdSnap =
                _tickProdCache.TryGetValue(buf.SimTick, out var clientProdList) && clientProdList != null
                    ? clientProdList.ToArray()
                    : Array.Empty<StateHasher.ProdQueueSample>();
            int gen = _asyncGeneration;
            int simTick = buf.SimTick;
            bool apply = buf.ApplyCorrection;

            Task.Run(() =>
            {
                try
                {
                    if (gen != _asyncGeneration) return;

                    var clientMap = new Dictionary<ulong, StateHasher.EntityPosSample>(clientSnapshot.Length);
                    for (int i = 0; i < clientSnapshot.Length; i++)
                        clientMap[clientSnapshot[i].Id] = clientSnapshot[i];

                    int count;
                    ushort hostMask;
                    bool flagApplyLife;
                    bool flagApplyPos;
                    bool flagIncludeStatic;
                    bool flagHasEcon;
                    bool flagHasProd;
                    bool flagHasStructMeta;
                    bool flagHasTargetMeta;
                    bool flagHasSpawnMeta;
                    List<StateHasher.EntityPosSample> hostSamples;
                    EconSnapshot hostEcon = default;
                    List<StateHasher.ProdQueueSample> hostProds = null;
                    List<StructSampleMeta> hostStructMetas = null;
                    Dictionary<ulong, DynamicTargetSample> hostTargetMetas = null;
                    List<DynamicSpawnMeta> hostSpawnMetas = null;
                    using (var inMs = new MemoryStream(compressed))
                    using (var ds = new DeflateStream(inMs, CompressionMode.Decompress))
                    using (var br = new BinaryReader(ds))
                    {
                        ushort firstU16 = br.ReadUInt16();
                        if (firstU16 == 0)
                        {
                            byte ver = br.ReadByte();
                            if (ver != 2) throw new InvalidDataException($"Unsupported PosDriftSync v{ver}");
                            hostMask = br.ReadUInt16();
                            byte flags = br.ReadByte();
                            flagApplyLife = (flags & 1) != 0;
                            flagApplyPos = (flags & 2) != 0;
                            flagIncludeStatic = (flags & 4) != 0;
                            flagHasEcon = (flags & 8) != 0;
                            flagHasProd = (flags & 16) != 0;
                            flagHasStructMeta = (flags & 32) != 0;
                            flagHasTargetMeta = (flags & 64) != 0;
                            flagHasSpawnMeta = (flags & 128) != 0;
                            int chunkSeq = br.ReadInt32();
                            int baselineSeq = br.ReadInt32();
                            count = (int)NetCommand.ReadVarUInt32(br);

                            Dictionary<ulong, StateHasher.EntityPosSample> prevBase = null;
                            if (baselineSeq != 0)
                            {
                                lock (_driftBaselineLock)
                                {
                                    if (_clientPrevDriftSeq == baselineSeq && _clientPrevDriftSamples != null)
                                        prevBase = _clientPrevDriftSamples;
                                }
                            }

                            hostSamples = new List<StateHasher.EntityPosSample>(count);
                            var nextBase = new Dictionary<ulong, StateHasher.EntityPosSample>(count);
                            if (flagHasTargetMeta) hostTargetMetas = new Dictionary<ulong, DynamicTargetSample>(Math.Min(count, 256));
                            if (flagHasStructMeta) hostStructMetas = new List<StructSampleMeta>(32);
                            if (flagHasSpawnMeta) hostSpawnMetas = new List<DynamicSpawnMeta>(64);

                            ulong prevId = 0UL;
                            for (int i = 0; i < count; i++)
                            {
                                ulong id = prevId + NetCommand.ReadVarUInt64(br);
                                prevId = id;
                                byte eFlags = br.ReadByte();
                                StateHasher.EntityPosSample sample;
                                if ((eFlags & 0x01) != 0)
                                {
                                    if (prevBase != null && prevBase.TryGetValue(id, out var bs))
                                        sample = bs;
                                    else
                                        sample = new StateHasher.EntityPosSample { Id = id };
                                }
                                else
                                {
                                    int life = (eFlags & 0x02) != 0 ? NetCommand.ReadZigZagVarInt32(br) : 0;
                                    float sx, sy;
                                    if ((eFlags & 0x04) != 0)
                                        ReadPacked5B(br, out sx, out sy);
                                    else
                                    {
                                        sx = br.ReadSingle();
                                        sy = br.ReadSingle();
                                    }
                                    sample = new StateHasher.EntityPosSample { Id = id, X = sx, Y = sy, Life = life };

                                    if ((eFlags & 0x08) != 0)
                                    {
                                        byte tf = br.ReadByte();
                                        ulong tgtId = (tf & 1) != 0 ? br.ReadUInt64() : 0UL;
                                        float mx = float.NaN, my = float.NaN;
                                        if ((tf & 2) != 0) { mx = br.ReadSingle(); my = br.ReadSingle(); }
                                        if (hostTargetMetas == null) hostTargetMetas = new Dictionary<ulong, DynamicTargetSample>();
                                        hostTargetMetas[id] = new DynamicTargetSample { Id = id, TargetEntityId = tgtId, MoveX = mx, MoveY = my };
                                    }
                                    if ((eFlags & 0x10) != 0)
                                    {
                                        if (hostStructMetas == null) hostStructMetas = new List<StructSampleMeta>();
                                        hostStructMetas.Add(new StructSampleMeta
                                        {
                                            Id = id,
                                            TemplateId = br.ReadUInt64(),
                                            CellX = NetCommand.ReadZigZagVarInt32(br),
                                            CellY = NetCommand.ReadZigZagVarInt32(br),
                                            Width = br.ReadByte(),
                                            Height = br.ReadByte(),
                                            Life = life,
                                            BuildFactor = br.ReadSingle(),
                                            OwnerPlayerId = br.ReadByte()
                                        });
                                    }
                                    if ((eFlags & 0x20) != 0)
                                    {
                                        if (hostSpawnMetas == null) hostSpawnMetas = new List<DynamicSpawnMeta>();
                                        hostSpawnMetas.Add(new DynamicSpawnMeta
                                        {
                                            Id = id,
                                            TemplateId = br.ReadUInt64(),
                                            TeamRaw = br.ReadByte(),
                                            TargetEntityId = br.ReadUInt64(),
                                            OwnerPlayerId = br.ReadByte()
                                        });
                                    }
                                }
                                hostSamples.Add(sample);
                                nextBase[id] = sample;
                            }

                            lock (_driftBaselineLock)
                            {
                                _clientPrevDriftSeq = chunkSeq;
                                _clientPrevDriftSamples = nextBase;
                            }

                            if (flagHasEcon)
                            {
                                hostEcon = new EconSnapshot
                                {
                                    Gold = NetCommand.ReadZigZagVarInt32(br),
                                    Wood = NetCommand.ReadZigZagVarInt32(br),
                                    Stone = NetCommand.ReadZigZagVarInt32(br),
                                    Iron = NetCommand.ReadZigZagVarInt32(br),
                                    Oil = NetCommand.ReadZigZagVarInt32(br)
                                };
                            }
                            if (flagHasProd)
                            {
                                int pCount = (int)NetCommand.ReadVarUInt32(br);
                                hostProds = new List<StateHasher.ProdQueueSample>(pCount);
                                for (int i = 0; i < pCount; i++)
                                {
                                    ulong eid = br.ReadUInt64();
                                    int len = br.ReadByte();
                                    var cmdIds = len > 0 ? new int[len] : Array.Empty<int>();
                                    for (int j = 0; j < len; j++)
                                        cmdIds[j] = NetCommand.ReadZigZagVarInt32(br);
                                    hostProds.Add(new StateHasher.ProdQueueSample { EntityId = eid, CommandIds = cmdIds });
                                }
                            }
                        }
                        else
                        {
                            hostMask = firstU16;
                            byte flags = br.ReadByte();
                            flagApplyLife = (flags & 1) != 0;
                            flagApplyPos = (flags & 2) != 0;
                            flagIncludeStatic = (flags & 4) != 0;
                            flagHasEcon = (flags & 8) != 0;
                            flagHasProd = (flags & 16) != 0;
                            flagHasStructMeta = (flags & 32) != 0;
                            flagHasTargetMeta = (flags & 64) != 0;
                            flagHasSpawnMeta = (flags & 128) != 0;
                            count = br.ReadInt32();
                            hostSamples = new List<StateHasher.EntityPosSample>(count);
                            for (int i = 0; i < count; i++)
                            {
                                hostSamples.Add(new StateHasher.EntityPosSample
                                {
                                    Id = br.ReadUInt64(),
                                    X = br.ReadSingle(),
                                    Y = br.ReadSingle(),
                                    Life = br.ReadInt32()
                                });
                            }
                            if (flagHasEcon)
                            {
                                hostEcon = new EconSnapshot
                                {
                                    Gold = br.ReadInt32(),
                                    Wood = br.ReadInt32(),
                                    Stone = br.ReadInt32(),
                                    Iron = br.ReadInt32(),
                                    Oil = br.ReadInt32()
                                };
                            }
                            if (flagHasProd)
                            {
                                int pCount = br.ReadInt32();
                                hostProds = new List<StateHasher.ProdQueueSample>(pCount);
                                for (int i = 0; i < pCount; i++)
                                {
                                    ulong eid = br.ReadUInt64();
                                    int len = br.ReadByte();
                                    var cmdIds = len > 0 ? new int[len] : Array.Empty<int>();
                                    for (int j = 0; j < len; j++)
                                        cmdIds[j] = br.ReadInt32();
                                    hostProds.Add(new StateHasher.ProdQueueSample { EntityId = eid, CommandIds = cmdIds });
                                }
                            }
                            if (flagHasStructMeta)
                            {
                                int sCount = br.ReadInt32();
                                hostStructMetas = new List<StructSampleMeta>(sCount);
                                for (int i = 0; i < sCount; i++)
                                {
                                    hostStructMetas.Add(new StructSampleMeta
                                    {
                                        Id = br.ReadUInt64(),
                                        TemplateId = br.ReadUInt64(),
                                        CellX = br.ReadInt32(),
                                        CellY = br.ReadInt32(),
                                        Width = br.ReadByte(),
                                        Height = br.ReadByte(),
                                        Life = br.ReadInt32(),
                                        BuildFactor = br.ReadSingle(),
                                        OwnerPlayerId = br.ReadByte()
                                    });
                                }
                            }
                            if (flagHasTargetMeta)
                            {
                                int tCount = br.ReadInt32();
                                hostTargetMetas = new Dictionary<ulong, DynamicTargetSample>(tCount);
                                for (int i = 0; i < tCount; i++)
                                {
                                    var tm = new DynamicTargetSample
                                    {
                                        Id = br.ReadUInt64(),
                                        TargetEntityId = br.ReadUInt64(),
                                        MoveX = br.ReadSingle(),
                                        MoveY = br.ReadSingle()
                                    };
                                    hostTargetMetas[tm.Id] = tm;
                                }
                            }
                            if (flagHasSpawnMeta)
                            {
                                int dCount = br.ReadInt32();
                                hostSpawnMetas = new List<DynamicSpawnMeta>(dCount);
                                for (int i = 0; i < dCount; i++)
                                {
                                    hostSpawnMetas.Add(new DynamicSpawnMeta
                                    {
                                        Id = br.ReadUInt64(),
                                        TemplateId = br.ReadUInt64(),
                                        TeamRaw = br.ReadByte(),
                                        TargetEntityId = br.ReadUInt64(),
                                        OwnerPlayerId = br.ReadByte()
                                    });
                                }
                            }
                        }
                    }

                    if (gen != _asyncGeneration) return;

                    bool effectiveApplyLife = apply && flagApplyLife;
                    int comparedCount = 0;
                    int drifted001 = 0, drifted01 = 0, drifted1 = 0, drifted5 = 0;
                    int lifeDiffCount = 0, missingOnHost = 0, missingOnClient = 0;
                    float maxDelta = 0f;
                    ulong maxDeltaId = 0;
                    ulong hostMaxId = 0;
                    double sumSq = 0.0;
                    List<EntityDeltaFix> deltas = apply ? new List<EntityDeltaFix>(64) : null;
                    HashSet<ulong> driftedPosIds15 = (apply && flagHasTargetMeta) ? new HashSet<ulong>() : null;
                    List<EntityLifeFix> lifeFixes = effectiveApplyLife ? new List<EntityLifeFix>(64) : null;
                    var hostIds = new HashSet<ulong>();
                    var hostSampleMap = (flagHasSpawnMeta || flagHasStructMeta)
                        ? new Dictionary<ulong, StateHasher.EntityPosSample>(hostSamples.Count)
                        : null;
                    Dictionary<ulong, byte> hostSpawnTeamMap = null;
                    if (flagHasSpawnMeta && hostSpawnMetas != null)
                    {
                        hostSpawnTeamMap = new Dictionary<ulong, byte>(hostSpawnMetas.Count);
                        for (int i = 0; i < hostSpawnMetas.Count; i++)
                            hostSpawnTeamMap[hostSpawnMetas[i].Id] = hostSpawnMetas[i].TeamRaw;
                    }

                    for (int i = 0; i < hostSamples.Count; i++)
                    {
                        var hs = hostSamples[i];
                        hostIds.Add(hs.Id);
                        if (hostSampleMap != null) hostSampleMap[hs.Id] = hs;
                        if (hs.Id > hostMaxId) hostMaxId = hs.Id;
                        if (!clientMap.TryGetValue(hs.Id, out StateHasher.EntityPosSample cs))
                        {
                            missingOnClient++;
                            continue;
                        }

                        byte expTeamFilter = 0;
                        if (hostSpawnTeamMap != null && hostSpawnTeamMap.TryGetValue(hs.Id, out byte hTeamRaw))
                        {
                            bool hIsPlayer = hTeamRaw == 0;
                            bool cIsZombie = cs.IsZombie;
                            if (hIsPlayer == cIsZombie)
                                continue; // Skip ID-collided entity between player unit and zombie.
                            expTeamFilter = hIsPlayer ? (byte)1 : (byte)2;
                        }

                        // Host-authoritative HP: apply the delta measured at the same epoch tick so
                        // damage taken locally since then is preserved.
                        if (hs.Life != cs.Life)
                        {
                            lifeDiffCount++;
                            if (effectiveApplyLife)
                                lifeFixes.Add(new EntityLifeFix { Id = hs.Id, Delta = hs.Life - cs.Life, Kill = hs.Life <= 0, ExpectedTeamFilter = expTeamFilter });
                        }

                        if (!cs.IsDynamic) continue;
                        comparedCount++;

                        float dx = hs.X - cs.X;
                        float dy = hs.Y - cs.Y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (dist > 0.001f)
                        {
                            drifted001++;
                            sumSq += dist * dist;
                        }
                        if (dist > 0.01f) drifted01++;
                        if (dist > 0.1f) drifted1++;
                        if (dist > 0.5f) drifted5++;

                        if (dist > maxDelta)
                        {
                            maxDelta = dist;
                            maxDeltaId = hs.Id;
                        }

                        if (driftedPosIds15 != null && dist >= 0.15f)
                            driftedPosIds15.Add(hs.Id);
                        if (apply && dist >= MinAsyncPosSnapCells && (cs.IsZombie || dist <= MaxAsyncNudgeCells))
                            deltas.Add(new EntityDeltaFix { Id = hs.Id, Dx = dx, Dy = dy });
                    }

                    // Dynamic entities alive on the client but absent on the Host at the same epoch tick, whose
                    // ID the Host has already allocated past, are candidates for host-killed stragglers.
                    // Never kill a large batch when MissingOnClient is also non-trivial: that signals a
                    // DXGuid ID-shift or unspawned wave where mass-killing would wipe out Client's own horde.
                    var missingOnHostDynamicIds = effectiveApplyLife ? new List<ulong>(16) : null;
                    for (int i = 0; i < clientSnapshot.Length; i++)
                    {
                        var cs = clientSnapshot[i];
                        if (!cs.IsDynamic) continue;
                        if ((hostMask & (1 << (int)(cs.Id & 0xFUL))) == 0) continue;
                        if (hostIds.Contains(cs.Id) || cs.Id > hostMaxId) continue;
                        missingOnHost++;
                        missingOnHostDynamicIds?.Add(cs.Id);
                    }
                    if (effectiveApplyLife && missingOnHostDynamicIds != null
                        && missingOnHost > 0 && missingOnHost <= 8 && missingOnClient <= 4)
                    {
                        for (int i = 0; i < missingOnHostDynamicIds.Count; i++)
                            lifeFixes.Add(new EntityLifeFix { Id = missingOnHostDynamicIds[i], Delta = 0, Kill = true });
                    }

                    // Phase 2A: Resource deltas
                    bool applyEconCorrection = false;
                    int deltaGold = 0, deltaWood = 0, deltaStone = 0, deltaIron = 0, deltaOil = 0;
                    if (apply && flagHasEcon && hasClientEcon)
                    {
                        deltaGold = hostEcon.Gold - clientEcon.Gold;
                        deltaWood = hostEcon.Wood - clientEcon.Wood;
                        deltaStone = hostEcon.Stone - clientEcon.Stone;
                        deltaIron = hostEcon.Iron - clientEcon.Iron;
                        deltaOil = hostEcon.Oil - clientEcon.Oil;
                        applyEconCorrection = deltaGold != 0 || deltaWood != 0 || deltaStone != 0 || deltaIron != 0 || deltaOil != 0;
                    }

                    // Phase 2B: Production queue fixes
                    List<ProdQueueFixItem> prodQueueFixes = null;
                    if (apply && flagHasProd)
                    {
                        var clientProdMap = new Dictionary<ulong, int[]>(clientProdSnap.Length);
                        for (int i = 0; i < clientProdSnap.Length; i++)
                            clientProdMap[clientProdSnap[i].EntityId] = clientProdSnap[i].CommandIds ?? Array.Empty<int>();
                        var hostProdMap = new Dictionary<ulong, int[]>(hostProds != null ? hostProds.Count : 0);
                        if (hostProds != null)
                        {
                            for (int i = 0; i < hostProds.Count; i++)
                            {
                                var hp = hostProds[i];
                                int[] hCmds = hp.CommandIds ?? Array.Empty<int>();
                                hostProdMap[hp.EntityId] = hCmds;
                                int[] cCmds = clientProdMap.TryGetValue(hp.EntityId, out var found) ? found : Array.Empty<int>();
                                if (!IntArraysEqual(hCmds, cCmds))
                                {
                                    if (prodQueueFixes == null) prodQueueFixes = new List<ProdQueueFixItem>(8);
                                    prodQueueFixes.Add(new ProdQueueFixItem
                                    {
                                        EntityId = hp.EntityId,
                                        ExpectedClientCommandIds = cCmds,
                                        HostCommandIds = hCmds
                                    });
                                }
                            }
                        }
                        foreach (var kvp in clientProdMap)
                        {
                            if (!hostProdMap.ContainsKey(kvp.Key))
                            {
                                if (prodQueueFixes == null) prodQueueFixes = new List<ProdQueueFixItem>(8);
                                prodQueueFixes.Add(new ProdQueueFixItem
                                {
                                    EntityId = kvp.Key,
                                    ExpectedClientCommandIds = kvp.Value,
                                    HostCommandIds = Array.Empty<int>()
                                });
                            }
                        }
                    }

                    // Phase 2C: Small static structure alignment (missing/extra <= 8)
                    List<StructSampleMeta> structBuilds = null;
                    List<ulong> structDestroys = null;
                    if (apply && flagHasStructMeta && flagIncludeStatic && hostStructMetas != null)
                    {
                        var missingStructs = new List<StructSampleMeta>(8);
                        for (int i = 0; i < hostStructMetas.Count; i++)
                        {
                            var sm = hostStructMetas[i];
                            if (!clientMap.ContainsKey(sm.Id))
                                missingStructs.Add(sm);
                        }
                        var extraStructs = new List<ulong>(8);
                        for (int i = 0; i < clientSnapshot.Length; i++)
                        {
                            var cs = clientSnapshot[i];
                            if (!cs.IsStructure) continue;
                            if ((hostMask & (1 << (int)(cs.Id & 0xFUL))) == 0) continue;
                            if (!hostIds.Contains(cs.Id))
                                extraStructs.Add(cs.Id);
                        }
                        if (missingStructs.Count <= 8 && extraStructs.Count <= 8)
                        {
                            if (missingStructs.Count > 0)
                            {
                                missingStructs.Sort((a, b) => a.Id.CompareTo(b.Id));
                                structBuilds = missingStructs;
                            }
                            if (extraStructs.Count > 0)
                            {
                                extraStructs.Sort();
                                structDestroys = extraStructs;
                            }
                        }
                    }

                    // Phase 3B: Small dynamic entity spawns (1..32 missing on client, <= 64 missing on host)
                    List<DynamicSpawnSample> dynamicSpawns = null;
                    if (apply && flagHasSpawnMeta && hostSpawnMetas != null && hostSampleMap != null && missingOnHost <= 64)
                    {
                        var pendingSpawns = new List<DynamicSpawnSample>(32);
                        for (int i = 0; i < hostSpawnMetas.Count; i++)
                        {
                            var dsm = hostSpawnMetas[i];
                            if (clientMap.ContainsKey(dsm.Id)) continue;
                            if (!hostSampleMap.TryGetValue(dsm.Id, out var hs)) continue;
                            pendingSpawns.Add(new DynamicSpawnSample
                            {
                                Id = dsm.Id,
                                TemplateId = dsm.TemplateId,
                                X = hs.X,
                                Y = hs.Y,
                                Life = hs.Life,
                                TeamRaw = dsm.TeamRaw,
                                TargetEntityId = dsm.TargetEntityId,
                                OwnerPlayerId = dsm.OwnerPlayerId
                            });
                        }
                        if (pendingSpawns.Count > 0 && pendingSpawns.Count <= 32)
                        {
                            pendingSpawns.Sort((a, b) => a.Id.CompareTo(b.Id));
                            dynamicSpawns = pendingSpawns;
                        }
                    }

                    float rmsDelta = drifted001 > 0 ? (float)Math.Sqrt(sumSq / drifted001) : 0f;
                    bool exceededThreshold = maxDelta >= MaxAsyncTeleportCells;
                    bool emergencyPosOverride = maxDelta >= 3.5f || rmsDelta >= 1.2f;
                    bool effectiveApplyPos = apply && (flagApplyPos || emergencyPosOverride);

                    if (gen != _asyncGeneration) return;

                    _incomingPosAlignQueue.Enqueue(new PendingPosAlignApply
                    {
                        Generation = gen,
                        Tick = simTick,
                        ApplyCorrection = effectiveApplyLife,
                        ApplyPosCorrection = effectiveApplyPos,
                        ComparedCount = comparedCount,
                        Drifted001 = drifted001,
                        Drifted01 = drifted01,
                        Drifted1 = drifted1,
                        Drifted5 = drifted5,
                        MaxDelta = maxDelta,
                        RmsDelta = rmsDelta,
                        MaxDeltaEntityId = maxDeltaId,
                        HostMaxId = hostMaxId,
                        ExceededThreshold = exceededThreshold,
                        HasTargetFixesPayload = apply && flagHasTargetMeta,
                        Deltas = effectiveApplyPos ? deltas : null,
                        LifeFixes = lifeFixes,
                        LifeDiffCount = lifeDiffCount,
                        MissingOnHost = missingOnHost,
                        MissingOnClient = missingOnClient,
                        ApplyEconCorrection = applyEconCorrection,
                        DeltaGold = deltaGold,
                        DeltaWood = deltaWood,
                        DeltaStone = deltaStone,
                        DeltaIron = deltaIron,
                        DeltaOil = deltaOil,
                        ProdQueueFixes = prodQueueFixes,
                        StructBuilds = structBuilds,
                        StructDestroys = structDestroys,
                        TargetFixes = (apply && flagHasTargetMeta) ? hostTargetMetas : null,
                        DriftedPosIds15 = driftedPosIds15,
                        DynamicSpawns = dynamicSpawns
                    });
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Async decompress/scan failed: {ex.Message}");
                }
            });
        }

        private static void DumpHostEntitiesOnDesync(int tick)
        {
            if (_firstChecksumPosDumped) return;
            _firstChecksumPosDumped = true;
            DumpPeerEntities(tick, "coop_desync_pos_host.txt");
        }

        private static void DumpPeerEntities(int tick, string fileName)
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;
                string savesDir = GameBridge.GetSavesDirectory();
                if (string.IsNullOrEmpty(savesDir)) return;
                string path = Path.Combine(savesDir, fileName);
                var sb = new System.Text.StringBuilder(1 << 18);
                sb.Append("# tick=").Append(tick).Append('\n');
                int count = 0;
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    if (e.GetType() == typeof(DXEntity) || string.Equals(e.GetType().Name, "Raven", StringComparison.Ordinal) || GameBridge.IsColonistType(e.GetType())) continue;
                    var b = e.Body;
                    float bx = b != null ? b.PositionComputed.X : float.NaN;
                    float by = b != null ? b.PositionComputed.Y : float.NaN;
                    sb.Append(e.ID).Append('|')
                      .Append(e.Position.X.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.Position.Y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(bx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(by.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.GetType().Name).Append('\n');
                    count++;
                }
                File.WriteAllText(path, sb.ToString());
                ModLogger.Info($"[StateChecksum] Dumped {count} entity positions (tick {tick}) -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[StateChecksum] DumpPeerEntities failed: {ex.Message}");
            }
        }

        private static void TryAssembleSnapshot()
        {
            if (_expectedChunks <= 0 || _incomingChunks.Count < _expectedChunks) return;

            for (int i = 0; i < _expectedChunks; i++)
                if (!_incomingChunks.ContainsKey(i)) return;

            int total = 0;
            for (int i = 0; i < _expectedChunks; i++) total += _incomingChunks[i].Length;
            byte[] savBytes = new byte[total];
            int offset = 0;
            for (int i = 0; i < _expectedChunks; i++)
            {
                byte[] part = _incomingChunks[i];
                Buffer.BlockCopy(part, 0, savBytes, offset, part.Length);
                offset += part.Length;
            }
            _incomingChunks.Clear();
            _expectedChunks = 0;

            ApplyReceivedSnapshotOnClient(savBytes, _incomingCheckContent, _incomingRandomSeed, _incomingEntityIdCounter);
        }

        private static void ApplyReceivedSnapshotOnClient(byte[] savBytes, string chkContent, int randomSeed,
            long hostEntityIdCounter)
        {
            try
            {
                string savesDir = GameBridge.GetSavesDirectory();
                // Reuse the Host's exact file stem: the engine's .zxcheck sidecar appears to be bound
                // to the save name (renaming it made ReadSaveFile fail with "error while loading").
                string clientSaveName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, clientSaveName + ".zxsav");
                string chkPath = Path.Combine(savesDir, clientSaveName + ".zxcheck");

                File.WriteAllBytes(savPath, savBytes);
                // ZXGame validates saves via Path.ChangeExtension(path, ".zxcheck") and the sidecar
                // is bound to the save name, hence we reuse the Host's file stem above.
                File.WriteAllText(chkPath, chkContent);

                ModLogger.Info($"[CoopSessionManager] Saved received snapshot to {savPath} ({savBytes.Length:N0} bytes, check={chkContent.Length} chars). Deferring load to next frame...");

                // Defer: this runs inside network packet delivery. Loading here re-enters the engine
                // and previously blew the main-thread stack under Chaos.
                _incomingRandomSeed = randomSeed;
                _incomingEntityIdCounter = hostEntityIdCounter;
                _pendingClientSnapshotLoadPath = savPath;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to apply received snapshot on Client", ex);
            }
        }

        /// <summary>Client-side snapshot load, executed from OnFrameTick before packet polling.</summary>
        private static void BeginClientSnapshotLoad(string savPath)
        {
            try
            {
                CaptureLocalControlGroupsForMidGameReload();
                if (_pendingResyncSelectionGroups == null)
                {
                    // Initial join or reconnect from main menu: clear all 9 control-group slots
                    // so Host control groups baked into coop_host_snapshot.zxsav are never inherited.
                    _pendingResyncSelectionGroups = new ulong[9][];
                }
                IsInSyncedLevel = false;
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;

                if (!GameBridge.IsSaveLoadable(savPath))
                {
                    ModLogger.Error($"[CoopSessionManager] ZXGame rejected the received snapshot as unloadable: {savPath}");
                    GameBridge.ShowTopBanner("Received snapshot failed the save-file validity check", Color.OrangeRed, 4000);
                    return;
                }

                // Enable the deterministic sandbox and defer ID calibration to the loader completion
                // postfix — the level is not restored when LoadSaveFileAndEnter returns.
                RequestSnapshotCalibration(_incomingRandomSeed, _incomingEntityIdCounter);
                GameBridge.PrepareForProgrammaticLoad();

                ModLogger.Info($"[CoopSessionManager] Client calling LoadSaveFileAndEnter('{savPath}')...");
                GameBridge.LoadSaveFileAndEnter(savPath);
                ModLogger.Info("[CoopSessionManager] Client LoadSaveFileAndEnter returned; driving loader fades.");

                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Client snapshot load threw", ex);
            }
        }

        /// <summary>
        /// Advances the engine's black loading-fade animation while the Client waits to enter the
        /// level. LoadGameAndEnter gates the real load behind a DXAnimation chain that never runs if
        /// the background window's scene does not advance it.
        /// </summary>
        public static void NotifyExternalLoadStarted()
        {
            _kickingLoadFade = true;
            _loadFadeKickFrames = 0;
            _loadFadeKickStartTicks = -1.0;
        }

        private static void TickLoadingFade()
        {
            if (!_kickingLoadFade) return;

            if (!_awaitingSnapshotLoad && !_hostWaitingOwnReload && !LockstepClock.LoadingLevel
                && !GameBridge.IsGameLoading() && !GameBridge.IsLoadingFadeInScene())
            {
                _kickingLoadFade = false;
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (_loadFadeKickStartTicks < 0.0) _loadFadeKickStartTicks = now;
            else if (now - _loadFadeKickStartTicks >= LoadFadeTimeoutMs)
            {
                _kickingLoadFade = false;
                ModLogger.Error("[CoopSessionManager] Client snapshot load did not complete within 30s of driving the loader fades.");
                return;
            }

            GameBridge.KickLoadingFade(700.0);

            ++_loadFadeKickFrames;
            if (ModLogger.IsVerbose ? (_loadFadeKickFrames % 60) == 0 : (_loadFadeKickFrames >= 300 && (_loadFadeKickFrames % 300) == 0))
                ModLogger.Info($"[LoadFade] frames={_loadFadeKickFrames} awaited={_awaitingSnapshotLoad} loading={LockstepClock.LoadingLevel} zxLoading={GameBridge.IsGameLoading()} tick={DXGame.Current?.IAIterationInt ?? -1}");
        }
    }

    /// <summary>
    /// Hooks ZXGame.OnStartFrame and ZXSystemGameLevel.#=zmTU4kueQctVr (InitLevel)
    /// to drive CoopSessionManager every frame and auto-sync when Host enters a level.
    /// </summary>
    [HarmonyPatch]
    public static class SessionLifecyclePatches
    {
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPostfix]
        public static void DXGame_StartFrame_Postfix()
        {
            CoopSessionManager.OnFrameTick();
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_InitLevel
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel);
            }

            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Determinism.LockstepClock.IsDeterministicModeActive)
                    Determinism.LockstepClock.ResetSyncedRandomStreamsForInitLevel();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!CoopSessionManager.IsSessionActive) return;

                if (CoopSessionManager.Transport.IsHost)
                {
                    // Snapshot export is triggered from CoopSessionManager.OnLevelLoadComplete, not
                    // here: InitLevel fires while the save is still deserializing.
                    ModLogger.Info("[SessionLifecyclePatches] Host level initialized; snapshot will be sent after load completes.");
                }
                else
                {
                    // Do NOT calibrate here: InitLevel runs on the loader's background thread and
                    // the level is not fully restored yet (see LevelLoadCompletePatch).
                    ModLogger.Info("[SessionLifecyclePatches] Client InitLevel fired; waiting for loader completion.");
                }
            }
        }
    }
}
