using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Testing;
using ZX;

namespace TABCoopMod.Sync
{
    public enum CoopBalancePreset : byte
    {
        Balanced = 0,
        Vanilla = 1
    }

    public sealed class SurvivalBalanceProfile
    {
        public int PlayerCount { get; set; }
        // Actual lobby player count (2..MaxPlayers).
        public int ActualPlayerCount { get; set; }
        // Which enhancement tier (2 or 3) supplies map/unit/swarm scaling.
        public int BalanceScalePlayerCount { get; set; }
        public bool IsActive { get; set; }
        public int TargetNCells { get; set; }
        public float MapScale { get; set; }
        public float AreaScale { get; set; }
        public int ExtraRangers { get; set; }
        public int ExtraSoldiers { get; set; }
        public int ExtraWorkers { get; set; }
        public int ExtraFood { get; set; }
        public double RoamingDensityScale { get; set; }
        public float EarlySwarmScale { get; set; }
        public float LateSwarmScale { get; set; }
    }

    /// <summary>
    /// Host-side balance scaling for New Survival games in Co-op Archon mode.
    /// Mutates generator parameters once on the Host before the initial .zxsav snapshot
    /// is exported to clients, preserving 100% determinism and zero desync.
    /// Singleplayer, Load Save, Custom Map, Community Challenge, and Vanilla preset remain untouched.
    /// </summary>
    public static class CoopBalanceManager
    {
        [ThreadStatic]
        private static int _genDepth;

        [ThreadStatic]
        private static SurvivalBalanceProfile _activeProfile;

        [ThreadStatic]
        private static List<GameBridge.MapThemeBalanceSnapshot> _themeSnapshots;

        // Explicit test-only overrides used solely by ControlServer "start_survival_gen"
        // when rendering deterministic same-seed panorama comparisons.
        internal static int? TestOverridePlayerCount;
        internal static CoopBalancePreset? TestOverridePreset;
        internal static bool? TestOverrideMultiFaction;
        internal static MultiFactionSpawnMode? TestOverrideSpawnMode;

        internal static bool IsMultiFactionGenerationActive =>
            TestOverrideMultiFaction ?? CoopSessionManager.IsMultiFactionActive;

        public static SurvivalBalanceProfile GetMultiFactionProfileForPlayerCount(int playerCount, CoopBalancePreset preset)
        {
            int n = Math.Max(2, Math.Min(CoopLimits.MaxFactionPlayers, playerCount));
            bool balanced = preset == CoopBalancePreset.Balanced;
            int targetNCells = n >= 3 ? 416 : 360;
            return new SurvivalBalanceProfile
            {
                PlayerCount = n,
                ActualPlayerCount = n,
                BalanceScalePlayerCount = n,
                IsActive = true,
                TargetNCells = targetNCells,
                MapScale = targetNCells / 256f,
                AreaScale = balanced ? (n >= 3 ? 2.64f : 1.98f) : 1.0f,
                ExtraRangers = 0,
                ExtraSoldiers = 0,
                ExtraWorkers = 0,
                ExtraFood = 0,
                RoamingDensityScale = 1.0,
                EarlySwarmScale = n >= 3 ? 0.58f : 0.68f,
                LateSwarmScale = n >= 3 ? 0.98f : 1.08f
            };
        }

