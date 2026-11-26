using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Patches;
using ZX;

namespace TABCoopMod.Sync
{
    public enum CoopGameMode : byte
    {
        Archon = 0,
        MultiFaction = 1
    }

    public enum MultiFactionDefeatRule : byte
    {
        AllBasesLost = 0,
        AnyBaseLost = 1
    }

    public enum MultiFactionSpawnMode : byte
    {
        Separated = 0,
        Clustered = 1
    }

    public enum MutationTendency : byte
    {
        Balanced = 0,
        HeavyArmor = 1,
        Caustic = 2,
        SwarmHunter = 3
    }

    /// <summary>
    /// Phase 4 M1 deterministic virtual faction ownership layer.
    /// Keeps all players on ZXTeamType.Player (zero friendly fire, shared fog of war)
    /// while isolating per-player entity ownership (EntityId -> PlayerId 1..N) across
    /// initial spawn, construction, unit training, building upgrades, neutral takeover,
    /// CommandCenter destruction (AllBasesLost exile handover vs AnyBaseLost), and snapshot sync.
    /// </summary>
    public static class FactionOwnershipTracker
    {
        public const string ScriptVarKey = "TABCoop_MultiFactionState";

        private static readonly object _sync = new object();
        private static readonly Dictionary<ulong, byte> _entityOwners = new Dictionary<ulong, byte>();
        private static readonly Dictionary<ulong, byte> _pendingCcOwners = new Dictionary<ulong, byte>();
        private static readonly HashSet<byte> _exiledPlayers = new HashSet<byte>();
        private static readonly Dictionary<ulong, byte> _subSwarmEventTargets = new Dictionary<ulong, byte>();
        private static readonly Dictionary<ulong, byte> _zombieSwarmTargets = new Dictionary<ulong, byte>();

        public static byte ActivePlayerCount { get; private set; }
        public static bool IsInitialized { get; private set; }
        public static byte LocalPlayerId => CommandInterceptor.LocalPlayerId;
        public static MultiFactionSpawnMode ActiveSpawnMode { get; private set; } = MultiFactionSpawnMode.Separated;
        public static MutationTendency ActiveMutationTendency { get; private set; } = MutationTendency.Balanced;
        public static int ActiveMapSeed { get; private set; }

        public static Color GetPlayerColor(byte playerId)
        {
            return CoopLimits.GetPlayerColor(playerId);
        }

        public static int TrackedEntityCount
        {
            get { lock (_sync) return _entityOwners.Count; }
        }

        public static void CopyTrackedOwners(List<KeyValuePair<ulong, byte>> dest)
        {
            if (dest == null) return;
            dest.Clear();
            lock (_sync)
            {
                if (_entityOwners.Count == 0) return;
                if (dest.Capacity < _entityOwners.Count)
                    dest.Capacity = _entityOwners.Count;
                foreach (var kv in _entityOwners)
                    dest.Add(kv);
            }
        }

        public static void RegisterPendingCcOwners(IReadOnlyList<DXEntity> orderedCcs)
        {
            if (orderedCcs == null) return;
            lock (_sync)
            {
                _pendingCcOwners.Clear();
                for (int i = 0; i < orderedCcs.Count; i++)
                {
                    var cc = orderedCcs[i];
                    if (cc != null && cc.ID != 0UL)
                        _pendingCcOwners[cc.ID] = (byte)(i + 1);
                }
            }
        }

        public static void Reset()
        {
            lock (_sync)
            {
                _entityOwners.Clear();
                _pendingCcOwners.Clear();
                _exiledPlayers.Clear();
                _subSwarmEventTargets.Clear();
                _zombieSwarmTargets.Clear();
                ActivePlayerCount = 0;
                IsInitialized = false;
                ActiveSpawnMode = MultiFactionSpawnMode.Separated;
                ActiveMutationTendency = MutationTendency.Balanced;
                ActiveMapSeed = 0;
            }
            GameBridge.LocalEntityControlFilter = null;
            GameBridge.InvalidateCommandCenterCache();
            FactionContextManager.Reset();
            WonderCrowdfundManager.Reset();
            MultiWaveSwarmContext.Reset();
            GameBridge.SetZeroFeeMayorActive(false);
        }

        public static void SetGeneratedMapMetadata(MultiFactionSpawnMode spawnMode, MutationTendency tendency, int mapSeed)
        {
            lock (_sync)
            {
                ActiveSpawnMode = spawnMode;
                ActiveMutationTendency = tendency;
                ActiveMapSeed = mapSeed;
            }
        }

        public static void SetSubSwarmEventTarget(ulong eventRequestId, byte targetPlayerId)
        {
            if (eventRequestId == 0UL || targetPlayerId == 0) return;
            lock (_sync)
            {
                _subSwarmEventTargets[eventRequestId] = targetPlayerId;
            }
        }

