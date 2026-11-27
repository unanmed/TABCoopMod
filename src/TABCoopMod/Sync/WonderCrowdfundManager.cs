using System;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;

namespace TABCoopMod.Sync
{
    public enum FunctionalWonderType : byte
    {
        Telescope = 0,
        Academy = 1,
        Spire = 2
    }

    public enum WonderPhase : byte
    {
        ResearchFunding = 0,
        BuildFunding = 1,
        ReadyVoucher = 2,
        Building = 3,
        Completed = 4,
        Researching = 5
    }

    /// <summary>
    /// Authoritative team crowdfunding state for the 3 Functional Wonders
    /// (The Great Telescope, The Soldiers' Academy, The Electric Spire) in Multi-Faction mode.
    /// Costs scale deterministically by integer factor (2 + playerCount - 1) / 2:
    /// 2P -> 1.5x, 3P -> 2.0x. Also scales The Spire energy supply identically.
    /// All state transitions occur exclusively from lockstep NetCommands or deterministic sim events.
    /// </summary>
    public static class WonderCrowdfundManager
    {
        public struct CostBundle
        {
            public int Gold;
            public int Wood;
            public int Stone;
            public int Iron;
            public int Oil;

            public bool IsZero => Gold <= 0 && Wood <= 0 && Stone <= 0 && Iron <= 0 && Oil <= 0;

            public int Get(ResourceTransferKind k)
            {
                switch (k)
                {
                    case ResourceTransferKind.Gold: return Gold;
                    case ResourceTransferKind.Wood: return Wood;
                    case ResourceTransferKind.Stone: return Stone;
                    case ResourceTransferKind.Iron: return Iron;
                    case ResourceTransferKind.Oil: return Oil;
                    default: return 0;
                }
            }

            public void Set(ResourceTransferKind k, int val)
            {
                switch (k)
                {
                    case ResourceTransferKind.Gold: Gold = val; break;
                    case ResourceTransferKind.Wood: Wood = val; break;
                    case ResourceTransferKind.Stone: Stone = val; break;
                    case ResourceTransferKind.Iron: Iron = val; break;
                    case ResourceTransferKind.Oil: Oil = val; break;
                }
            }

            public void Add(ResourceTransferKind k, int delta) => Set(k, Get(k) + delta);

            public void Clear()
            {
                Gold = Wood = Stone = Iron = Oil = 0;
            }
        }

        /// <summary>
        /// Fixed deterministic simulation-tick duration for Wonder tech research once ResearchFunding reaches 100%.
        /// 1350 ticks = 45 seconds at 30 Hz IA simulation speed.
        /// </summary>
        public const int DefaultResearchDurationTicks = 1350;