        public static SurvivalBalanceProfile GetProfileForPlayerCount(
            int playerCount,
            CoopBalancePreset preset,
            int balanceEnhancementPlayers = 0)
        {
            int actual = Math.Max(1, playerCount);

            if (preset == CoopBalancePreset.Vanilla || actual < 2)
            {
                return new SurvivalBalanceProfile
                {
                    PlayerCount = actual,
                    ActualPlayerCount = actual,
                    BalanceScalePlayerCount = actual,
                    IsActive = false,
                    TargetNCells = 256,
                    MapScale = 1.0f,
                    AreaScale = 1.0f,
                    ExtraRangers = 0,
                    ExtraSoldiers = 0,
                    ExtraWorkers = 0,
                    ExtraFood = 0,
                    RoamingDensityScale = 1.0,
                    EarlySwarmScale = 1.0f,
                    LateSwarmScale = 1.0f
                };
            }

            // Balance scaling is independent from the actual lobby size. A 6-player room can
            // deliberately use the 2P or 3P enhancement tier to keep map/infected pressure sane.
            int scale = balanceEnhancementPlayers > 0
                ? balanceEnhancementPlayers
                : Math.Min(actual, CoopLimits.MaxBalanceEnhancementPlayers);
            scale = Math.Max(2, Math.Min(CoopLimits.MaxBalanceEnhancementPlayers, scale));

            if (scale == 2)
            {
                return new SurvivalBalanceProfile
                {
                    PlayerCount = 2,
                    ActualPlayerCount = actual,
                    BalanceScalePlayerCount = 2,
                    IsActive = true,
                    TargetNCells = 280,
                    MapScale = 280f / 256f,
                    AreaScale = 1.20f,
                    ExtraRangers = 2,
                    ExtraSoldiers = 0,
                    ExtraWorkers = 2,
                    ExtraFood = 0,
                    RoamingDensityScale = 1.05,
                    EarlySwarmScale = 1.10f,
                    LateSwarmScale = 1.20f
                };
            }

            return new SurvivalBalanceProfile
            {
                PlayerCount = 3,
                ActualPlayerCount = actual,
                BalanceScalePlayerCount = 3,
                IsActive = true,
                TargetNCells = 304,
                MapScale = 304f / 256f,
                AreaScale = 1.41f,
                ExtraRangers = 2,
                ExtraSoldiers = 1,
                ExtraWorkers = 3,
                ExtraFood = 1,
                RoamingDensityScale = 1.10,
                EarlySwarmScale = 1.20f,
                LateSwarmScale = 1.40f
            };
        }

        public static string DescribePreset(CoopBalancePreset preset)
        {
            return preset == CoopBalancePreset.Vanilla
                ? "原版纯净 (100%原版参数)"
                : "联机均衡 (适度扩图+分兵/人口补偿)";
        }

        private static bool ShouldApplyToCurrentGeneration(out SurvivalBalanceProfile profile)
        {
            profile = null;
            if (TestOverridePreset.HasValue || TestOverrideMultiFaction == true)
            {
                int overrideCount = TestOverridePlayerCount ?? 2;
                if (overrideCount < 2) return false;
                CoopBalancePreset preset = TestOverridePreset ?? CoopBalancePreset.Balanced;
                if (TestOverrideMultiFaction == true)
                {
                    profile = GetMultiFactionProfileForPlayerCount(overrideCount, preset);
                    return profile.IsActive;
                }
                if (preset != CoopBalancePreset.Balanced)
                    return false;
                profile = GetProfileForPlayerCount(overrideCount, CoopBalancePreset.Balanced);
                return profile.IsActive;
            }

            if (!CoopSessionManager.IsSessionActive || CoopSessionManager.Transport == null || !CoopSessionManager.Transport.IsHost)
                return false;

            if (TestHarnessConfig.Enabled)
                return false;

            if (CoopSessionManager.IsMultiFactionActive)
            {
                string mfGameMode = GameBridge.GetGameStateProperty("GameMode")?.ToString();
                string mfChallenge = GameBridge.GetGameStateProperty("ChallengeType")?.ToString();
                if (!string.Equals(mfGameMode, "Survival", StringComparison.Ordinal) ||
                    !string.Equals(mfChallenge, "Default", StringComparison.Ordinal))
                {
                    return false;
                }
                int mfPlayers = CoopSessionManager.GetEffectivePeers().Count;
                if (mfPlayers < 2)
                    return false;

                profile = GetMultiFactionProfileForPlayerCount(mfPlayers, CoopSessionManager.BalancePreset);
                return true;
            }

            if (CoopSessionManager.BalancePreset != CoopBalancePreset.Balanced)
                return false;

            string gameMode = GameBridge.GetGameStateProperty("GameMode")?.ToString();
            string challengeType = GameBridge.GetGameStateProperty("ChallengeType")?.ToString();
            if (!string.Equals(gameMode, "Survival", StringComparison.Ordinal) ||
                !string.Equals(challengeType, "Default", StringComparison.Ordinal))
            {
                return false;
            }

            int players = CoopSessionManager.GetEffectivePeers().Count;
            if (players < 2)
                return false;

            profile = GetProfileForPlayerCount(
                players,
                CoopSessionManager.BalancePreset,
                CoopSessionManager.GetEffectiveBalanceEnhancementPlayers());
            return profile.IsActive;
        }