        public static byte GetSubSwarmEventTarget(ulong eventRequestId)
        {
            if (eventRequestId == 0UL) return 0;
            lock (_sync)
            {
                return _subSwarmEventTargets.TryGetValue(eventRequestId, out byte pid) ? pid : (byte)0;
            }
        }

        public static void ClearSubSwarmEventTarget(ulong eventRequestId)
        {
            if (eventRequestId == 0UL) return;
            lock (_sync)
            {
                _subSwarmEventTargets.Remove(eventRequestId);
            }
        }

        public static void SetZombieSwarmTarget(ulong zombieEntityId, byte targetPlayerId)
        {
            if (zombieEntityId == 0UL || targetPlayerId == 0) return;
            lock (_sync)
            {
                _zombieSwarmTargets[zombieEntityId] = targetPlayerId;
            }
        }

        public static void InstallControlFilterIfNeeded()
        {
            if (CoopSessionManager.IsMultiFactionActive)
                GameBridge.LocalEntityControlFilter = entity => CanPlayerControl(CommandInterceptor.LocalPlayerId, entity);
            else
                GameBridge.LocalEntityControlFilter = null;
        }

        public static byte GetOwner(ulong entityId)
        {
            if (entityId == 0UL) return 0;
            lock (_sync)
            {
                return _entityOwners.TryGetValue(entityId, out byte pid) ? pid : (byte)0;
            }
        }

        public static byte GetOwner(DXEntity entity)
        {
            return entity == null ? (byte)0 : GetOwner(entity.ID);
        }

        public static void SetOwner(ulong entityId, byte playerId)
        {
            if (entityId == 0UL || (entityId & 0x8000000000000000UL) != 0)
                return;
            lock (_sync)
            {
                if (playerId == 0)
                    _entityOwners.Remove(entityId);
                else
                    _entityOwners[entityId] = playerId;
            }
            if (CoopSessionManager.IsMultiFactionActive && playerId >= 1 && playerId <= 3)
            {
                var ent = DXGame.Current?.GetEntityByID(entityId);
                if (ent != null) GameBridge.RefreshEntityFactionColor(ent, GetPlayerColor(playerId));
            }
        }

        public static void RemoveEntity(ulong entityId)
        {
            if (entityId == 0UL) return;
            lock (_sync)
            {
                _entityOwners.Remove(entityId);
                _zombieSwarmTargets.Remove(entityId);
            }
        }

        public static bool IsExiled(byte playerId)
        {
            if (playerId == 0) return false;
            lock (_sync)
            {
                return _exiledPlayers.Contains(playerId);
            }
        }

        public static bool CanPlayerControl(byte playerId, DXEntity entity)
        {
            if (!CoopSessionManager.IsMultiFactionActive)
                return true;
            if (entity == null || entity.Disposed || playerId == 0)
                return false;
            if (IsExiled(playerId))
                return false;
            if (!GameBridge.IsPlayerEntity(entity))
                return false;

            byte owner = GetOwner(entity.ID);
            return owner == playerId;
        }

        public static bool CanLocalPlayerSelect(DXEntity entity)
        {
            if (!CoopSessionManager.IsMultiFactionActive)
                return true;
            if (entity == null || entity.Disposed)
                return false;
            if (!GameBridge.IsPlayerEntity(entity))
                return true; // Allow inspecting zombies/neutral ruins, just like vanilla.

            byte localId = CommandInterceptor.LocalPlayerId;
            if (localId == 0 || IsExiled(localId))
                return false;

            byte owner = GetOwner(entity.ID);
            return owner == localId;
        }

        public static int CountAliveCommandCenters()
        {
            return GameBridge.GetAllCommandCenters(aliveOnly: true).Count;
        }

        public static DXEntity GetPrimaryCommandCenterForPlayer(byte playerId)
        {
            var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            for (int i = 0; i < ccs.Count; i++)
            {
                if (GetOwner(ccs[i].ID) == playerId)
                    return ccs[i];
            }
            if (CoopSessionManager.IsMultiFactionActive)
                return null;
            return ccs.Count > 0 ? ccs[0] : null;
        }

        public static List<DXEntity> GetAliveCommandCentersForPlayer(byte playerId)
        {
            var result = new List<DXEntity>();
            var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            for (int i = 0; i < ccs.Count; i++)
            {
                if (GetOwner(ccs[i].ID) == playerId)
                    result.Add(ccs[i]);
            }
            return result;
        }