        public static int GetResearchDurationTicks()
        {
            if (Testing.TestHarnessConfig.ControlPort > 0)
            {
                string raw = Environment.GetEnvironmentVariable("TABCOOP_WONDER_RESEARCH_TICKS");
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int custom) && custom >= 10 && custom <= 10000)
                    return custom;
            }
            return DefaultResearchDurationTicks;
        }

        public sealed class WonderState
        {
            public FunctionalWonderType Kind;
            public WonderPhase Phase;
            public bool ResearchUnlocked;
            public int ResearchTicksElapsed;
            public int ResearchTicksTotal;
            public CostBundle ResearchTarget;
            public CostBundle ResearchContributed;
            public CostBundle BuildTarget;
            public CostBundle BuildContributed;
            public CostBundle[] PlayerResearchContrib = new CostBundle[4]; // index 1..3
            public CostBundle[] PlayerBuildContrib = new CostBundle[4];    // index 1..3
            public byte BuilderPlayerId;
            public ulong BuildingEntityId;

            public bool IsCurrentPhaseFunding =>
                Phase == WonderPhase.ResearchFunding || Phase == WonderPhase.BuildFunding;

            public CostBundle CurrentTarget =>
                (Phase == WonderPhase.ResearchFunding || Phase == WonderPhase.Researching) ? ResearchTarget : BuildTarget;

            public CostBundle CurrentContributed =>
                (Phase == WonderPhase.ResearchFunding || Phase == WonderPhase.Researching) ? ResearchContributed : BuildContributed;

            public CostBundle[] CurrentPlayerContrib =>
                (Phase == WonderPhase.ResearchFunding || Phase == WonderPhase.Researching) ? PlayerResearchContrib : PlayerBuildContrib;

            public float ResearchProgress01
            {
                get
                {
                    if (ResearchUnlocked) return 1f;
                    if (Phase != WonderPhase.Researching || ResearchTicksTotal <= 0) return 0f;
                    float p = (float)ResearchTicksElapsed / ResearchTicksTotal;
                    return p < 0f ? 0f : (p > 1f ? 1f : p);
                }
            }

            public int ResearchRemainingSeconds
            {
                get
                {
                    if (Phase != WonderPhase.Researching || ResearchTicksTotal <= ResearchTicksElapsed) return 0;
                    return (int)Math.Ceiling((ResearchTicksTotal - ResearchTicksElapsed) / 30.0);
                }
            }

            public bool IsFundingComplete
            {
                get
                {
                    var t = CurrentTarget;
                    var c = CurrentContributed;
                    return c.Gold >= t.Gold
                        && c.Wood >= t.Wood
                        && c.Stone >= t.Stone
                        && c.Iron >= t.Iron
                        && c.Oil >= t.Oil;
                }
            }
        }

        private static readonly WonderState[] _wonders = new WonderState[3];
        public static bool IsInitialized { get; private set; }
        public static int PlayerCount { get; private set; } = 1;

        public static string GetDisplayName(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return "The Silent Beholder";
                case FunctionalWonderType.Academy: return "The Academy of Immortals";
                case FunctionalWonderType.Spire: return "The Lightning Spire";
                default: return kind.ToString();
            }
        }

        public static string GetTechTypeName(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return "ZX.Commands.TechTheGreatTelescope";
                case FunctionalWonderType.Academy: return "ZX.Commands.TechTheSoldierAcademy";
                case FunctionalWonderType.Spire: return "ZX.Commands.TechTheElectricSpire";
                default: return null;
            }
        }

        public static string GetEntityTypeName(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return "ZX.Entities.TheGreatTelescope";
                case FunctionalWonderType.Academy: return "ZX.Entities.TheAcademy";
                case FunctionalWonderType.Spire: return "ZX.Entities.TheSpire";
                default: return null;
            }
        }

        public static Type GetRequiredWorkshopType(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return GameBridge.TypeWoodWorkshop;
                case FunctionalWonderType.Academy: return GameBridge.TypeStoneWorkshop;
                case FunctionalWonderType.Spire: return GameBridge.TypeFoundry;
                default: return null;
            }
        }

        public static string GetRequiredWorkshopDisplayNameZh(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return "木材工坊";
                case FunctionalWonderType.Academy: return "石材工坊";
                case FunctionalWonderType.Spire: return "铸造厂";
                default: return "工坊";
            }
        }

        public static string GetRequiredWorkshopDisplayNameEn(FunctionalWonderType kind)
        {
            switch (kind)
            {
                case FunctionalWonderType.Telescope: return "Wood Workshop";
                case FunctionalWonderType.Academy: return "Stone Workshop";
                case FunctionalWonderType.Spire: return "Foundry";
                default: return "Workshop";
            }
        }

        /// <summary>
        /// Returns true iff at least one alive, non-exiled player on the team currently owns a completed
        /// (not under construction) workshop of the vanilla tier required to research <paramref name="kind"/>:
        /// Telescope -> WoodWorkshop, Academy -> StoneWorkshop, Spire -> Foundry.
        /// </summary>
        public static bool HasRequiredWorkshopBuilt(FunctionalWonderType kind)
        {
            Type reqType = GetRequiredWorkshopType(kind);
            var game = DXGame.Current;
            if (reqType == null || game == null) return false;

            lock (DXGame.SyncLogic)
            {
                foreach (var ent in game.EntitiesOfType(reqType))
                {
                    if (ent == null || ent.Disposed) continue;
                    if (GameBridge.IsBuildingBeingBuilt(ent)) continue;
                    if (!GameBridge.IsPlayerEntity(ent)) continue;

                    byte owner = FactionOwnershipTracker.GetOwner(ent.ID);
                    if (owner >= 1 && owner <= 3 && FactionOwnershipTracker.IsExiled(owner))
                        continue;
                    return true;
                }
            }
            return false;
        }

        public static bool TryClassifyEntityType(Type entityType, out FunctionalWonderType kind)
        {
            kind = FunctionalWonderType.Telescope;
            if (entityType == null) return false;
            if (entityType == GameBridge.TypeTheGreatTelescope || entityType.FullName == "ZX.Entities.TheGreatTelescope") { kind = FunctionalWonderType.Telescope; return true; }
            if (entityType == GameBridge.TypeTheAcademy || entityType.FullName == "ZX.Entities.TheAcademy") { kind = FunctionalWonderType.Academy; return true; }
            if (entityType == GameBridge.TypeTheSpire || entityType.FullName == "ZX.Entities.TheSpire") { kind = FunctionalWonderType.Spire; return true; }
            return false;
        }

        public static bool TryClassifyTechCommand(object cmd, out FunctionalWonderType kind)
        {
            kind = FunctionalWonderType.Telescope;
            if (cmd == null) return false;
            Type t = cmd.GetType();
            if (t == GameBridge.TypeTechGreatTelescope || t.FullName == "ZX.Commands.TechTheGreatTelescope") { kind = FunctionalWonderType.Telescope; return true; }
            if (t == GameBridge.TypeTechSoldierAcademy || t.FullName == "ZX.Commands.TechTheSoldierAcademy") { kind = FunctionalWonderType.Academy; return true; }
            if (t == GameBridge.TypeTechElectricSpire || t.FullName == "ZX.Commands.TechTheElectricSpire") { kind = FunctionalWonderType.Spire; return true; }
            return false;
        }

        public static int ScaleCost(int baseVal, int playerCount)
        {
            if (baseVal <= 0) return 0;
            int n = Math.Max(1, Math.Min(3, playerCount));
            return (baseVal * (2 + n - 1)) / 2;
        }

        public static int GetScaledSpireEnergy(int baseEnergy = 800)
        {
            return ScaleCost(baseEnergy, PlayerCount);
        }

        public static WonderState GetState(FunctionalWonderType kind) => _wonders[(int)kind];

        public static void Reset()
        {
            IsInitialized = false;
            PlayerCount = 1;
            Array.Clear(_wonders, 0, _wonders.Length);
        }

        public static void Initialize(int playerCount)
        {
            PlayerCount = Math.Max(1, Math.Min(3, playerCount));
            for (int i = 0; i < 3; i++)
            {
                var kind = (FunctionalWonderType)i;
                var st = new WonderState
                {
                    Kind = kind,
                    Phase = WonderPhase.ResearchFunding,
                    ResearchUnlocked = false
                };
                PopulateScaledTargets(kind, st, PlayerCount);
                _wonders[i] = st;
            }
            IsInitialized = true;

            // If this save/level already had any functional wonder researched or built natively (e.g. loaded save), sync initial state.
            SyncExistingWondersFromScene();
            ModLogger.Info($"[WonderCrowdfund] Initialized for {PlayerCount} players. "
                + $"Telescope=({stStr(FunctionalWonderType.Telescope)}), "
                + $"Academy=({stStr(FunctionalWonderType.Academy)}), "
                + $"Spire=({stStr(FunctionalWonderType.Spire)}) (SpireEnergy={GetScaledSpireEnergy()})");
        }

        private static string stStr(FunctionalWonderType k)
        {
            var s = _wonders[(int)k];
            return $"ResG={s.ResearchTarget.Gold}, BuildG={s.BuildTarget.Gold}/W={s.BuildTarget.Wood}/S={s.BuildTarget.Stone}/I={s.BuildTarget.Iron}/O={s.BuildTarget.Oil}, Phase={s.Phase}";
        }

        private static void SyncExistingWondersFromScene()
        {
            var game = DXGame.Current;
            if (game == null) return;

            for (int i = 0; i < 3; i++)
            {
                var kind = (FunctionalWonderType)i;
                var st = _wonders[i];

                // Check if tech command is already unlocked on any player
                var techType = GameBridge.FindCommandTypeByName(GetTechTypeName(kind));
                var techCmd = techType != null ? GameBridge.GetCommandInstanceByType(techType) : null;
                if (techCmd != null)
                {
                    int techId = GameBridge.GetCommandId(techCmd);
                    for (byte pid = 1; pid <= PlayerCount; pid++)
                    {
                        if (FactionContextManager.HasUnlockedOnlyOneCommandForPlayer(pid, techId))
                        {
                            st.ResearchUnlocked = true;
                            if (st.Phase == WonderPhase.ResearchFunding)
                                st.Phase = WonderPhase.BuildFunding;
                        }
                    }
                    if (st.ResearchUnlocked)
                        FactionContextManager.GrantOnlyOneCommandToAllAlivePlayers(techId);
                }

                // Check existing wonder instances in scene
                string entName = GetEntityTypeName(kind);
                var entities = GameBridge.GetEntitiesList(game);
                if (entities != null)
                {
                    if (kind == FunctionalWonderType.Telescope)
                    {
                        for (int idx = 0; idx < entities.Count; idx++)
                        {
                            var ent = entities[idx];
                            if (ent == null || ent.Disposed) continue;
                            if (ent.GetType() == GameBridge.TypeCommandCenter && GameBridge.GetCommandCenterTelescopeEnabled(ent))
                            {
                                st.ResearchUnlocked = true;
                                st.BuilderPlayerId = FactionOwnershipTracker.GetOwner(ent.ID);
                                st.BuildingEntityId = ent.ID;
                                st.Phase = WonderPhase.Completed;
                                break;
                            }
                        }
                    }
                    else
                    {
                        for (int idx = 0; idx < entities.Count; idx++)
                        {
                            var ent = entities[idx];
                            if (ent == null || ent.Disposed) continue;
                            if (ent.GetType().FullName == entName)
                            {
                                st.ResearchUnlocked = true;
                                st.BuilderPlayerId = FactionOwnershipTracker.GetOwner(ent.ID);
                                st.BuildingEntityId = ent.ID;
                                st.Phase = GameBridge.IsBuildingBeingBuilt(ent) ? WonderPhase.Building : WonderPhase.Completed;
                                break;
                            }
                        }
                    }
                }
            }
        }

        private static void PopulateScaledTargets(FunctionalWonderType kind, WonderState st, int playerCount)
        {
            var game = DXGame.Current;
            var techType = GameBridge.FindCommandTypeByName(GetTechTypeName(kind));
            var techCmd = techType != null ? GameBridge.GetCommandInstanceByType(techType) : null;

            object anyPlayerEntity = null;
            var entities = game != null ? GameBridge.GetEntitiesList(game) : null;
            if (entities != null)
            {
                for (int idx = 0; idx < entities.Count; idx++)
                {
                    var ent = entities[idx];
                    if (ent != null && !ent.Disposed && ent.GetType() == GameBridge.TypeCommandCenter)
                    {
                        anyPlayerEntity = ent;
                        break;
                    }
                }
            }

            if (techCmd != null && GameBridge.TryGetCommandResourcesCost(techCmd, anyPlayerEntity,
                out int tg, out int tw, out int ts, out int ti, out int to, out _, out _, out _))
            {
                st.ResearchTarget = new CostBundle
                {
                    Gold = ScaleCost(tg, playerCount),
                    Wood = ScaleCost(tw, playerCount),
                    Stone = ScaleCost(ts, playerCount),
                    Iron = ScaleCost(ti, playerCount),
                    Oil = ScaleCost(to, playerCount)
                };
            }
            else
            {
                // Fallback default research costs from TAB Tech commands
                int fallbackGold = kind == FunctionalWonderType.Telescope ? 1200 : 1000;
                st.ResearchTarget = new CostBundle { Gold = ScaleCost(fallbackGold, playerCount) };
            }

            var entType = GameBridge.FindEntityTypeByName(GetEntityTypeName(kind));
            if (entType != null && GameBridge.TryGetEntityResourcesCost(entType,
                out int bg, out int bw, out int bs, out int bi, out int bo, out _, out _, out _))
            {
                st.BuildTarget = new CostBundle
                {
                    Gold = ScaleCost(bg, playerCount),
                    Wood = ScaleCost(bw, playerCount),
                    Stone = ScaleCost(bs, playerCount),
                    Iron = ScaleCost(bi, playerCount),
                    Oil = ScaleCost(bo, playerCount)
                };
            }
            else
            {
                st.BuildTarget = new CostBundle { Gold = ScaleCost(3000, playerCount) };
            }
        }

        /// <summary>
        /// Contributes resources from player <paramref name="pid"/> to the active phase of <paramref name="kind"/>.
        /// Clamps contribution cleanly to min(requested, remainingNeeded, playerAvailable).
        /// Returns actual amount contributed.
        /// </summary>
        public static int TryContribute(byte pid, FunctionalWonderType kind, ResourceTransferKind res, int amount)
        {
            if (!IsInitialized || pid < 1 || pid > 3 || amount <= 0) return 0;
            if (FactionOwnershipTracker.IsExiled(pid)) return 0;

            var st = _wonders[(int)kind];
            if (st == null || !st.IsCurrentPhaseFunding) return 0;

            if (st.Phase == WonderPhase.ResearchFunding && !HasRequiredWorkshopBuilt(kind))
            {
                ModLogger.Info($"[WonderCrowdfund] P{pid} contribution to {kind} ResearchFunding blocked: team lacks completed {GetRequiredWorkshopDisplayNameEn(kind)}.");
                return 0;
            }

            int target = st.CurrentTarget.Get(res);
            int cur = st.CurrentContributed.Get(res);
            int need = target - cur;
            if (need <= 0) return 0;

            var ledger = FactionContextManager.GetLedger(pid);
            if (ledger == null) return 0;

            int avail = FactionContextManager.GetStock(pid, res);
            int actual = Math.Min(amount, Math.Min(need, avail));
            if (actual <= 0) return 0;

            // Deduct from contributor's ledger (and live ZXLevelState if pid is active/local)
            FactionContextManager.AdjustStockAndSync(pid, res, -actual);

            if (st.Phase == WonderPhase.ResearchFunding)
            {
                st.ResearchContributed.Add(res, actual);
                st.PlayerResearchContrib[pid].Add(res, actual);
            }
            else
            {
                st.BuildContributed.Add(res, actual);
                st.PlayerBuildContrib[pid].Add(res, actual);
            }

            ModLogger.Info($"[WonderCrowdfund] P{pid} contributed {actual} {res} to {kind} ({st.Phase}).");

            // Check phase completion
            if (st.IsFundingComplete)
            {
                if (st.Phase == WonderPhase.ResearchFunding)
                {
                    int duration = GetResearchDurationTicks();
                    st.Phase = WonderPhase.Researching;
                    st.ResearchTicksElapsed = 0;
                    st.ResearchTicksTotal = duration;
                    ModLogger.Info($"[WonderCrowdfund] {kind} research crowdfunding COMPLETE! Phase -> Researching ({duration} ticks / {Math.Max(1, duration / 30)}s).");
                }
                else if (st.Phase == WonderPhase.BuildFunding)
                {
                    st.Phase = WonderPhase.ReadyVoucher;
                    ModLogger.Info($"[WonderCrowdfund] {kind} build crowdfunding COMPLETE! Phase -> ReadyVoucher.");
                }
            }

            return actual;
        }

        /// <summary>
        /// Advances deterministic simulation-tick research countdowns for all Wonders currently in
        /// <see cref="WonderPhase.Researching"/>. Must be invoked strictly once per completed sim tick
        /// prior to StateHasher epoch hashing so all peers transition identically.
        /// </summary>
        public static void TickResearchProgress()
        {
            if (!IsInitialized || !CoopSessionManager.IsMultiFactionActive) return;

            for (int i = 0; i < 3; i++)
            {
                var st = _wonders[i];
                if (st == null || st.Phase != WonderPhase.Researching) continue;
                if (!HasRequiredWorkshopBuilt(st.Kind)) continue;

                if (st.ResearchTicksTotal <= 0)
                    st.ResearchTicksTotal = DefaultResearchDurationTicks;

                st.ResearchTicksElapsed++;
                if (st.ResearchTicksElapsed >= st.ResearchTicksTotal)
                {
                    CompleteResearchPhase(st);
                    string wName = GetDisplayName(st.Kind);
                    CommandExecutor.ShowBannerOutsideDet(
                        $"{wName}: Shared Research Complete! Build crowdfunding open.",
                        System.Drawing.Color.Gold, 3500);
                }
            }
        }

        private static void CompleteResearchPhase(WonderState st)
        {
            st.ResearchUnlocked = true;
            st.ResearchTicksElapsed = st.ResearchTicksTotal;
            st.Phase = WonderPhase.BuildFunding;

            var techType = GameBridge.FindCommandTypeByName(GetTechTypeName(st.Kind));
            var techCmd = techType != null ? GameBridge.GetCommandInstanceByType(techType) : null;
            if (techCmd != null)
            {
                int techId = GameBridge.GetCommandId(techCmd);
                FactionContextManager.GrantOnlyOneCommandToAllAlivePlayers(techId);
            }
            ModLogger.Info($"[WonderCrowdfund] {st.Kind} RESEARCH COMPLETE! Unlocked tech for all players. Phase -> BuildFunding.");
        }

        /// <summary>
        /// Withdraws unconsumed contributions in the active funding phase (blocked once ReadyVoucher/Building/Completed).
        /// </summary>
        public static int TryWithdraw(byte pid, FunctionalWonderType kind, ResourceTransferKind res, int amount)
        {
            if (!IsInitialized || pid < 1 || pid > 3 || amount <= 0) return 0;
            var st = _wonders[(int)kind];
            if (st == null || !st.IsCurrentPhaseFunding) return 0;

            int contrib = st.CurrentPlayerContrib[pid].Get(res);
            int actual = Math.Min(amount, contrib);
            if (actual <= 0) return 0;

            if (st.Phase == WonderPhase.ResearchFunding)
            {
                st.ResearchContributed.Add(res, -actual);
                st.PlayerResearchContrib[pid].Add(res, -actual);
            }
            else
            {
                st.BuildContributed.Add(res, -actual);
                st.PlayerBuildContrib[pid].Add(res, -actual);
            }

            var ledger = FactionContextManager.GetLedger(pid);
            if (ledger != null)
                FactionContextManager.AdjustStockAndSync(pid, res, actual);

            ModLogger.Info($"[WonderCrowdfund] P{pid} withdrew {actual} {res} from {kind} ({st.Phase}).");
            return actual;
        }

        public static bool CanPlaceBuildVoucher(FunctionalWonderType kind)
        {
            if (!IsInitialized) return false;
            var st = _wonders[(int)kind];
            return st != null && st.Phase == WonderPhase.ReadyVoucher;
        }

        /// <summary>
        /// Called when a Build command for a Functional Wonder executes cleanly on <paramref name="placerPid"/>.
        /// Consumes ReadyVoucher -> Building.
        /// </summary>
        public static bool ConsumeBuildVoucher(FunctionalWonderType kind, byte placerPid, ulong entityId)
        {
            if (!IsInitialized) return false;
            var st = _wonders[(int)kind];
            if (st == null || st.Phase != WonderPhase.ReadyVoucher) return false;

            st.Phase = WonderPhase.Building;
            st.BuilderPlayerId = placerPid;
            st.BuildingEntityId = entityId;
            ModLogger.Info($"[WonderCrowdfund] {kind} Build voucher CONSUMED by P{placerPid} (ent={entityId}). Phase -> Building.");
            return true;
        }

        public static void SetBuildingEntityId(FunctionalWonderType kind, ulong entityId)
        {
            if (!IsInitialized) return;
            var st = _wonders[(int)kind];
            if (st != null && st.Phase == WonderPhase.Building && entityId != 0)
                st.BuildingEntityId = entityId;
        }

        /// <summary>
        /// Called when construction finishes (OnBuildingConstructionComplete).
        /// Transitions Building -> Completed and applies global effects (FullMap / Veterans / SharedEnergyGrid).
        /// </summary>
        public static void HandleConstructionCompleted(FunctionalWonderType kind, DXEntity entity)
        {
            if (!IsInitialized) return;
            var st = _wonders[(int)kind];
            if (st == null) return;

            st.Phase = WonderPhase.Completed;
            if (entity != null) st.BuildingEntityId = entity.ID;

            if (kind == FunctionalWonderType.Telescope)
            {
                // Ensure all players get full map visibility
                FactionContextManager.SetShowFullMapForAllPlayers(true);
            }
            else if (kind == FunctionalWonderType.Spire)
            {
                if (entity != null) GameBridge.CallBuildingRecalculateNear(entity);
                FactionContextManager.RecalculateAllFactions();
            }

            ModLogger.Info($"[WonderCrowdfund] {kind} COMPLETED globally! (ent={entity?.ID ?? 0})");
        }

        /// <summary>
        /// Called when an in-progress Functional Wonder construction is canceled (UndoBuilding / Destroy while IsBeingBuilt).
        /// Proportionally refunds escrow back to contributors (default 50% or returnFactor*100%) and returns to BuildFunding.
        /// </summary>
        public static void HandleConstructionCanceled(FunctionalWonderType kind, int returnPctInt = 50)
        {
            if (!IsInitialized) return;
            var st = _wonders[(int)kind];
            if (st == null || st.Phase != WonderPhase.Building) return;

            int pct = Math.Max(0, Math.Min(100, returnPctInt));
            for (byte pid = 1; pid <= 3; pid++)
            {
                var contrib = st.PlayerBuildContrib[pid];
                var ledger = FactionContextManager.GetLedger(pid);
                if (ledger != null)
                {
                    foreach (ResourceTransferKind res in Enum.GetValues(typeof(ResourceTransferKind)))
                    {
                        int c = contrib.Get(res);
                        if (c <= 0) continue;
                        int refund = (c * pct) / 100;
                        if (refund > 0)
                            FactionContextManager.AdjustStockAndSync(pid, res, refund);
                    }
                }
                st.PlayerBuildContrib[pid].Clear();
            }

            st.BuildContributed.Clear();
            st.BuilderPlayerId = 0;
            st.BuildingEntityId = 0;
            st.Phase = WonderPhase.BuildFunding;
            ModLogger.Info($"[WonderCrowdfund] {kind} construction CANCELED: refunded {pct}% proportionally, Phase -> BuildFunding.");
        }

        /// <summary>
        /// Called when a completed Functional Wonder is destroyed (e.g. TheSpire / TheAcademy).
        /// Re-opens Phase -> BuildFunding (ResearchUnlocked remains true!) so the team can rebuild it.
        /// </summary>
        public static void HandleWonderDestroyed(FunctionalWonderType kind, ulong entityId)
        {
            if (!IsInitialized) return;
            var st = _wonders[(int)kind];
            if (st == null) return;
            if (st.BuildingEntityId != 0 && entityId != 0 && st.BuildingEntityId != entityId) return;

            bool wasBuilding = st.Phase == WonderPhase.Building;
            if (wasBuilding)
            {
                HandleConstructionCanceled(kind, 50);
                return;
            }

            for (int p = 1; p <= 3; p++) st.PlayerBuildContrib[p].Clear();
            st.BuildContributed.Clear();
            st.BuilderPlayerId = 0;
            st.BuildingEntityId = 0;
            st.Phase = st.ResearchUnlocked ? WonderPhase.BuildFunding : WonderPhase.ResearchFunding;

            if (kind == FunctionalWonderType.Spire)
                FactionContextManager.RecalculateAllFactions();

            ModLogger.Info($"[WonderCrowdfund] {kind} DESTROYED (ent={entityId}). Phase -> {st.Phase}.");
        }

        public static bool IsWonderBlockedGlobally(FunctionalWonderType kind)
        {
            if (!IsInitialized) return false;
            var st = _wonders[(int)kind];
            if (st == null) return false;
            return st.Phase == WonderPhase.Building || st.Phase == WonderPhase.Completed;
        }

        public static void WriteState(BinaryWriter w)
        {
            w.Write(IsInitialized);
            w.Write((byte)PlayerCount);
            if (!IsInitialized) return;

            for (int i = 0; i < 3; i++)
            {
                var st = _wonders[i];
                w.Write((byte)st.Phase);
                w.Write(st.ResearchUnlocked);
                w.Write(st.ResearchTicksElapsed);
                w.Write(st.ResearchTicksTotal);
                w.Write(st.BuilderPlayerId);
                w.Write(st.BuildingEntityId);
                WriteBundle(w, st.ResearchTarget);
                WriteBundle(w, st.ResearchContributed);
                WriteBundle(w, st.BuildTarget);
                WriteBundle(w, st.BuildContributed);
                for (int p = 1; p <= 3; p++)
                {
                    WriteBundle(w, st.PlayerResearchContrib[p]);
                    WriteBundle(w, st.PlayerBuildContrib[p]);
                }
            }
        }

        public static void ReadState(BinaryReader r)
        {
            bool init = r.ReadBoolean();
            int pc = r.ReadByte();
            if (!init)
            {
                Reset();
                return;
            }
            PlayerCount = Math.Max(1, Math.Min(3, pc));
            for (int i = 0; i < 3; i++)
            {
                var st = new WonderState { Kind = (FunctionalWonderType)i };
                st.Phase = (WonderPhase)r.ReadByte();
                st.ResearchUnlocked = r.ReadBoolean();
                st.ResearchTicksElapsed = r.ReadInt32();
                st.ResearchTicksTotal = r.ReadInt32();
                st.BuilderPlayerId = r.ReadByte();
                st.BuildingEntityId = r.ReadUInt64();
                st.ResearchTarget = ReadBundle(r);
                st.ResearchContributed = ReadBundle(r);
                st.BuildTarget = ReadBundle(r);
                st.BuildContributed = ReadBundle(r);
                for (int p = 1; p <= 3; p++)
                {
                    st.PlayerResearchContrib[p] = ReadBundle(r);
                    st.PlayerBuildContrib[p] = ReadBundle(r);
                }
                _wonders[i] = st;
            }
            IsInitialized = true;
        }

        private static void WriteBundle(BinaryWriter w, CostBundle b)
        {
            w.Write(b.Gold); w.Write(b.Wood); w.Write(b.Stone); w.Write(b.Iron); w.Write(b.Oil);
        }

        private static CostBundle ReadBundle(BinaryReader r)
        {
            return new CostBundle
            {
                Gold = r.ReadInt32(),
                Wood = r.ReadInt32(),
                Stone = r.ReadInt32(),
                Iron = r.ReadInt32(),
                Oil = r.ReadInt32()
            };
        }

        public static ulong ComputeHash()
        {
            if (!IsInitialized) return 0UL;
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < 3; i++)
            {
                var st = _wonders[i];
                if (st == null) continue;
                h = Mix(h, (ulong)st.Phase);
                h = Mix(h, st.ResearchUnlocked ? 1UL : 0UL);
                h = Mix(h, (uint)st.ResearchTicksElapsed);
                h = Mix(h, (uint)st.ResearchTicksTotal);
                h = Mix(h, st.BuilderPlayerId);
                h = Mix(h, st.BuildingEntityId);
                h = MixBundle(h, st.ResearchContributed);
                h = MixBundle(h, st.BuildContributed);
                for (int p = 1; p <= 3; p++)
                {
                    h = MixBundle(h, st.PlayerResearchContrib[p]);
                    h = MixBundle(h, st.PlayerBuildContrib[p]);
                }
            }
            return h;
        }

        private static ulong MixBundle(ulong h, CostBundle b)
        {
            h = Mix(h, (uint)b.Gold);
            h = Mix(h, (uint)b.Wood);
            h = Mix(h, (uint)b.Stone);
            h = Mix(h, (uint)b.Iron);
            return Mix(h, (uint)b.Oil);
        }

        private static ulong Mix(ulong h, ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
            return h;
        }
    }
}