        public static void OnGeneratePrefix(object randomLevelParams)
        {
            int depth = _genDepth++;
            if (depth > 0) return;

            _activeProfile = null;
            _themeSnapshots = null;

            if (randomLevelParams == null) return;
            if (!ShouldApplyToCurrentGeneration(out var profile)) return;

            try
            {
                var themes = GameBridge.GetMapThemesDictionary();
                if (themes == null)
                {
                    ModLogger.Warn("[CoopBalance] MapThemes dictionary unavailable; skipping balance scaling.");
                    return;
                }

                bool isMulti = IsMultiFactionGenerationActive;
                _themeSnapshots = new List<GameBridge.MapThemeBalanceSnapshot>(themes.Count);
                foreach (DictionaryEntry entry in themes)
                {
                    if (entry.Value == null) continue;
                    var snap = GameBridge.CaptureMapThemeSnapshot(entry.Value);
                    _themeSnapshots.Add(snap);

                    if (isMulti)
                    {
                        float dvCountScale = profile.PlayerCount >= 3 ? 2.00f : 1.50f;
                        int dvMin = profile.PlayerCount >= 3 ? 3 : 2;
                        var scaledDvCount = ScaleRangeCeilWithFloor(snap.NumDoomVillages, dvCountScale, dvMin);
                        GameBridge.SetMapThemeNumDoomVillages(entry.Value, scaledDvCount);

                        float propScale = profile.PlayerCount >= 3 ? 2.40f : 1.80f;
                        float giantScale = profile.PlayerCount >= 3 ? 1.45f : 1.20f;
                        int mutantTarget = snap.NumInfectedMutants > 0
                            ? (profile.PlayerCount >= 3 ? snap.NumInfectedMutants + 1 : snap.NumInfectedMutants)
                            : 0;

                        GameBridge.ApplyMapThemeValues(
                            entry.Value,
                            snap.DoomVillagesSize,
                            ScaleRangeRound(snap.NumTreasures, propScale),
                            ScaleRangeRound(snap.NumSuppyFood, propScale),
                            ScaleRangeRound(snap.NumSuppyEnergy, propScale),
                            ScaleIntRound(snap.NumInfectedGiants, giantScale),
                            mutantTarget,
                            Math.Min(0.95, snap.InfectedFactor * profile.RoamingDensityScale));
                    }
                    else
                    {
                        GameBridge.ApplyMapThemeValues(
                            entry.Value,
                            ScaleRangeRound(snap.DoomVillagesSize, profile.MapScale),
                            ScaleRangeRound(snap.NumTreasures, profile.AreaScale),
                            ScaleRangeRound(snap.NumSuppyFood, profile.AreaScale),
                            ScaleRangeRound(snap.NumSuppyEnergy, profile.AreaScale),
                            ScaleIntRound(snap.NumInfectedGiants, profile.AreaScale),
                            ScaleIntRound(snap.NumInfectedMutants, profile.AreaScale),
                            Math.Min(0.95, snap.InfectedFactor * profile.RoamingDensityScale));
                    }
                }

                GameBridge.SetRandomParamsNCells(randomLevelParams, profile.TargetNCells);
                _activeProfile = profile;
                ModLogger.Info($"[CoopBalance] Pre-gen applied: actualPlayers={profile.ActualPlayerCount}, scalePlayers={profile.BalanceScalePlayerCount}, NCells={profile.TargetNCells}, mapScale={profile.MapScale:F3}, areaScale={profile.AreaScale:F2}.");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopBalance] OnGeneratePrefix failed; restoring themes.", ex);
                RestoreThemes();
                _activeProfile = null;
            }
        }