        /// <summary>
        /// Deterministically assigns initial ownership for all CommandCenters and nearby player
        /// units/structures on a fresh Multi-Faction match.
        /// </summary>
        public static void AssignInitialOwnershipFromLevel(int playerCount)
        {
            int count = Math.Max(1, Math.Min(3, playerCount));
            var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            var ccOwners = new List<(DXEntity Cc, byte PlayerId)>(ccs.Count);

            lock (_sync)
            {
                _entityOwners.Clear();
                _exiledPlayers.Clear();
                ActivePlayerCount = (byte)count;

                var usedPids = new HashSet<byte>();
                var assignedCcs = new byte[ccs.Count];
                for (int i = 0; i < ccs.Count; i++)
                {
                    if (_pendingCcOwners.TryGetValue(ccs[i].ID, out byte preferred)
                        && preferred >= 1 && preferred <= count && !usedPids.Contains(preferred))
                    {
                        assignedCcs[i] = preferred;
                        usedPids.Add(preferred);
                    }
                }
                _pendingCcOwners.Clear();

                byte nextSequential = 1;
                for (int i = 0; i < ccs.Count; i++)
                {
                    byte pid = assignedCcs[i];
                    if (pid == 0)
                    {
                        while (nextSequential <= count && usedPids.Contains(nextSequential))
                            nextSequential++;
                        pid = nextSequential <= count ? nextSequential++ : (byte)1;
                        usedPids.Add(pid);
                    }
                    _entityOwners[ccs[i].ID] = pid;
                    ccOwners.Add((ccs[i], pid));
                }

                var game = DXGame.Current;
                var entities = GameBridge.GetEntitiesList(game);
                if (entities != null)
                {
                    for (int i = 0; i < entities.Count; i++)
                    {
                        AssignEntityToNearestCcLocked(entities[i], ccOwners);
                    }
                }
                else if (game?.Entities != null)
                {
                    foreach (var ent in game.Entities)
                    {
                        AssignEntityToNearestCcLocked(ent, ccOwners);
                    }
                }

                IsInitialized = true;
            }

            // On fresh matches (tick <= 5), clear any lingering zombies inside a 28-cell safe
            // perimeter around all starter CommandCenters as a safety net.
            int initTick = DXGame.Current?.IAIterationInt ?? 0;
            if (initTick <= 5)
            {
                for (int i = 0; i < ccOwners.Count; i++)
                {
                    ClearNearbyZombiesLive(ccOwners[i].Cc.Cell, 28);
                }
            }

            InstallControlFilterIfNeeded();
            FactionContextManager.InitializeFromCurrentLevelState(count);
            WonderCrowdfundManager.Initialize(count);
            GameBridge.SetZeroFeeMayorActive(CoopSessionManager.IsMultiFactionActive && !FactionContextManager.ZeroTransferFeeActive);
            RefreshAllTrackedEntityColors();
            ModLogger.Info($"[FactionOwnership] Initialized ownership for {count} players across {ccOwners.Count} CCs (tracked={TrackedEntityCount}).");
        }

        public static void RefreshAllTrackedEntityColors()
        {
            var game = DXGame.Current;
            if (game == null || !CoopSessionManager.IsMultiFactionActive) return;
            List<KeyValuePair<ulong, byte>> snapshot;
            lock (_sync) snapshot = _entityOwners.ToList();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var ent = game.GetEntityByID(snapshot[i].Key);
                if (ent != null && !ent.Disposed)
                    GameBridge.RefreshEntityFactionColor(ent, GetPlayerColor(snapshot[i].Value));
            }
        }

        private static void AssignEntityToNearestCcLocked(DXEntity entity, List<(DXEntity Cc, byte PlayerId)> ccOwners)
        {
            if (entity == null || entity.Disposed) return;
            if ((entity.ID & 0x8000000000000000UL) != 0) return;
            if (_entityOwners.ContainsKey(entity.ID)) return;
            if (entity.GetType() == typeof(DXEntity) || GameBridge.IsColonistType(entity.GetType())) return;
            if (!GameBridge.IsPlayerEntity(entity)) return;

            byte bestPid = 1;
            long bestDistSq = long.MaxValue;
            for (int i = 0; i < ccOwners.Count; i++)
            {
                var entry = ccOwners[i];
                long dx = entity.CellX - entry.Cc.CellX;
                long dy = entity.CellY - entry.Cc.CellY;
                long distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq || (distSq == bestDistSq && entry.PlayerId < bestPid))
                {
                    bestDistSq = distSq;
                    bestPid = entry.PlayerId;
                }
            }

