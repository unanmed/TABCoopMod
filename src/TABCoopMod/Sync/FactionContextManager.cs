using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Patches;

namespace TABCoopMod.Sync
{
    public enum ResourceTransferKind : byte
    {
        Gold = 0,
        Wood = 1,
        Stone = 2,
        Iron = 3,
        Oil = 4
    }

    /// <summary>
    /// Phase 4 M2 Active Context Swap ledger manager.
    /// Maintains isolated economy, resource caps, extra CC bonuses, gift templates, and unlocked tech
    /// for each player (1..N). At rest, ZXLevelState mirrors LocalPlayerId so all vanilla UI,
    /// command panel checks, and tooltips naturally read only the local player's state.
    /// </summary>
    public static class FactionContextManager
    {
        public const string CoopMayorZeroFeeId = "CoopMayor_ZeroTransferFee";

        public sealed class FactionLedger
        {
            public GameBridge.RawLevelEconomyState Economy;
            public int OwnEnergySupply;
            public int OwnEnergyNeeded;
            public int TestExtraStorage;
            public int TestExtraWorkers;
            public int TestExtraFood;
            public int TestExtraEnergy;
            public int TestExtraColonists;
            public bool ShowFullMap;
            public Dictionary<ulong, int> BonusEntityTemplates = new Dictionary<ulong, int>();
            public Dictionary<int, int> OnlyOneExecutedCommands = new Dictionary<int, int>();

            public FactionLedger Clone()
            {
                return new FactionLedger
                {
                    Economy = Economy,
                    OwnEnergySupply = OwnEnergySupply,
                    OwnEnergyNeeded = OwnEnergyNeeded,
                    TestExtraStorage = TestExtraStorage,
                    TestExtraWorkers = TestExtraWorkers,
                    TestExtraFood = TestExtraFood,
                    TestExtraEnergy = TestExtraEnergy,
                    TestExtraColonists = TestExtraColonists,
                    BonusEntityTemplates = new Dictionary<ulong, int>(BonusEntityTemplates),
                    OnlyOneExecutedCommands = new Dictionary<int, int>(OnlyOneExecutedCommands)
                };
            }
        }

        private static readonly FactionLedger[] _factions = new FactionLedger[4]; // 1..3
        private static readonly Stack<byte> _contextStack = new Stack<byte>();
        private static byte _activeLevelStateOwner;

        public static bool IsInitialized { get; private set; }
        public static bool ZeroTransferFeeActive { get; set; }
        public static bool SharedEnergyGridActive { get; private set; }
        public static int SharedEnergySupply { get; private set; }
        public static int SharedEnergyNeeded { get; private set; }

        public static byte ActiveContextPlayerId =>
            _contextStack.Count > 0 ? _contextStack.Peek() : GetRestingPlayerId();

        public static byte CurrentActiveOwner => _activeLevelStateOwner;
        public static bool HasAnyAliveMarket() => AnyAlivePlayerHasMarket();
        public static int GetStock(byte pid, ResourceTransferKind res)
        {
            var l = GetLedger(pid);
            return l != null ? GetStockByResourceType(in l.Economy, (int)res) : 0;
        }
        public static bool ExecuteTransferResource(byte fromPid, byte toPid, ResourceTransferKind res, int grossAmount, out int netAmount) =>
            ExecuteTransferResource(fromPid, toPid, (int)res, grossAmount, out netAmount);
        public static void AdjustStockAndSync(byte pid, ResourceTransferKind res, int delta)
        {
            var l = GetLedger(pid);
            if (l == null) return;
            AdjustStockOnActive(ref l.Economy, (int)res, delta);
            if (_activeLevelStateOwner == pid)
                GameBridge.AddLevelResource(res, delta);
        }

        public static int ContextDepth => _contextStack.Count;

        private static byte GetRestingPlayerId()
        {
            byte lid = CommandInterceptor.LocalPlayerId;
            if (lid >= 1 && lid <= 3) return lid;
            return 1;
        }

        public static void Reset()
        {
            for (int i = 0; i < _factions.Length; i++)
                _factions[i] = null;
            _contextStack.Clear();
            _activeLevelStateOwner = 0;
            IsInitialized = false;
            ZeroTransferFeeActive = false;
            SharedEnergyGridActive = false;
            SharedEnergySupply = 0;
            SharedEnergyNeeded = 0;
        }

        public static FactionLedger GetLedger(byte playerId)
        {
            if (playerId < 1 || playerId > 3) return null;
            return _factions[playerId];
        }

        public static bool AnyAlivePlayerHasMarket()
        {
            if (!IsInitialized) return (GameBridge.ReadRawLevelEconomyState(out var s) && s.NMarkets > 0);
            const int maxP = 3;
            for (byte p = 1; p <= maxP; p++)
            {
                if (FactionOwnershipTracker.IsExiled(p)) continue;
                var l = _factions[p];
                if (l != null && l.Economy.NMarkets > 0)
                    return true;
            }
            return false;
        }

        public static int GetTotalMarketCount()
        {
            if (!IsInitialized) return GameBridge.ReadRawLevelEconomyState(out var s) ? s.NMarkets : 0;
            int total = 0;
            int maxP = Math.Max(1, Math.Min(3, (int)FactionOwnershipTracker.ActivePlayerCount));
            for (byte p = 1; p <= maxP; p++)
            {
                if (FactionOwnershipTracker.IsExiled(p)) continue;
                if (_factions[p] != null) total += _factions[p].Economy.NMarkets;
            }
            return total;
        }

        /// <summary>
        /// Initializes all player ledgers symmetrically from the loaded/starting ZXLevelState.
        /// Called on Host before snapshot export when starting a fresh Multi-Faction match.
        /// </summary>
        public static void InitializeFromCurrentLevelState(int playerCount)
        {
            if (!GameBridge.ReadRawLevelEconomyState(out var baseEcon))
                return;

            int count = Math.Max(1, Math.Min(3, playerCount));
            var baseBonus = GameBridge.GetLevelBonusTemplatesDict() ?? new Dictionary<ulong, int>();
            var baseTechs = GameBridge.GetLevelOnlyOneCommandsDict() ?? new Dictionary<int, int>();

            byte[] slots = FactionOwnershipTracker.GetParticipatingSlotsSorted();
            byte firstSlot = (slots != null && slots.Length > 0) ? slots[0] : (byte)1;
            var activeSlots = new HashSet<byte>();
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++)
                    activeSlots.Add(slots[i]);
            }
            if (activeSlots.Count == 0)
            {
                for (byte p = 1; p <= count; p++)
                    activeSlots.Add(p);
            }