        public static void OnGeneratePostfix(DXLevel generatedLevel, object randomLevelParams)
        {
            if (_genDepth != 1) return;

            var profile = _activeProfile;
            if (profile == null || !profile.IsActive || generatedLevel == null) return;

            try
            {
                if (!GameBridge.TryGetGeneratedLevelContext(
                        generatedLevel,
                        out _,
                        out var extraEntities,
                        out var layerTerrain,
                        out var layerObjects,
                        out int gridNCells,
                        out var levelEvents))
                {
                    ModLogger.Warn("[CoopBalance] Could not resolve ZXLevelExtension/MapDrawer on generated level.");
                    return;
                }

                int scaledEvents = ScaleLevelEvents(levelEvents, randomLevelParams, profile);
                int spawnedUnits = 0;
                int multiBaseCount = 0;
                if (IsMultiFactionGenerationActive)
                {
                    var spawnMode = TestOverrideSpawnMode ?? CoopSessionManager.SpawnMode;
                    MultiBaseMapGenerator.PostProcessGeneratedLevel(generatedLevel, randomLevelParams, profile, spawnMode);
                    multiBaseCount = profile.PlayerCount;
                }
                else
                {
                    spawnedUnits = SpawnExtraStartingUnits(
                        extraEntities,
                        layerTerrain,
                        layerObjects,
                        gridNCells,
                        profile.ExtraRangers,
                        profile.ExtraSoldiers);
                }
                bool extrasSet = GameBridge.AddLevelStateCommandCenterExtras(profile.ExtraWorkers, profile.ExtraFood);

                ModLogger.Info(
                    $"[CoopBalance] Post-gen complete: actualPlayers={profile.ActualPlayerCount}, scalePlayers={profile.BalanceScalePlayerCount}, multiBases={multiBaseCount}, NCells={GameBridge.GetRandomParamsNCells(randomLevelParams)}, " +
                    $"scaledEvents={scaledEvents}, extraUnits={spawnedUnits} (+{profile.ExtraRangers} Ranger, +{profile.ExtraSoldiers} Soldier), " +
                    $"ccExtras=(+{profile.ExtraWorkers} workers, +{profile.ExtraFood} food, applied={extrasSet}).");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopBalance] OnGeneratePostfix failed.", ex);
            }
        }

        public static void OnGenerateFinalizer()
        {
            _genDepth = Math.Max(0, _genDepth - 1);
            if (_genDepth > 0) return;

            RestoreThemes();
            _activeProfile = null;
        }

        /// <summary>
        /// Replaces vanilla <c>(nCells / 2) - 60</c> when non-power-of-two target sizes (280/304)
        /// promote the backing grid to 512, preserving the exact proportional ~68/128 mid-ring
        /// radius and keeping Doom Villages comfortably inside playable map boundaries.
        /// </summary>
        public static int GetEffectiveDoomVillageMaxDistBase(int rawNCells)
        {
            var profile = _activeProfile;
            if (profile != null && profile.IsActive && rawNCells > profile.TargetNCells)
            {
                return (int)Math.Round(profile.TargetNCells * (68.0 / 256.0));
            }
            return (rawNCells / 2) - 60;
        }

        /// <summary>
        /// Replaces vanilla <c>nCells / 4</c> for raven/relic ring placement when backing grid
        /// is promoted to 512 for 280/304 target sizes.
        /// </summary>
        public static int GetEffectiveQuarterNCells(int rawNCells)
        {
            var profile = _activeProfile;
            if (profile != null && profile.IsActive && rawNCells > profile.TargetNCells)
            {
                return (int)Math.Round(profile.TargetNCells / 4.0);
            }
            return rawNCells / 4;
        }

        private static void RestoreThemes()
        {
            var snaps = _themeSnapshots;
            _themeSnapshots = null;
            if (snaps == null) return;

            foreach (var snap in snaps)
            {
                try
                {
                    GameBridge.RestoreMapThemeSnapshot(snap);
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[CoopBalance] Failed restoring ZXMapTheme snapshot: {ex.Message}");
                }
            }
        }

        private static int ScaleLevelEvents(IList levelEvents, object randomLevelParams, SurvivalBalanceProfile profile)
        {
            if (levelEvents == null || levelEvents.Count == 0) return 0;

            float durationFactor = GameBridge.GetRandomParamsFactorGameDuration(randomLevelParams);
            if (durationFactor <= 0f) durationFactor = 1f;
            int earlyCutoffH = (int)Math.Round(35f * 24f * durationFactor);
            bool isMulti = IsMultiFactionGenerationActive;
            int earlyMfCutoffH = (int)Math.Round(28f * 24f * durationFactor);
            int midMfCutoffH = (int)Math.Round(55f * 24f * durationFactor);

            int count = 0;
            foreach (object ev in levelEvents)
            {
                if (ev == null) continue;
                GameBridge.GetLevelEventTiming(ev, out int startTimeH, out int gameTimeH, out bool isFinalSwarm);
                int refTimeH = startTimeH > 0 ? startTimeH : gameTimeH;
                float mult;
                if (isMulti)
                {
                    if (isFinalSwarm)
                    {
                        mult = profile.PlayerCount >= 3 ? 2.70f : 2.10f;
                    }
                    else if (refTimeH <= earlyMfCutoffH)
                    {
                        mult = profile.PlayerCount >= 3 ? 0.58f : 0.68f;
                    }
                    else if (refTimeH <= midMfCutoffH)
                    {
                        mult = profile.PlayerCount >= 3 ? 0.82f : 0.92f;
                    }
                    else
                    {
                        mult = profile.PlayerCount >= 3 ? 0.98f : 1.08f;
                    }
                }
                else
                {
                    mult = (!isFinalSwarm && refTimeH <= earlyCutoffH) ? profile.EarlySwarmScale : profile.LateSwarmScale;
                }
                GameBridge.ScaleLevelEventPopulation(ev, mult, 100);
                count++;
            }
            return count;
        }

        private static int SpawnExtraStartingUnits(
            List<DXEntity> extraEntities,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gridNCells,
            int extraRangers,
            int extraSoldiers)
        {
            if (extraEntities == null || (extraRangers <= 0 && extraSoldiers <= 0)) return 0;

            DXEntity commandCenter = null;
            DXEntityTemplate rangerTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeRanger);
            DXEntityTemplate soldierTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeSoldierRegular);

            var occupiedCells = new HashSet<long>();
            foreach (var ent in extraEntities)
            {
                if (ent == null) continue;
                if (commandCenter == null && GameBridge.TypeCommandCenter != null && GameBridge.TypeCommandCenter.IsInstanceOfType(ent))
                    commandCenter = ent;
                if (rangerTemplate == null && GameBridge.TypeRanger != null && GameBridge.TypeRanger.IsInstanceOfType(ent))
                    rangerTemplate = ent.Template;
                if (soldierTemplate == null && GameBridge.TypeSoldierRegular != null && GameBridge.TypeSoldierRegular.IsInstanceOfType(ent))
                    soldierTemplate = ent.Template;

                var area = ent.CellArea;
                if (area.Width > 0 && area.Height > 0)
                {
                    for (int x = area.Left; x < area.Right; x++)
                        for (int y = area.Top; y < area.Bottom; y++)
                            occupiedCells.Add(PackCell(x, y));
                }
                else
                {
                    occupiedCells.Add(PackCell(ent.CellX, ent.CellY));
                }
            }

            if (commandCenter == null)
            {
                ModLogger.Warn("[CoopBalance] CommandCenter not found in ExtraEntities; skipping extra starting units.");
                return 0;
            }

            Point ccCell = commandCenter.Cell;
            // Disperse extra squads across upper-left / upper-right / lower-left quadrants around CommandCenter
            // so players can quickly grab independent patrol groups without clumping onto vanilla bottom-right units.
            var anchorOffsets = new[]
            {
                new Point(-3, -3),
                new Point(3, -3),
                new Point(-3, 3),
                new Point(0, -4),
                new Point(-4, 0)
            };

            int spawned = 0;
            int anchorIdx = 0;

            for (int i = 0; i < extraRangers && rangerTemplate != null; i++)
            {
                Point? cell = FindFreeWalkableCellNear(
                    ccCell.X + anchorOffsets[anchorIdx % anchorOffsets.Length].X,
                    ccCell.Y + anchorOffsets[anchorIdx % anchorOffsets.Length].Y,
                    layerTerrain,
                    layerObjects,
                    gridNCells,
                    occupiedCells);
                anchorIdx++;
                if (cell == null) continue;

                DXEntity unit = rangerTemplate.CreateInstance();
                unit.Cell = cell.Value;
                extraEntities.Add(unit);
                occupiedCells.Add(PackCell(cell.Value.X, cell.Value.Y));
                spawned++;
            }

            for (int i = 0; i < extraSoldiers && soldierTemplate != null; i++)
            {
                Point? cell = FindFreeWalkableCellNear(
                    ccCell.X + anchorOffsets[anchorIdx % anchorOffsets.Length].X,
                    ccCell.Y + anchorOffsets[anchorIdx % anchorOffsets.Length].Y,
                    layerTerrain,
                    layerObjects,
                    gridNCells,
                    occupiedCells);
                anchorIdx++;
                if (cell == null) continue;

                DXEntity unit = soldierTemplate.CreateInstance();
                unit.Cell = cell.Value;
                extraEntities.Add(unit);
                occupiedCells.Add(PackCell(cell.Value.X, cell.Value.Y));
                spawned++;
            }

            return spawned;
        }

        private static Point? FindFreeWalkableCellNear(
            int targetX,
            int targetY,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gridNCells,
            HashSet<long> occupiedCells)
        {
            int maxCoord = gridNCells > 0 ? gridNCells - 2 : 510;
            for (int radius = 0; radius <= 10; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;

                        int cx = targetX + dx;
                        int cy = targetY + dy;
                        if (cx < 2 || cy < 2 || cx > maxCoord || cy > maxCoord) continue;
                        if (occupiedCells.Contains(PackCell(cx, cy))) continue;

                        if (layerTerrain != null)
                        {
                            var t = layerTerrain[cx, cy];
                            if (t != ZXMapLayerTerrainType.Earth && t != ZXMapLayerTerrainType.Grass)
                                continue;
                        }
                        if (layerObjects != null && layerObjects[cx, cy] != ZXMapLayerObjectType.None)
                            continue;

                        return new Point(cx, cy);
                    }
                }
            }
            return null;
        }

        private static long PackCell(int x, int y) => ((long)x << 32) | (uint)y;

        private static DXRange<int> ScaleRangeRound(DXRange<int> range, float factor)
        {
            int first = ScaleIntRound(range.First, factor);
            int last = Math.Max(first, ScaleIntRound(range.Last, factor));
            return new DXRange<int>(first, last);
        }

        private static int ScaleIntRound(int value, float factor)
        {
            if (value <= 0) return value;
            return Math.Max(1, (int)Math.Round(value * factor));
        }

        private static DXRange<int> ScaleRangeCeilWithFloor(DXRange<int> range, float factor, int minFloor)
        {
            int scaledFirst = Math.Max(minFloor, (int)Math.Ceiling(range.First * factor));
            int scaledLast = Math.Max(scaledFirst, (int)Math.Ceiling(range.Last * factor));
            return new DXRange<int>(scaledFirst, scaledLast);
        }
    }
}