            _entityOwners[entity.ID] = bestPid;
        }

        /// <summary>
        /// Deterministic handler invoked on CLife death of a CommandCenter.
        /// Handles AllBasesLost exile + ownership handover vs AnyBaseLost team defeat.
        /// </summary>
        public static void HandleCommandCenterDestroyed(DXEntity destroyedCc)
        {
            if (destroyedCc == null || !CoopSessionManager.IsMultiFactionActive)
                return;

            byte deadOwner = GetOwner(destroyedCc.ID);
            RemoveEntity(destroyedCc.ID);

            var allCcs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            var remaining = new List<DXEntity>(allCcs.Count);
            for (int i = 0; i < allCcs.Count; i++)
            {
                if (allCcs[i].ID != destroyedCc.ID)
                    remaining.Add(allCcs[i]);
            }

            if (remaining.Count == 0)
            {
                GameBridge.RefreshLevelStateCommandCenter(null);
                ModLogger.Info($"[FactionOwnership] Last CommandCenter destroyed (owner P{deadOwner}); team defeat imminent.");
                return;
            }

            // Keep ZXLevelState pointing to a valid surviving CC so Zombie stuck checks,
            // PickableItem drops, and fallback AI targets remain valid.
            GameBridge.RefreshLevelStateCommandCenter(remaining[0]);

            if (CoopSessionManager.DefeatRule == MultiFactionDefeatRule.AnyBaseLost)
            {
                ModLogger.Info($"[FactionOwnership] CommandCenter destroyed (owner P{deadOwner}) under AnyBaseLost rule; full team defeat triggered.");
                return;
            }

            if (deadOwner == 0)
                return;

            bool deadOwnerStillHasCc = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                if (GetOwner(remaining[i].ID) == deadOwner)
                {
                    deadOwnerStillHasCc = true;
                    break;
                }
            }

            if (deadOwnerStillHasCc)
            {
                if (deadOwner == CommandInterceptor.LocalPlayerId)
                    GameBridge.SetLevelSysCurrentCommandCenter(GetPrimaryCommandCenterForPlayer(deadOwner));
                return;
            }

            // Pick surviving ally deterministically (lowest PlayerId with an alive CC).
            byte targetAllyId = 0;
            DXEntity targetAllyCc = null;
            for (int i = 0; i < remaining.Count; i++)
            {
                byte pid = GetOwner(remaining[i].ID);
                if (pid != 0 && pid != deadOwner && (targetAllyId == 0 || pid < targetAllyId))
                {
                    targetAllyId = pid;
                    targetAllyCc = remaining[i];
                }
            }
            if (targetAllyId == 0)
            {
                targetAllyId = 1;
                targetAllyCc = remaining[0];
            }

            int transferred = 0;
            lock (_sync)
            {
                _exiledPlayers.Add(deadOwner);
                var keys = _entityOwners.Keys.ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    ulong eid = keys[i];
                    if (_entityOwners[eid] == deadOwner)
                    {
                        _entityOwners[eid] = targetAllyId;
                        transferred++;
                    }
                }
            }

            FactionContextManager.HandleExileHandover(deadOwner, targetAllyId);
            RefreshAllTrackedEntityColors();

            ModLogger.InfoImmediate($"[FactionOwnership] P{deadOwner} lost last CommandCenter -> EXILED; transferred {transferred} entities to P{targetAllyId}.");

            if (deadOwner == CommandInterceptor.LocalPlayerId)
            {
                GameBridge.UnselectAllEntities();
                GameBridge.SetLevelSysCurrentCommandCenter(targetAllyCc);
                GameBridge.NotifyLocalPlayerBannerAndSound(
                    $"你的指挥中心已沦陷！剩余 {transferred} 个单位/建筑已移交 P{targetAllyId}（进入观战状态）",
                    Color.OrangeRed,
                    5000);
            }
            else if (targetAllyId == CommandInterceptor.LocalPlayerId)
            {
                GameBridge.NotifyLocalPlayerBannerAndSound(
                    $"盟友 P{deadOwner} 指挥中心沦陷！你接管了其剩余 {transferred} 个单位/建筑",
                    Color.Gold,
                    5000);
            }
            else
            {
                GameBridge.NotifyLocalPlayerBannerAndSound(
                    $"盟友 P{deadOwner} 指挥中心沦陷！其剩余部队已移交 P{targetAllyId}",
                    Color.Orange,
                    4500);
            }
        }

        /// <summary>
        /// Deterministically chooses which alive CommandCenter a zombie / swarm should target.
        /// Distant swarm zombies (>35 cells from nearest CC) split evenly via ID % aliveCount.
        /// </summary>
        public static DXEntity PickTargetCommandCenterForZombie(DXEntity zombie, bool splitDistantById)
        {
            var ccs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            if (ccs.Count == 0) return null;
            if (ccs.Count == 1) return ccs[0];

            if (zombie == null) return ccs[0];

            byte assignedPid;
            lock (_sync)
            {
                _zombieSwarmTargets.TryGetValue(zombie.ID, out assignedPid);
            }
            if (assignedPid != 0)
            {
                for (int i = 0; i < ccs.Count; i++)
                {
                    if (GetOwner(ccs[i].ID) == assignedPid)
                        return ccs[i];
                }
            }

            int nearestIdx = 0;
            long bestDistSq = long.MaxValue;
            for (int i = 0; i < ccs.Count; i++)
            {
                long dx = zombie.CellX - ccs[i].CellX;
                long dy = zombie.CellY - ccs[i].CellY;
                long distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearestIdx = i;
                }
            }

            if (splitDistantById && bestDistSq > 35L * 35L)
            {
                int slot = (int)(zombie.ID % (ulong)ccs.Count);
                return ccs[slot];
            }

            return ccs[nearestIdx];
        }

        public static void CenterLocalCameraOnOwnBase(bool selectCc)
        {
            if (!CoopSessionManager.IsMultiFactionActive) return;

            byte localId = CommandInterceptor.LocalPlayerId;
            DXEntity myCc = GetPrimaryCommandCenterForPlayer(localId);
            if (myCc == null) return;

            GameBridge.SetLevelSysCurrentCommandCenter(myCc);
            float curW = DXScene.Current != null ? DXScene.Current.CameraArea.Width : 0f;
            float targetW = curW >= 28f ? curW : 42f;
            GameBridge.SetCameraWorld(myCc.Position, targetW);
            if (selectCc)
                GameBridge.SelectOnlyEntity(myCc);

            ModLogger.Info($"[FactionOwnership] Centered camera for P{localId} on own CommandCenter ID={myCc.ID} at ({myCc.CellX},{myCc.CellY}) width={targetW:F1} (prev={curW:F1}).");
        }

        /// <summary>
        /// Live-level fallback for Host when loading a 1-CC save or 1-CC custom map in Multi-Faction mode:
        /// ensures at least <paramref name="playerCount"/> alive CommandCenters exist before snapshot export.
        /// </summary>
        public static int EnsureExtraCommandCentersInLiveLevel(int playerCount)
        {
            if (playerCount < 2) return 0;
            var existingCcs = GameBridge.GetAllCommandCenters(aliveOnly: true);
            if (existingCcs.Count >= playerCount) return 0;

            var ccTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeCommandCenter);
            var rangerTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeRanger);
            var soldierTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeSoldierRegular);
            if (ccTemplate == null) return 0;

            var terrain = GameBridge.GetTerrainResourceCells();
            int nCells = DXLevel.Current?.NCells ?? DXWorldGrid.NCells;
            if (terrain == null || nCells <= 0) return 0;

            RectangleF playable = DXLevel.Current?.PlayableArea ?? RectangleF.Empty;
            var placedCcs = new List<DXEntity>(existingCcs);
            int createdCcs = 0;

            for (int p = existingCcs.Count + 1; p <= playerCount; p++)
            {
                Point? spot = FindLiveFallbackBaseCell(placedCcs, terrain, nCells, playable, p);
                if (!spot.HasValue)
                {
                    ModLogger.Warn($"[FactionOwnership] Could not find fallback spot for P{p} CommandCenter in live level.");
                    continue;
                }

                Point ccCell = spot.Value;
                ClearNearbyZombiesLive(ccCell, 22);

                DXEntity newCc = ccTemplate.CreateInstance();
                newCc.Cell = ccCell;
                GameBridge.SetEntityTeamAndRelation(newCc, ZXTeamType.Player, ZXRelationshipType.Friend);
                newCc.AddToScene();
                placedCcs.Add(newCc);
                createdCcs++;

                var offsets = new[]
                {
                    new Point(-3, 2), new Point(-2, 3), new Point(3, 2), new Point(2, 3), new Point(0, -3)
                };
                for (int u = 0; u < 4 && rangerTemplate != null; u++)
                {
                    Point? uCell = FindFreeLiveUnitCell(ccCell.X + offsets[u].X, ccCell.Y + offsets[u].Y, terrain, nCells);
                    if (!uCell.HasValue) continue;
                    DXEntity r = rangerTemplate.CreateInstance();
                    r.Cell = uCell.Value;
                    GameBridge.SetEntityTeamAndRelation(r, ZXTeamType.Player, ZXRelationshipType.Friend);
                    r.AddToScene();
                }
                if (soldierTemplate != null)
                {
                    Point? sCell = FindFreeLiveUnitCell(ccCell.X + offsets[4].X, ccCell.Y + offsets[4].Y, terrain, nCells);
                    if (sCell.HasValue)
                    {
                        DXEntity s = soldierTemplate.CreateInstance();
                        s.Cell = sCell.Value;
                        GameBridge.SetEntityTeamAndRelation(s, ZXTeamType.Player, ZXRelationshipType.Friend);
                        s.AddToScene();
                    }
                }

                ModLogger.Info($"[FactionOwnership] Placed live fallback CommandCenter for P{p} at ({ccCell.X},{ccCell.Y}).");
            }

            if (createdCcs > 0)
            {
                RegisterPendingCcOwners(placedCcs);
                GameBridge.InvalidateCommandCenterCache();
                GameBridge.RebuildEnergyGridNow();
            }
            return createdCcs;
        }

        private static Point? FindLiveFallbackBaseCell(
            List<DXEntity> existingCcs,
            ZXTerrainResourceType[,] terrain,
            int nCells,
            RectangleF playable,
            int playerIndex)
        {
            Point anchor = existingCcs.Count > 0 ? existingCcs[0].Cell : new Point(nCells / 2, nCells / 2);
            double baseAngle = (playerIndex - 1) * (2.0 * Math.PI / 3.0) + 0.5;
            int[] primaryRadii = { 46, 54, 38, 62, 32, 70 };
            int[] extendedRadii = { 46, 54, 38, 62, 32, 70, 78, 86, 94, 104, 114, 26, 20 };

            // First try preferred ring distances with >=32-cell CC separation; fall back to wider rings
            // and relaxed separation (>=18 cells) only when dense late-game colonies occupy 32..70.
            for (int pass = 0; pass < 2; pass++)
            {
                int[] radii = pass == 0 ? primaryRadii : extendedRadii;
                long minSepSq = pass == 0 ? 32L * 32L : 18L * 18L;
                int angleDiv = pass == 0 ? 24 : 36;

                for (int rIdx = 0; rIdx < radii.Length; rIdx++)
                {
                    int rad = radii[rIdx];
                    for (int aStep = 0; aStep < angleDiv; aStep++)
                    {
                        double ang = baseAngle + aStep * (2.0 * Math.PI / angleDiv);
                        int cx = anchor.X + (int)Math.Round(Math.Cos(ang) * rad);
                        int cy = anchor.Y + (int)Math.Round(Math.Sin(ang) * rad);
                        if (cx < 20 || cy < 20 || cx >= nCells - 20 || cy >= nCells - 20) continue;

                        if (!playable.IsEmpty)
                        {
                            PointF scenePt = DXWorldGrid.ScenePointFromWorldCell(cx, cy);
                            RectangleF safeRect = playable;
                            safeRect.Inflate(-16f, -16f);
                            if (!safeRect.Contains(scenePt)) continue;
                        }

                        bool tooClose = false;
                        for (int i = 0; i < existingCcs.Count; i++)
                        {
                            long dx = cx - existingCcs[i].CellX;
                            long dy = cy - existingCcs[i].CellY;
                            if (dx * dx + dy * dy < minSepSq) { tooClose = true; break; }
                        }
                        if (tooClose) continue;

                        if (IsLiveAreaClearForCC(cx, cy, terrain, nCells))
                            return new Point(cx, cy);
                    }
                }
            }
            return null;
        }

        private static bool IsLiveAreaClearForCC(int cx, int cy, ZXTerrainResourceType[,] terrain, int nCells)
        {
            // CC footprint is 5x5; require walkable Earth/Grass and no existing entities in 7x7.
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                for (int y = cy - 3; y <= cy + 3; y++)
                {
                    if (x < 2 || y < 2 || x >= nCells - 2 || y >= nCells - 2) return false;
                    var t = terrain[x, y];
                    if (t != ZXTerrainResourceType.Earth && t != ZXTerrainResourceType.Grass)
                        return false;
                }
            }
            var entities = DXWorldGrid.EntitiesAtCellArea(new Rectangle(cx - 3, cy - 3, 7, 7), uint.MaxValue);
            if (entities != null)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e != null && !e.Disposed && !GameBridge.IsZombieType(e.GetType()))
                        return false;
                }
            }
            return true;
        }

        private static Point? FindFreeLiveUnitCell(int tx, int ty, ZXTerrainResourceType[,] terrain, int nCells)
        {
            for (int r = 0; r <= 6; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int cx = tx + dx;
                        int cy = ty + dy;
                        if (cx < 2 || cy < 2 || cx >= nCells - 2 || cy >= nCells - 2) continue;
                        var t = terrain[cx, cy];
                        if (t != ZXTerrainResourceType.Earth && t != ZXTerrainResourceType.Grass) continue;
                        var ents = DXWorldGrid.EntitiesAtCellArea(new Rectangle(cx, cy, 1, 1), uint.MaxValue);
                        if (ents == null || ents.Count == 0)
                            return new Point(cx, cy);
                    }
                }
            }
            return null;
        }

        private static void ClearNearbyZombiesLive(Point center, int radius)
        {
            var area = new Rectangle(center.X - radius, center.Y - radius, radius * 2 + 1, radius * 2 + 1);
            var list = DXWorldGrid.EntitiesAtCellArea(area, uint.MaxValue);
            if (list == null) return;
            long rSq = (long)radius * radius;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e != null && !e.Disposed && GameBridge.IsZombieType(e.GetType()))
                {
                    long dx = e.CellX - center.X;
                    long dy = e.CellY - center.Y;
                    if (dx * dx + dy * dy <= rSq)
                    {
                        e.RemoveFromScene();
                        e.Dispose();
                    }
                }
            }
        }

        // ====================================================================
        // Snapshot Serialization & Determinism Hash
        // ====================================================================

        public static byte[] SerializeToBytes()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                lock (_sync)
                {
                    w.Write((byte)3); // format version 3 includes M4 spawn mode, tendency, seed, and active sub-swarm targets
                    w.Write((byte)CoopSessionManager.GameMode);
                    w.Write((byte)CoopSessionManager.DefeatRule);
                    w.Write(ActivePlayerCount);
                    w.Write(IsInitialized);

                    var exiled = _exiledPlayers.OrderBy(p => p).ToArray();
                    w.Write((byte)exiled.Length);
                    for (int i = 0; i < exiled.Length; i++)
                        w.Write(exiled[i]);

                    var entries = _entityOwners.OrderBy(kv => kv.Key).ToArray();
                    w.Write(entries.Length);
                    for (int i = 0; i < entries.Length; i++)
                    {
                        w.Write(entries[i].Key);
                        w.Write(entries[i].Value);
                    }

                    FactionContextManager.WriteState(w);
                    WonderCrowdfundManager.WriteState(w);

                    w.Write((byte)ActiveSpawnMode);
                    w.Write((byte)ActiveMutationTendency);
                    w.Write(ActiveMapSeed);

                    var evTargets = _subSwarmEventTargets.OrderBy(kv => kv.Key).ToArray();
                    w.Write(evTargets.Length);
                    for (int i = 0; i < evTargets.Length; i++)
                    {
                        w.Write(evTargets[i].Key);
                        w.Write(evTargets[i].Value);
                    }

                    var zTargets = _zombieSwarmTargets.OrderBy(kv => kv.Key).ToArray();
                    w.Write(zTargets.Length);
                    for (int i = 0; i < zTargets.Length; i++)
                    {
                        w.Write(zTargets[i].Key);
                        w.Write(zTargets[i].Value);
                    }
                }
                return ms.ToArray();
            }
        }

        public static bool DeserializeFromBytes(byte[] data)
        {
            if (data == null || data.Length < 6) return false;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    byte ver = r.ReadByte();
                    if (ver < 1 || ver > 3) return false;

                    var mode = (CoopGameMode)r.ReadByte();
                    var defeatRule = (MultiFactionDefeatRule)r.ReadByte();
                    byte playerCount = r.ReadByte();
                    bool initialized = r.ReadBoolean();

                    byte exiledCount = r.ReadByte();
                    var exiled = new List<byte>(exiledCount);
                    for (int i = 0; i < exiledCount; i++)
                        exiled.Add(r.ReadByte());

                    int ownerCount = r.ReadInt32();
                    var map = new Dictionary<ulong, byte>(ownerCount);
                    for (int i = 0; i < ownerCount; i++)
                    {
                        ulong eid = r.ReadUInt64();
                        byte pid = r.ReadByte();
                        if (eid != 0UL && pid != 0)
                            map[eid] = pid;
                    }

                    CoopSessionManager.ApplySyncedMultiFactionSettings(mode, defeatRule);
                    lock (_sync)
                    {
                        _exiledPlayers.Clear();
                        for (int i = 0; i < exiled.Count; i++)
                            _exiledPlayers.Add(exiled[i]);
                        _entityOwners.Clear();
                        foreach (var kv in map)
                            _entityOwners[kv.Key] = kv.Value;
                        ActivePlayerCount = playerCount;
                        IsInitialized = initialized;
                        _subSwarmEventTargets.Clear();
                        _zombieSwarmTargets.Clear();
                    }

                    if (ver >= 2 && r.BaseStream.Position < r.BaseStream.Length)
                    {
                        FactionContextManager.ReadState(r);
                        WonderCrowdfundManager.ReadState(r);
                    }
                    else if (mode == CoopGameMode.MultiFaction && initialized)
                    {
                        FactionContextManager.InitializeFromCurrentLevelState(playerCount);
                        WonderCrowdfundManager.Initialize(playerCount);
                    }

                    if (ver >= 3 && r.BaseStream.Position + 6 <= r.BaseStream.Length)
                    {
                        var spawnMode = (MultiFactionSpawnMode)r.ReadByte();
                        var tendency = (MutationTendency)r.ReadByte();
                        int mapSeed = r.ReadInt32();
                        var evMap = new Dictionary<ulong, byte>();
                        if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                        {
                            int evCount = r.ReadInt32();
                            for (int i = 0; i < evCount && r.BaseStream.Position + 9 <= r.BaseStream.Length; i++)
                            {
                                ulong id = r.ReadUInt64();
                                byte pid = r.ReadByte();
                                if (id != 0UL && pid != 0) evMap[id] = pid;
                            }
                        }
                        var zMap = new Dictionary<ulong, byte>();
                        if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                        {
                            int zCount = r.ReadInt32();
                            for (int i = 0; i < zCount && r.BaseStream.Position + 9 <= r.BaseStream.Length; i++)
                            {
                                ulong id = r.ReadUInt64();
                                byte pid = r.ReadByte();
                                if (id != 0UL && pid != 0) zMap[id] = pid;
                            }
                        }
                        lock (_sync)
                        {
                            ActiveSpawnMode = spawnMode;
                            ActiveMutationTendency = tendency;
                            ActiveMapSeed = mapSeed;
                            foreach (var kv in evMap) _subSwarmEventTargets[kv.Key] = kv.Value;
                            foreach (var kv in zMap) _zombieSwarmTargets[kv.Key] = kv.Value;
                        }
                        CoopSessionManager.ApplySyncedSpawnMode(spawnMode);
                    }

                    GameBridge.SetZeroFeeMayorActive(CoopSessionManager.IsMultiFactionActive && !FactionContextManager.ZeroTransferFeeActive);
                    RefreshAllTrackedEntityColors();
                }

                InstallControlFilterIfNeeded();
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[FactionOwnership] DeserializeFromBytes failed: {ex.Message}");
                return false;
            }
        }

        public static void SaveToLevelScriptVariables()
        {
            if (!CoopSessionManager.IsMultiFactionActive && (!IsInitialized || ActivePlayerCount < 2)) return;
            var vars = GameBridge.GetLevelScriptVariables();
            if (vars == null) return;
            byte[] bytes = SerializeToBytes();
            vars[ScriptVarKey] = Convert.ToBase64String(bytes);
        }

        public static bool TryLoadFromLevelScriptVariables()
        {
            var vars = GameBridge.GetLevelScriptVariables();
            if (vars == null || !vars.Contains(ScriptVarKey))
                return false;
            if (!(vars[ScriptVarKey] is string b64) || string.IsNullOrEmpty(b64))
                return false;

            try
            {
                byte[] bytes = Convert.FromBase64String(b64);
                bool ok = DeserializeFromBytes(bytes);
                if (ok)
                {
                    ModLogger.Info($"[FactionOwnership] Restored {TrackedEntityCount} owner entries from ScriptVariables (mode={CoopSessionManager.GameMode}, defeat={CoopSessionManager.DefeatRule}).");
                }
                return ok;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[FactionOwnership] TryLoadFromLevelScriptVariables failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Order-independent 64-bit digest of EntityOwnerMap, exiled players, and defeat rule
        /// so any ownership desync is immediately flagged by StateHasher.
        /// </summary>
        public static ulong ComputeOwnershipHash()
        {
            if (!CoopSessionManager.IsMultiFactionActive) return 0UL;

            ulong mix = 0x9E3779B97F4A7C15UL ^ (ulong)CoopSessionManager.DefeatRule;
            lock (_sync)
            {
                mix ^= ((ulong)ActiveSpawnMode << 8) ^ ((ulong)ActiveMutationTendency << 16);
                foreach (byte pid in _exiledPlayers)
                    mix ^= (ulong)pid * 0xBF58476D1CE4E5B9UL;

                ulong sum = 0;
                foreach (var kv in _entityOwners)
                {
                    ulong entry = (kv.Key * 0x9E3779B97F4A7C15UL) ^ ((ulong)kv.Value * 0x94D049BB133111EBUL);
                    sum += entry;
                }
                foreach (var kv in _subSwarmEventTargets)
                {
                    sum += (kv.Key * 0xBF58476D1CE4E5B9UL) ^ ((ulong)kv.Value * 0x9E3779B97F4A7C15UL);
                }
                mix ^= sum;
                mix ^= (ulong)_entityOwners.Count * 0xD6E8FEB86659FD93UL;
            }
            return mix;
        }
    }
}