            for (byte p = 1; p <= 3; p++)
            {
                if (!activeSlots.Contains(p))
                {
                    _factions[p] = null;
                    continue;
                }
                _factions[p] = new FactionLedger
                {
                    Economy = baseEcon,
                    OwnEnergySupply = baseEcon.EnergySupply,
                    OwnEnergyNeeded = baseEcon.EnergyNeeded,
                    ShowFullMap = GameBridge.GetLevelStateShowFullMap(),
                    // Starter match starts with symmetric starting stock & unlocked commands;
                    // mid-game saves with Mayor bonuses keep gifts only on the first active slot so bonuses aren't duplicated.
                    BonusEntityTemplates = p == firstSlot ? new Dictionary<ulong, int>(baseBonus) : new Dictionary<ulong, int>(),
                    OnlyOneExecutedCommands = new Dictionary<int, int>(baseTechs)
                };
            }

            ZeroTransferFeeActive = false;
            SharedEnergyGridActive = false;
            _contextStack.Clear();
            _activeLevelStateOwner = GetRestingPlayerId();
            IsInitialized = true;

            RecalculateAllFactions();
            LoadOwnerIntoLevelState(_activeLevelStateOwner);
            ModLogger.Info($"[FactionContext] Initialized {count} independent ledgers (gold={baseEcon.Gold}, wood={baseEcon.Wood}, techs={baseTechs.Count}).");
        }

        public static void EnsureInitializedAfterRestore()
        {
            if (!CoopSessionManager.IsMultiFactionActive) return;
            if (!IsInitialized)
            {
                int count = Math.Max(1, (int)FactionOwnershipTracker.ActivePlayerCount);
                InitializeFromCurrentLevelState(count);
                return;
            }
            _contextStack.Clear();
            byte rest = GetRestingPlayerId();
            _activeLevelStateOwner = rest;
            LoadOwnerIntoLevelState(rest);
            RecalculateAllFactions();
        }

        private static void EnsureSimIdleOnMainThread()
        {
            if (!Determinism.DeterministicSandboxPatches.IsAsyncTickInFlight)
                return;
            int mainId = Determinism.LockstepClock.MainThreadId;
            int curId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (mainId != 0 && curId == mainId && Determinism.LockstepClock.LogicThreadId != curId)
                Determinism.DeterministicSandboxPatches.FlushPendingAsyncTick(DXGame.Current);
        }

        public static void PushContext(byte playerId)
        {
            if (!CoopSessionManager.IsMultiFactionActive || !IsInitialized)
                return;
            EnsureSimIdleOnMainThread();
            if (playerId < 1 || playerId > 3)
                playerId = 1;

            // Lazy flush: when ZXLevelState already holds playerId's ledger, pushing again needs
            // neither an 80-field ReadRawLevelEconomyState nor LoadOwnerIntoLevelState.
            if (_activeLevelStateOwner != playerId)
            {
                FlushActiveOwnerFromLevelState();
                LoadOwnerIntoLevelState(playerId);
            }
            _contextStack.Push(playerId);
        }

        public static void PopContext()
        {
            if (!CoopSessionManager.IsMultiFactionActive || !IsInitialized || _contextStack.Count == 0)
                return;
            EnsureSimIdleOnMainThread();
            if (_contextStack.Count == 0)
                return;

            _contextStack.Pop();
            byte target = _contextStack.Count > 0 ? _contextStack.Peek() : GetRestingPlayerId();
            if (_activeLevelStateOwner != target)
            {
                FlushActiveOwnerFromLevelState();
                LoadOwnerIntoLevelState(target);
            }
        }

        [ThreadStatic]
        internal static bool IsLoadingOwnerState;

        public static void FlushActiveOwnerFromLevelState()
        {
            if (_activeLevelStateOwner < 1 || _activeLevelStateOwner > 3) return;
            var ledger = _factions[_activeLevelStateOwner];
            if (ledger == null) return;

            if (GameBridge.ReadRawLevelEconomyState(out var raw))
            {
                ledger.Economy = raw;
                if (!SharedEnergyGridActive)
                {
                    ledger.OwnEnergySupply = raw.EnergySupply;
                    ledger.OwnEnergyNeeded = raw.EnergyNeeded;
                }
            }
            bool curShowFullMap = GameBridge.GetLevelStateShowFullMap();
            if (curShowFullMap && !ledger.ShowFullMap)
            {
                // ShowFullMap is global via shared ZXSystemMapLayers fog: propagate any vanilla trigger
                // (FinalSwarm ShowAllMap / Telescope) across all faction ledgers so PopContext() never reverts it.
                for (byte p = 1; p <= 3; p++)
                {
                    if (_factions[p] != null)
                        _factions[p].ShowFullMap = true;
                }
            }
            ledger.ShowFullMap = curShowFullMap;
            var bonus = GameBridge.GetLevelBonusTemplatesDict();
            if (bonus != null) ledger.BonusEntityTemplates = bonus;
            var techs = GameBridge.GetLevelOnlyOneCommandsDict();
            if (techs != null) ledger.OnlyOneExecutedCommands = techs;
        }

        internal static void OnNativeShowFullMapSet(bool value)
        {
            if (IsLoadingOwnerState || Determinism.LockstepClock.LoadingLevel || !value)
                return;

            if (CoopSessionManager.IsMultiFactionActive && IsInitialized)
            {
                for (byte p = 1; p <= 3; p++)
                {
                    if (_factions[p] != null)
                        _factions[p].ShowFullMap = true;
                }
            }

            if (Determinism.LockstepClock.IsDeterministicModeActive)
            {
                // Apply FogEnabled/ShadowEnabled synchronously on the deterministic simulation tick rather than
                // waiting for each peer's asynchronous OnStartFrame render loop (prevents ZXBehaviour watch interval drift).
                GameBridge.SetFogEnabled(false);
                GameBridge.SetShadowEnabled(false);
            }
        }

        private static void LoadOwnerIntoLevelState(byte playerId)
        {
            if (playerId < 1 || playerId > 3) playerId = 1;
            var ledger = _factions[playerId];
            if (ledger == null) return;

            IsLoadingOwnerState = true;
            try
            {
                GameBridge.WriteRawLevelEconomyState(in ledger.Economy);
                GameBridge.SetLevelStateShowFullMap(ledger.ShowFullMap);
                GameBridge.SetLevelBonusTemplatesDict(ledger.BonusEntityTemplates);
                GameBridge.SetLevelOnlyOneCommandsDict(ledger.OnlyOneExecutedCommands);
                _activeLevelStateOwner = playerId;
            }
            finally
            {
                IsLoadingOwnerState = false;
            }
        }

        /// <summary>
        /// Multi-faction replacement for ZXLevelState.#=zBpC6By32LAqv(): computes each player's
        /// production, population, storage, upkeep, and market count solely from entities they own,
        /// while preserving cross-faction enhancer building auras and optional TheSpire energy pooling.
        /// </summary>
        public static void RecalculateAllFactions()
        {
            var game = DXGame.Current;
            if (!CoopSessionManager.IsMultiFactionActive || !IsInitialized || game == null)
                return;

            FlushActiveOwnerFromLevelState();
            byte savedOwner = _activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3 ? _activeLevelStateOwner : GetRestingPlayerId();

            // 1. Scan only Buildings, Humans, and DefensiveMachines via engine type indices instead of 128k full entities.
            var allBuildings = new List<DXEntity>(128);
            var allUnits = new List<DXEntity>(128);
            var enhancers = new List<DXEntity>(16);
            bool spireAliveEnabled = false;

            Type tBuilding = GameBridge.TypeBuilding;
            Type tSpire = GameBridge.TypeTheSpire;
            if (tBuilding != null)
            {
                foreach (var ent in game.EntitiesOfType(tBuilding))
                {
                    if (ent == null || ent.Disposed) continue;
                    allBuildings.Add(ent);
                    var dp = GameBridge.GetEntityDefaultParamsBoxed(ent);
                    if (dp != null)
                    {
                        GameBridge.ReadDefaultParamsStats(dp, out bool canEnhance, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
                        if (canEnhance && GameBridge.IsEntityEnabled(ent))
                            enhancers.Add(ent);
                    }
                    if (tSpire != null && tSpire.IsInstanceOfType(ent)
                        && GameBridge.IsPlayerEntity(ent) && GameBridge.IsEntityEnabled(ent)
                        && !GameBridge.IsBuildingBeingBuilt(ent) && !GameBridge.IsBuildingInfected(ent))
                    {
                        spireAliveEnabled = true;
                    }
                }
                if (allBuildings.Count > 1)
                    allBuildings.Sort(GameBridge.CompareEntitiesDeterministically);
                if (enhancers.Count > 1)
                    enhancers.Sort(GameBridge.CompareEntitiesDeterministically);
            }

            Type tHuman = GameBridge.TypeHuman;
            if (tHuman != null)
            {
                foreach (var ent in game.EntitiesOfType(tHuman))
                {
                    if (ent != null && !ent.Disposed)
                        allUnits.Add(ent);
                }
            }
            Type tDefMachine = GameBridge.TypeDefensiveMachine;
            if (tDefMachine != null)
            {
                foreach (var ent in game.EntitiesOfType(tDefMachine))
                {
                    if (ent == null || ent.Disposed) continue;
                    if (tHuman != null && tHuman.IsInstanceOfType(ent)) continue;
                    allUnits.Add(ent);
                }
            }
            if (allUnits.Count > 1)
                allUnits.Sort(GameBridge.CompareEntitiesDeterministically);
            const int maxP = 3;
            var enhancersByOwner = new List<DXEntity>[4];
            for (byte pid = 1; pid <= maxP; pid++)
                enhancersByOwner[pid] = new List<DXEntity>(8);
            for (int i = 0; i < enhancers.Count; i++)
            {
                var enh = enhancers[i];
                byte enhOwner = FactionOwnershipTracker.ResolveOrAssignOwner(enh);
                if (enhOwner >= 1 && enhOwner <= maxP)
                    enhancersByOwner[enhOwner].Add(enh);
            }

            float sellWood = GameBridge.GetLevelSellFactor(0);
            float sellStone = GameBridge.GetLevelSellFactor(1);
            float sellIron = GameBridge.GetLevelSellFactor(2);
            float sellOil = GameBridge.GetLevelSellFactor(3);

            int totalSpirePoolSupply = 0;
            int totalSpirePoolNeeded = 0;

            for (byte pid = 1; pid <= maxP; pid++)
            {
                var ledger = _factions[pid];
                if (ledger == null) continue;

                // Scope enhancer aura calculations (Market -20% FoodNeeded, Warehouse +20% Output, Bank)
                // strictly to buildings owned by the same faction, and invalidate the cached Render_Iteration>>4
                // enhancer stamp so simulation ticks never depend on local render FPS.
                GameBridge.CallLevelStateSetEnhancerBuildings(enhancersByOwner[pid]);

                var e = ledger.Economy;
                e.ResourceStorage = 0;
                e.FoodSupply = 0;
                e.WorkersSupply = 0;
                e.EnergySupply = 0;
                e.FoodNeeded = e.FoodReserved;
                e.WorkersNeeded = e.WorkersReserved;
                e.EnergyNeeded = e.EnergyReserved;
                e.Colonists = 0;
                e.WoodProd = -e.WoodProdRes;
                e.StoneProd = -e.StoneProdRes;
                e.IronProd = -e.IronProdRes;
                e.OilProd = -e.OilProdRes;
                e.GoldProd = 0;
                e.MaxGoldExport = 0;

                int nMarkets = 0;
                int armyGoldCost = 0;
                int structGoldCost = 0;
                float goldPerColonist = 0f;
                int numGoldAdd = 0;

                // Note: while scanning this player's buildings, temporarily swap CC extras into ZXLevelState
                // only if needed; Reading BuildingTotals directly from cached #=zXbxJzH8= avoids extra work
                // because Building.#=z3rO8Jok=() already populated CC totals using the CC owner's extras.
                for (int i = 0; i < allBuildings.Count; i++)
                {
                    var item = allBuildings[i];
                    if (item == null || item.Disposed || !GameBridge.IsPlayerEntity(item)) continue;
                    if (FactionOwnershipTracker.ResolveOrAssignOwner(item) != pid) continue;
                    if (GameBridge.GetBuildingDisabledManually(item) || GameBridge.IsBuildingInfected(item)) continue;

                    var dp = GameBridge.GetEntityDefaultParamsBoxed(item);
                    if (dp == null) continue;

                    GameBridge.InvalidateBuildingEnhancerCache(item);
                    GameBridge.ReadDefaultParamsStats(dp,
                        out _, out float resGenTime, out bool convertGold,
                        out int dpCol, out int dpStorage, out int dpGoldGen, out float dpGoldPerCol,
                        out int dpFoodNeed, out int dpWorkNeed, out int dpEngNeed,
                        out _, out _, out _, out _, out _, out _, out _, out _, out _);

                    GameBridge.ReadBuildingTotals(item,
                        out int totalFoodSup, out int totalWorkSup, out int totalEngSup,
                        out int totalWoodSup, out int totalStoneSup, out int totalIronSup, out int totalOilSup, out int totalGoldSup,
                        out int totalFoodNeed);

                    e.FoodNeeded += totalFoodNeed;
                    e.WorkersNeeded += dpWorkNeed;
                    e.EnergyNeeded += dpEngNeed;
                    goldPerColonist += dpGoldPerCol;

                    if (GameBridge.GetBuildingCanSupplyResources(item))
                    {
                        e.Colonists += dpCol;
                        e.ResourceStorage += dpStorage;
                        e.FoodSupply += totalFoodSup;
                        e.WorkersSupply += totalWorkSup;
                        e.EnergySupply += totalEngSup;
                        if (GameBridge.TypeTheSpire != null && GameBridge.TypeTheSpire.IsInstanceOfType(item) && WonderCrowdfundManager.IsInitialized)
                        {
                            int scaledSpire = WonderCrowdfundManager.GetScaledSpireEnergy(totalEngSup > 0 ? totalEngSup : 800);
                            if (scaledSpire > totalEngSup)
                                e.EnergySupply += (scaledSpire - totalEngSup);
                        }

                        float genFactor = resGenTime > 0f ? resGenTime : 1f;
                        if (totalWoodSup > 0) e.MaxGoldExport += (int)(totalWoodSup * sellWood / genFactor);
                        if (totalStoneSup > 0) e.MaxGoldExport += (int)(totalStoneSup * sellStone / genFactor);
                        if (totalIronSup > 0) e.MaxGoldExport += (int)(totalIronSup * sellIron / genFactor);
                        if (totalOilSup > 0) e.MaxGoldExport += (int)(totalOilSup * sellOil / genFactor);

                        e.WoodProd += totalWoodSup;
                        e.IronProd += totalIronSup;
                        e.StoneProd += totalStoneSup;
                        e.OilProd += totalOilSup;

                        if (dpGoldGen >= 0)
                            numGoldAdd += totalGoldSup;
                        else
                        {
                            structGoldCost -= dpGoldGen;
                            numGoldAdd += totalGoldSup - dpGoldGen;
                        }
                        if (convertGold) nMarkets++;
                    }
                    else if (!GameBridge.IsBuildingInfected(item))
                    {
                        var baseEnt = GameBridge.TryGetDwellingPreUpgradeTemplateEntity(item);
                        var dp2 = baseEnt != null ? GameBridge.GetEntityDefaultParamsBoxed(baseEnt) : null;
                        if (dp2 != null)
                        {
                            GameBridge.ReadDefaultParamsStats(dp2,
                                out _, out _, out _, out int dp2Col, out _, out int dp2Gold, out _, out _, out _, out _,
                                out _, out _, out _, out _, out _, out _, out _, out _, out _);
                            int dp2WorkSup = (GameBridge.TypeBuilding != null && GameBridge.TypeBuilding.IsInstanceOfType(baseEnt))
                                ? GameBridge.GetEntityWorkSupplyFallback(dp2) : 0;
                            e.Colonists += dp2Col;
                            e.WorkersSupply += dp2WorkSup;
                            numGoldAdd += dp2Gold;
                        }
                    }
                }

                e.ResourceStorage = Math.Max(50, e.ResourceStorage + ledger.TestExtraStorage);
                e.WorkersSupply += ledger.TestExtraWorkers;
                e.FoodSupply += ledger.TestExtraFood;
                e.EnergySupply += ledger.TestExtraEnergy;
                e.Colonists += ledger.TestExtraColonists;

                int armyFoodPen = 0;
                int uWood = 0, uStone = 0, uIron = 0, uOil = 0;
                for (int i = 0; i < allUnits.Count; i++)
                {
                    var u = allUnits[i];
                    if (u == null || u.Disposed || !GameBridge.IsPlayerEntity(u)) continue;
                    if (FactionOwnershipTracker.ResolveOrAssignOwner(u) != pid) continue;
                    if (!GameBridge.IsEntityAlive(u)) continue;

                    var dp3 = GameBridge.GetEntityDefaultParamsBoxed(u);
                    if (dp3 == null) continue;
                    GameBridge.ReadDefaultParamsStats(dp3,
                        out _, out _, out _, out _, out _, out int uGoldGen, out _,
                        out int uFoodNeed, out int uWorkNeed, out int uEngNeed,
                        out int wg, out int sg, out int ig, out int og,
                        out _, out _, out _, out _, out _);

                    e.FoodNeeded += uFoodNeed;
                    if (e.FoodNeeded > e.FoodSupply)
                        armyFoodPen -= (int)Math.Round((float)uGoldGen);
                    e.WorkersNeeded += uWorkNeed;
                    e.EnergyNeeded += uEngNeed;
                    armyGoldCost -= uGoldGen;
                    uWood += wg;
                    uStone += sg;
                    uIron += ig;
                    uOil += og;
                }

                e.WoodProd += uWood;
                e.StoneProd += uStone;
                e.IronProd += uIron;
                e.OilProd += uOil;
                e.TotalStock = e.Wood + e.Stone + e.Iron + e.Oil;
                e.NMarkets = nMarkets;
                e.ArmyGold = armyGoldCost;
                e.ArmyFoodPen = armyFoodPen;
                e.StructGold = structGoldCost;
                e.GoldPerColonist = goldPerColonist;
                e.GoldByCc = numGoldAdd + (int)(goldPerColonist * e.Colonists) - structGoldCost - armyGoldCost - armyFoodPen;
                e.GoldProd = e.GoldByCc;
                e.MaxColonists = Math.Max(e.MaxColonists, e.Colonists);

                ledger.OwnEnergySupply = e.EnergySupply;
                ledger.OwnEnergyNeeded = e.EnergyNeeded;
                if (!FactionOwnershipTracker.IsExiled(pid))
                {
                    totalSpirePoolSupply += e.EnergySupply;
                    totalSpirePoolNeeded += e.EnergyNeeded;
                }
                ledger.Economy = e;
            }

            SharedEnergyGridActive = spireAliveEnabled;
            SharedEnergySupply = totalSpirePoolSupply;
            SharedEnergyNeeded = totalSpirePoolNeeded;

            if (spireAliveEnabled)
            {
                for (byte pid = 1; pid <= maxP; pid++)
                {
                    var l = _factions[pid];
                    if (l == null || FactionOwnershipTracker.IsExiled(pid)) continue;
                    l.Economy.EnergySupply = totalSpirePoolSupply;
                    l.Economy.EnergyNeeded = totalSpirePoolNeeded;
                }
            }

            // Restore global enhancer list & clear cache stamps so vanilla tooltips reflect global enhancers.
            GameBridge.CallLevelStateSetEnhancerBuildings(enhancers);
            for (int i = 0; i < allBuildings.Count; i++)
                GameBridge.InvalidateBuildingEnhancerCache(allBuildings[i]);

            LoadOwnerIntoLevelState(savedOwner);
        }

        public static bool ApplyAsyncEconDeltasForPlayer(
            byte pid,
            int dGold, int dWood, int dStone, int dIron, int dOil,
            int dFoodRes, int dWorkRes, int dEngRes,
            int dWoodProdRes, int dStoneProdRes, int dIronProdRes, int dOilProdRes)
        {
            if (!IsInitialized || pid < 1 || pid > 3) return false;
            if (dGold == 0 && dWood == 0 && dStone == 0 && dIron == 0 && dOil == 0
                && dFoodRes == 0 && dWorkRes == 0 && dEngRes == 0
                && dWoodProdRes == 0 && dStoneProdRes == 0 && dIronProdRes == 0 && dOilProdRes == 0)
            {
                return false;
            }

            PushContext(pid);
            try
            {
                if (dGold != 0) GameBridge.AddLevelResourceDelta("Gold", dGold);
                if (dWood != 0) GameBridge.AddLevelResourceDelta("Wood", dWood);
                if (dStone != 0) GameBridge.AddLevelResourceDelta("Stone", dStone);
                if (dIron != 0) GameBridge.AddLevelResourceDelta("Iron", dIron);
                if (dOil != 0) GameBridge.AddLevelResourceDelta("Oil", dOil);
                GameBridge.ApplyLevelReservationDeltas(dFoodRes, dWorkRes, dEngRes, dWoodProdRes, dStoneProdRes, dIronProdRes, dOilProdRes);
                return true;
            }
            finally
            {
                PopContext();
            }
        }

        public static void Execute8HourStepForPlayer(byte pid)
        {
            if (!IsInitialized || pid < 1 || pid > 3 || FactionOwnershipTracker.IsExiled(pid))
                return;

            PushContext(pid);
            try
            {
                var ledger = _factions[pid];
                if (ledger == null) return;
                GameBridge.ReadRawLevelEconomyState(out var e);

                int hw = 0, hs = 0, hi = 0, ho = 0;
                var game = DXGame.Current;
                Type tHuman = GameBridge.TypeHuman;
                if (game != null && tHuman != null)
                {
                    foreach (var h in game.EntitiesOfType(tHuman))
                    {
                        if (h == null || h.Disposed || !GameBridge.IsPlayerEntity(h)) continue;
                        if (FactionOwnershipTracker.GetOwner(h.ID) != pid) continue;
                        var dp = GameBridge.GetEntityDefaultParamsBoxed(h);
                        if (dp == null) continue;
                        GameBridge.ReadDefaultParamsStats(dp, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                            out int wg, out int sg, out int ig, out int og, out _, out _, out _, out _, out _);
                        hw += wg; hs += sg; hi += ig; ho += og;
                    }
                }
                e.Wood = Math.Max(0, Math.Min(e.ResourceStorage, e.Wood + hw));
                e.Stone = Math.Max(0, Math.Min(e.ResourceStorage, e.Stone + hs));
                e.Iron = Math.Max(0, Math.Min(e.ResourceStorage, e.Iron + hi));
                e.Oil = Math.Max(0, Math.Min(e.ResourceStorage, e.Oil + ho));

                if (e.Gold >= 0)
                {
                    e.NegGoldTurns = 0;
                }
                else
                {
                    e.NegGoldTurns++;
                    Type tUnit = GameBridge.TypeUnit;
                    if (e.NegGoldTurns >= 2 && game != null && tUnit != null)
                    {
                        int deficit = -e.Gold;
                        var candidates = new List<(DXEntity U, int Level, int GoldCost)>();
                        foreach (var u in game.EntitiesOfType(tUnit))
                        {
                            if (u == null || u.Disposed || !GameBridge.IsPlayerEntity(u)) continue;
                            if (FactionOwnershipTracker.GetOwner(u.ID) != pid) continue;
                            if (GameBridge.HasInsideBuildingComponent(u)) continue;
                            var dp = GameBridge.GetEntityDefaultParamsBoxed(u);
                            if (dp == null) continue;
                            GameBridge.ReadDefaultParamsStats(dp, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _,
                                out _, out _, out _, out _, out _, out int gc, out int lvl, out _, out _);
                            candidates.Add((u, lvl, gc));
                        }
                        candidates.Sort((a, b) =>
                        {
                            int c = a.Level.CompareTo(b.Level);
                            return c != 0 ? c : a.U.ID.CompareTo(b.U.ID);
                        });
                        for (int i = 0; i < candidates.Count && deficit > 0; i++)
                        {
                            if (deficit < candidates[i].GoldCost) break;
                            candidates[i].U.Dispose();
                            e.Gold = Math.Min(e.ResourceStorage * 40, e.Gold + candidates[i].GoldCost);
                            deficit -= candidates[i].GoldCost;
                        }
                    }
                }

                if (e.FoodSupply - e.FoodNeeded >= 0)
                    e.NegFoodTurns = 0;
                else
                    e.NegFoodTurns++;

                GameBridge.WriteRawLevelEconomyState(in e);
                ledger.Economy = e;
            }
            finally
            {
                PopContext();
            }
        }

        /// <summary>
        /// Deterministically splits kill gold bounty (Params.GoldPerKill) when CLife death fires
        /// outside any command/building player context.
        /// </summary>
        public static void DistributeKillGoldDeterministically(int gold)
        {
            if (gold <= 0 || !IsInitialized) return;
            FlushActiveOwnerFromLevelState();
            const int maxP = 3;
            var alive = new List<byte>(3);
            for (byte p = 1; p <= maxP; p++)
            {
                if (!FactionOwnershipTracker.IsExiled(p) && FactionOwnershipTracker.IsSlotParticipating(p) && _factions[p] != null)
                    alive.Add(p);
            }
            if (alive.Count == 0) return;

            int share = gold / alive.Count;
            int rem = gold % alive.Count;
            for (int i = 0; i < alive.Count; i++)
            {
                int grant = share + (i < rem ? 1 : 0);
                if (grant <= 0) continue;
                var l = _factions[alive[i]];
                int maxGold = Math.Max(50, l.Economy.ResourceStorage) * 40;
                l.Economy.Gold = Math.Min(maxGold, l.Economy.Gold + grant);
            }

            if (_activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3)
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
        }

        public static bool CanTransferResource(byte fromPid, byte toPid, int resType, int grossAmount, out string reason)
        {
            reason = null;
            if (!CoopSessionManager.IsMultiFactionActive || !IsInitialized) { reason = "not in multi-faction match"; return false; }
            if (!AnyAlivePlayerHasMarket()) { reason = "requires at least 1 Market in the team"; return false; }
            if (fromPid == toPid || fromPid < 1 || toPid < 1 || fromPid > 3 || toPid > 3) { reason = "invalid peer"; return false; }
            if (FactionOwnershipTracker.IsExiled(fromPid) || FactionOwnershipTracker.IsExiled(toPid)) { reason = "player is exiled"; return false; }
            if (grossAmount <= 0 || resType < 0 || resType > 4) { reason = "invalid amount or resource"; return false; }

            var from = _factions[fromPid];
            var to = _factions[toPid];
            if (from == null || to == null) { reason = "ledger missing"; return false; }
            int avail = GetStockByResourceType(in from.Economy, resType);
            if (avail < grossAmount) { reason = $"insufficient stock ({avail} < {grossAmount})"; return false; }
            return true;
        }

        public static bool ExecuteTransferResource(byte fromPid, byte toPid, int resType, int grossAmount, out int netAmount)
        {
            netAmount = 0;
            if (!CanTransferResource(fromPid, toPid, resType, grossAmount, out string reason))
            {
                ModLogger.Info($"[FactionContext] Rejected transfer P{fromPid}->P{toPid} r={resType} amt={grossAmount}: {reason}");
                return false;
            }

            // Pure integer fee math (0% if ZeroTransferFeeActive, otherwise 20% fee => 80% net).
            netAmount = ZeroTransferFeeActive ? grossAmount : (grossAmount * 80) / 100;

            PushContext(fromPid);
            try
            {
                GameBridge.AddLevelResourceWithMarket(resType, -grossAmount);
            }
            finally
            {
                PopContext();
            }

            if (netAmount > 0)
            {
                PushContext(toPid);
                try
                {
                    GameBridge.AddLevelResourceWithMarket(resType, netAmount);
                }
                finally
                {
                    PopContext();
                }
            }

            RecalculateAllFactions();
            ModLogger.Info($"[FactionContext] Transferred res={resType} gross={grossAmount} net={netAmount} (feeFree={ZeroTransferFeeActive}) from P{fromPid} to P{toPid}.");
            return true;
        }

        public static int GetStockByResourceType(in GameBridge.RawLevelEconomyState e, int resType)
        {
            switch (resType)
            {
                case 0: return e.Gold;
                case 1: return e.Wood;
                case 2: return e.Stone;
                case 3: return e.Iron;
                case 4: return e.Oil;
                default: return 0;
            }
        }

        public static void AdjustStockOnActive(ref GameBridge.RawLevelEconomyState e, int resType, int delta)
        {
            switch (resType)
            {
                case 0: e.Gold = Math.Max(-10000, Math.Min(Math.Max(50, e.ResourceStorage) * 40, e.Gold + delta)); break;
                case 1: e.Wood = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Wood + delta)); break;
                case 2: e.Stone = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Stone + delta)); break;
                case 3: e.Iron = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Iron + delta)); break;
                case 4: e.Oil = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Oil + delta)); break;
            }
        }

        public static void AdjustResourcesForTesting(byte pid, int gold, int wood, int stone, int iron, int oil, int extraStorage = 0, int extraWorkers = 0, int extraColonists = 0, int extraFood = 0, int extraEnergy = 0)
        {
            if (!IsInitialized || pid < 1 || pid > 3) return;
            FlushActiveOwnerFromLevelState();
            var l = _factions[pid];
            if (l != null)
            {
                if (extraStorage != 0)
                {
                    l.TestExtraStorage = Math.Max(0, l.TestExtraStorage + extraStorage);
                    l.Economy.ResourceStorage = Math.Max(50, l.Economy.ResourceStorage + extraStorage);
                }
                if (extraWorkers != 0)
                {
                    l.TestExtraWorkers = Math.Max(0, l.TestExtraWorkers + extraWorkers);
                    l.Economy.WorkersSupply += extraWorkers;
                }
                if (extraFood != 0)
                {
                    l.TestExtraFood = Math.Max(0, l.TestExtraFood + extraFood);
                    l.Economy.FoodSupply += extraFood;
                }
                if (extraEnergy != 0)
                {
                    l.TestExtraEnergy = Math.Max(0, l.TestExtraEnergy + extraEnergy);
                    l.Economy.EnergySupply += extraEnergy;
                }
                if (extraColonists != 0)
                {
                    l.TestExtraColonists = Math.Max(0, l.TestExtraColonists + extraColonists);
                    l.Economy.Colonists += extraColonists;
                    l.Economy.MaxColonists = Math.Max(l.Economy.MaxColonists, l.Economy.Colonists);
                }
                if (gold != 0) AdjustStockOnActive(ref l.Economy, 0, gold);
                if (wood != 0) AdjustStockOnActive(ref l.Economy, 1, wood);
                if (stone != 0) AdjustStockOnActive(ref l.Economy, 2, stone);
                if (iron != 0) AdjustStockOnActive(ref l.Economy, 3, iron);
                if (oil != 0) AdjustStockOnActive(ref l.Economy, 4, oil);
            }
            if (_activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3)
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
        }

        public static bool HasUnlockedOnlyOneCommandForPlayer(byte pid, int commandId)
        {
            if (pid < 1 || pid > 3 || _factions[pid] == null) return false;
            return _factions[pid].OnlyOneExecutedCommands.TryGetValue(commandId, out int c) && c > 0;
        }

        public static void GrantOnlyOneCommandToAllAlivePlayers(int commandId)
        {
            if (commandId == 0 || !IsInitialized) return;
            FlushActiveOwnerFromLevelState();
            const int maxP = 3;
            for (byte p = 1; p <= maxP; p++)
            {
                if (FactionOwnershipTracker.IsExiled(p)) continue;
                var l = _factions[p];
                if (l != null && !l.OnlyOneExecutedCommands.ContainsKey(commandId))
                    l.OnlyOneExecutedCommands[commandId] = 1;
            }
            if (_activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3)
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
        }

        public static void SetShowFullMapForAllPlayers(bool show)
        {
            if (!IsInitialized) return;
            FlushActiveOwnerFromLevelState();
            for (byte p = 1; p <= 3; p++)
            {
                if (_factions[p] != null)
                    _factions[p].ShowFullMap = show;
            }
            if (_activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3)
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
        }

        public static void HandleExileHandover(byte exiledPid, byte recipientPid)
        {
            if (!IsInitialized || exiledPid < 1 || exiledPid > 3 || recipientPid < 1 || recipientPid > 3 || exiledPid == recipientPid)
                return;

            FlushActiveOwnerFromLevelState();
            var src = _factions[exiledPid];
            var dst = _factions[recipientPid];
            if (src == null || dst == null) return;

            dst.Economy.Gold += Math.Max(0, src.Economy.Gold);
            dst.Economy.Wood += Math.Max(0, src.Economy.Wood);
            dst.Economy.Stone += Math.Max(0, src.Economy.Stone);
            dst.Economy.Iron += Math.Max(0, src.Economy.Iron);
            dst.Economy.Oil += Math.Max(0, src.Economy.Oil);
            src.Economy.Gold = src.Economy.Wood = src.Economy.Stone = src.Economy.Iron = src.Economy.Oil = 0;

            foreach (var kv in src.BonusEntityTemplates)
            {
                dst.BonusEntityTemplates[kv.Key] = (dst.BonusEntityTemplates.TryGetValue(kv.Key, out int cur) ? cur : 0) + kv.Value;
            }
            src.BonusEntityTemplates.Clear();

            if (_activeLevelStateOwner >= 1 && _activeLevelStateOwner <= 3)
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
        }

        public static void WriteState(BinaryWriter w)
        {
            FlushActiveOwnerFromLevelState();
            w.Write(IsInitialized);
            w.Write(ZeroTransferFeeActive);
            w.Write(SharedEnergyGridActive);
            w.Write(SharedEnergySupply);
            w.Write(SharedEnergyNeeded);
            for (byte p = 1; p <= 3; p++)
            {
                var l = _factions[p];
                w.Write(l != null);
                if (l == null) continue;
                var e = l.Economy;
                w.Write(e.Wood); w.Write(e.Stone); w.Write(e.Iron); w.Write(e.Oil); w.Write(e.Gold); w.Write(e.ResourceStorage);
                w.Write(e.ExtraFood); w.Write(e.ExtraWorkers); w.Write(e.ExtraEnergy);
                w.Write(e.FoodReserved); w.Write(e.WorkersReserved); w.Write(e.EnergyReserved);
                w.Write(e.WoodProdRes); w.Write(e.StoneProdRes); w.Write(e.IronProdRes); w.Write(e.OilProdRes);
                w.Write(e.NegGoldTurns); w.Write(e.NegFoodTurns); w.Write(e.MaxColonists);
                w.Write(e.FoodSupply); w.Write(e.WorkersSupply); w.Write(e.EnergySupply);
                w.Write(e.FoodNeeded); w.Write(e.WorkersNeeded); w.Write(e.EnergyNeeded);
                w.Write(e.Colonists); w.Write(e.GoldByCc); w.Write(e.TotalStock);
                w.Write(e.WoodProd); w.Write(e.StoneProd); w.Write(e.IronProd); w.Write(e.OilProd); w.Write(e.GoldProd);
                w.Write(e.NMarkets); w.Write(e.MaxGoldExport); w.Write(e.ArmyGold); w.Write(e.ArmyFoodPen); w.Write(e.StructGold); w.Write(e.GoldPerColonist);
                w.Write(l.OwnEnergySupply); w.Write(l.OwnEnergyNeeded); w.Write(l.TestExtraStorage); w.Write(l.TestExtraWorkers); w.Write(l.TestExtraFood); w.Write(l.TestExtraEnergy); w.Write(l.ShowFullMap);

                w.Write(l.BonusEntityTemplates.Count);
                foreach (var kv in l.BonusEntityTemplates.OrderBy(k => k.Key))
                {
                    w.Write(kv.Key); w.Write(kv.Value);
                }
                w.Write(l.OnlyOneExecutedCommands.Count);
                foreach (var kv in l.OnlyOneExecutedCommands.OrderBy(k => k.Key))
                {
                    w.Write(kv.Key); w.Write(kv.Value);
                }
            }
        }

        public static void ReadState(BinaryReader r)
        {
            Reset();
            IsInitialized = r.ReadBoolean();
            ZeroTransferFeeActive = r.ReadBoolean();
            SharedEnergyGridActive = r.ReadBoolean();
            SharedEnergySupply = r.ReadInt32();
            SharedEnergyNeeded = r.ReadInt32();
            for (byte p = 1; p <= 3; p++)
            {
                bool has = r.ReadBoolean();
                if (!has) continue;
                var l = new FactionLedger();
                var e = new GameBridge.RawLevelEconomyState();
                e.Wood = r.ReadInt32(); e.Stone = r.ReadInt32(); e.Iron = r.ReadInt32(); e.Oil = r.ReadInt32();
                e.Gold = r.ReadInt32(); e.ResourceStorage = r.ReadInt32();
                e.ExtraFood = r.ReadInt32(); e.ExtraWorkers = r.ReadInt32(); e.ExtraEnergy = r.ReadInt32();
                e.FoodReserved = r.ReadInt32(); e.WorkersReserved = r.ReadInt32(); e.EnergyReserved = r.ReadInt32();
                e.WoodProdRes = r.ReadInt32(); e.StoneProdRes = r.ReadInt32(); e.IronProdRes = r.ReadInt32(); e.OilProdRes = r.ReadInt32();
                e.NegGoldTurns = r.ReadInt32(); e.NegFoodTurns = r.ReadInt32(); e.MaxColonists = r.ReadInt32();
                e.FoodSupply = r.ReadInt32(); e.WorkersSupply = r.ReadInt32(); e.EnergySupply = r.ReadInt32();
                e.FoodNeeded = r.ReadInt32(); e.WorkersNeeded = r.ReadInt32(); e.EnergyNeeded = r.ReadInt32();
                e.Colonists = r.ReadInt32(); e.GoldByCc = r.ReadInt32(); e.TotalStock = r.ReadInt32();
                e.WoodProd = r.ReadInt32(); e.StoneProd = r.ReadInt32(); e.IronProd = r.ReadInt32(); e.OilProd = r.ReadInt32(); e.GoldProd = r.ReadInt32();
                e.NMarkets = r.ReadInt32(); e.MaxGoldExport = r.ReadInt32(); e.ArmyGold = r.ReadInt32(); e.ArmyFoodPen = r.ReadInt32(); e.StructGold = r.ReadInt32(); e.GoldPerColonist = r.ReadSingle();
                l.OwnEnergySupply = r.ReadInt32(); l.OwnEnergyNeeded = r.ReadInt32();
                l.TestExtraStorage = r.ReadInt32();
                l.TestExtraWorkers = r.ReadInt32();
                l.TestExtraFood = r.ReadInt32();
                l.TestExtraEnergy = r.ReadInt32();
                l.ShowFullMap = r.ReadBoolean();
                l.Economy = e;

                int bc = r.ReadInt32();
                for (int i = 0; i < bc; i++) l.BonusEntityTemplates[r.ReadUInt64()] = r.ReadInt32();
                int tc = r.ReadInt32();
                for (int i = 0; i < tc; i++) l.OnlyOneExecutedCommands[r.ReadInt32()] = r.ReadInt32();
                _factions[p] = l;
            }
            if (IsInitialized)
            {
                _activeLevelStateOwner = GetRestingPlayerId();
                LoadOwnerIntoLevelState(_activeLevelStateOwner);
            }
        }

        public static ulong ComputeEconomyHash()
        {
            if (!IsInitialized) return 0UL;
            FlushActiveOwnerFromLevelState();
            ulong h = 0x517CC1B727220A95UL;
            h = Mix(h, ZeroTransferFeeActive ? 1UL : 0UL);
            h = Mix(h, SharedEnergyGridActive ? 1UL : 0UL);
            const int maxP = 3;
            for (byte p = 1; p <= maxP; p++)
            {
                if (!FactionOwnershipTracker.IsSlotParticipating(p)) continue;
                var l = _factions[p];
                if (l == null) continue;
                var e = l.Economy;
                h = Mix(h, p);
                h = Mix(h, (uint)e.Gold);
                h = Mix(h, (uint)e.Wood);
                h = Mix(h, (uint)e.Stone);
                h = Mix(h, (uint)e.Iron);
                h = Mix(h, (uint)e.Oil);
                h = Mix(h, (uint)e.FoodSupply);
                h = Mix(h, (uint)e.FoodNeeded);
                h = Mix(h, (uint)e.WorkersSupply);
                h = Mix(h, (uint)e.WorkersNeeded);
                h = Mix(h, (uint)e.EnergySupply);
                h = Mix(h, (uint)e.EnergyNeeded);
                h = Mix(h, (uint)e.NMarkets);
                h = Mix(h, (uint)l.OnlyOneExecutedCommands.Count);
                h = Mix(h, (uint)l.BonusEntityTemplates.Count);
            }
            return h;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            h ^= v;
            h *= 1099511628211UL;
            return h;
        }
    }
}
