using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using DXVision;
using TABCoopMod.Core;
using ZX;

namespace TABCoopMod.Sync
{
    /// <summary>
    /// Phase 4 M4 deterministic post-generation pipeline for Multi-Faction New Survival maps.
    /// Operates directly on ZXMapDrawer's 512x512 enum layers and ExtraEntities right after
    /// vanilla MapGenerator returns DXLevel (before ZXMapDrawer materializes entities),
    /// implementing Clustered/Separated spawn modes, A* base corridors, scaled Doom Villages,
    /// 3-ring zombie compression, seed-driven elite tendencies, and condensed outer Giants.
    /// </summary>
    public static class MultiBaseMapGenerator
    {
        private struct NestCluster
        {
            public List<DXEntity> Entities;
            public Point Centroid;
            public int Radius;
        }

        private struct AStarNode
        {
            public int X;
            public int Y;
            public int F;
        }

        public static void PostProcessGeneratedLevel(
            DXLevel generatedLevel,
            object randomLevelParams,
            SurvivalBalanceProfile profile,
            MultiFactionSpawnMode spawnMode)
        {
            if (generatedLevel == null || profile == null || profile.PlayerCount < 2)
                return;

            if (!GameBridge.TryGetGeneratedLevelContext(
                    generatedLevel,
                    out object mapDrawer,
                    out List<DXEntity> extraEntities,
                    out DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
                    out DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
                    out int gN,
                    out IList _))
            {
                ModLogger.Warn("[MultiBaseMapGen] Missing MapDrawer context; skipping M4 post-processing.");
                return;
            }

            var layerZombies = GameBridge.GetMapDrawerZombieLayer(mapDrawer);
            if (extraEntities == null || layerTerrain == null || layerObjects == null || layerZombies == null || gN <= 0)
            {
                ModLogger.Warn("[MultiBaseMapGen] MapDrawer layers unavailable; skipping M4 post-processing.");
                return;
            }

            int mapSeed = GetSeedFromParams(randomLevelParams);
            float factorPop = GetPopFromParams(randomLevelParams);
            ulong rngState = Mix64((ulong)(uint)mapSeed ^ 0x4D345F47454E5F31UL);

            MutationTendency tendency = PickMutationTendency(ref rngState);
            FactionOwnershipTracker.SetGeneratedMapMetadata(spawnMode, tendency, mapSeed);

            RectangleF playable = generatedLevel.PlayableArea;
            int playerCount = Math.Max(2, Math.Min(3, profile.PlayerCount));

            // 1. Collect original CC1, starter units, Doom Village clusters, Giants/Mutants, Oil/Pickables.
            DXEntity cc1 = null;
            var starterRangers = new List<DXEntity>();
            var starterSoldiers = new List<DXEntity>();
            var nestEntities = new List<DXEntity>();
            var existingBosses = new List<DXEntity>();
            var propEntities = new List<DXEntity>();

            for (int i = 0; i < extraEntities.Count; i++)
            {
                var e = extraEntities[i];
                if (e == null) continue;
                if (cc1 == null && GameBridge.IsCommandCenter(e))
                    cc1 = e;
                else if (GameBridge.TypeRanger != null && GameBridge.TypeRanger.IsInstanceOfType(e))
                    starterRangers.Add(e);
                else if (GameBridge.TypeSoldierRegular != null && GameBridge.TypeSoldierRegular.IsInstanceOfType(e))
                    starterSoldiers.Add(e);
                else if (GameBridge.IsInfectionNestEntity(e))
                    nestEntities.Add(e);
                else if ((GameBridge.TypeZombieGiant != null && GameBridge.TypeZombieGiant.IsInstanceOfType(e)) ||
                         (GameBridge.TypeZombieMutant != null && GameBridge.TypeZombieMutant.IsInstanceOfType(e)))
                    existingBosses.Add(e);
                else if (e.GetType().Name == "OilSource" || (GameBridge.TypePickableItem != null && GameBridge.TypePickableItem.IsInstanceOfType(e)))
                    propEntities.Add(e);
            }

            if (cc1 == null)
            {
                ModLogger.Warn("[MultiBaseMapGen] CC1 missing in ExtraEntities; aborting M4 post-processing.");
                return;
            }

            // 2. Build main walkable connected component mask around center.
            bool[,] mainWalkable = BuildMainWalkableMask(layerTerrain, layerObjects, gN, cc1.Cell);
            var blockedCells = BuildBlockedPropCells(nestEntities, propEntities, gN);

            // 3. Determine target CC spots for Clustered or Separated mode.
            List<Point> chosenSpots = spawnMode == MultiFactionSpawnMode.Clustered
                ? PickClusteredBaseSpots(cc1.Cell, mainWalkable, blockedCells, layerTerrain, layerObjects, playable, gN, playerCount, ref rngState)
                : PickSeparatedBaseSpots(cc1.Cell, mainWalkable, blockedCells, nestEntities, layerTerrain, layerObjects, playable, gN, playerCount, ref rngState);

            // 4. Relocate CC1 + starter units and create CC2..CCN + their starter units.
            List<DXEntity> placedCcs = ApplyStarterBases(
                cc1,
                starterRangers,
                starterSoldiers,
                chosenSpots,
                extraEntities,
                layerTerrain,
                layerObjects,
                gN);

            // 5. Ensure walkable corridors between adjacent bases (A* carving only where blocked or severe detour).
            EnsureBaseCorridors(chosenSpots, layerTerrain, layerObjects, gN);

            // 6. Relocate Doom Villages & props out of all starter safe zones, and top up village count if needed.
            var clusters = ClusterInfectionNests(nestEntities);
            RelocateAndTopUpDoomVillages(
                clusters,
                chosenSpots,
                extraEntities,
                layerTerrain,
                layerObjects,
                playable,
                gN,
                playerCount,
                factorPop,
                spawnMode,
                ref rngState);
            RelocateBlockedProps(propEntities, chosenSpots, layerTerrain, layerObjects, playable, gN);

            // 7. Rewrite LayerZombies per-base using vanilla dynamic noise & ZXMapTheme distance thresholds,
            //    then apply BTK-weighted promotion + low/mid-tier filler replenishment.
            RewriteZombieRingsAndBosses(
                chosenSpots,
                nestEntities,
                extraEntities,
                existingBosses,
                layerTerrain,
                layerObjects,
                layerZombies,
                playable,
                gN,
                playerCount,
                mapSeed,
                factorPop,
                randomLevelParams,
                spawnMode,
                tendency,
                ref rngState);

            FactionOwnershipTracker.RegisterPendingCcOwners(placedCcs, CoopSessionManager.GetOccupiedFactionSlotsSorted());

            string ccSummary = string.Join(" ", chosenSpots.ConvertAll(p => $"({p.X},{p.Y})"));
            ModLogger.Info(
                $"[MultiBaseMapGen] M4 complete: players={playerCount}, NCells={profile.TargetNCells}, " +
                $"spawnMode={spawnMode}, tendency={tendency}, CCs=[{ccSummary}], doomClusters={clusters.Count}.");
        }

        private static MutationTendency PickMutationTendency(ref ulong rng)
        {
            int roll = NextInt(ref rng, 100);
            if (roll < 35) return MutationTendency.Balanced;
            if (roll < 60) return MutationTendency.HeavyArmor;
            if (roll < 80) return MutationTendency.Caustic;
            return MutationTendency.SwarmHunter;
        }

        private static bool[,] BuildMainWalkableMask(
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            Point seedCell)
        {
            var walkable = new bool[gN, gN];
            Point start = seedCell;
            if (!IsCellWalkable(layerTerrain, layerObjects, gN, start.X, start.Y))
            {
                bool found = false;
                for (int r = 1; r <= 24 && !found; r++)
                {
                    for (int dx = -r; dx <= r && !found; dx++)
                    {
                        for (int dy = -r; dy <= r && !found; dy++)
                        {
                            int tx = seedCell.X + dx;
                            int ty = seedCell.Y + dy;
                            if (IsCellWalkable(layerTerrain, layerObjects, gN, tx, ty))
                            {
                                start = new Point(tx, ty);
                                found = true;
                            }
                        }
                    }
                }
                if (!found) return walkable;
            }

            var q = new Queue<Point>(16384);
            walkable[start.X, start.Y] = true;
            q.Enqueue(start);

            while (q.Count > 0)
            {
                Point p = q.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nx = p.X + (d == 0 ? 1 : (d == 1 ? -1 : 0));
                    int ny = p.Y + (d == 2 ? 1 : (d == 3 ? -1 : 0));
                    if (nx <= 2 || ny <= 2 || nx >= gN - 2 || ny >= gN - 2 || walkable[nx, ny]) continue;
                    if (!IsCellWalkable(layerTerrain, layerObjects, gN, nx, ny)) continue;
                    walkable[nx, ny] = true;
                    q.Enqueue(new Point(nx, ny));
                }
            }
            return walkable;
        }

        private static HashSet<long> BuildBlockedPropCells(List<DXEntity> nests, List<DXEntity> props, int gN)
        {
            var blocked = new HashSet<long>();
            void MarkArea(DXEntity e, int pad)
            {
                if (e == null) return;
                var a = e.CellArea;
                for (int x = Math.Max(0, a.Left - pad); x < Math.Min(gN, a.Right + pad); x++)
                    for (int y = Math.Max(0, a.Top - pad); y < Math.Min(gN, a.Bottom + pad); y++)
                        blocked.Add(PackCell(x, y));
            }
            for (int i = 0; i < nests.Count; i++) MarkArea(nests[i], 6);
            for (int i = 0; i < props.Count; i++) MarkArea(props[i], 2);
            return blocked;
        }

        private static List<Point> PickClusteredBaseSpots(
            Point centerAnchor,
            bool[,] mainWalkable,
            HashSet<long> blockedCells,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN,
            int playerCount,
            ref ulong rng)
        {
            var spots = new List<Point>(playerCount);
            double theta0 = NextDouble(ref rng) * Math.PI * 2.0;
            double ringR = playerCount >= 3 ? 12.0 : 10.5;
            int[] radiiOffsets = { 0, 2, -2, 4, 6 };

            for (int p = 0; p < playerCount; p++)
            {
                double baseAng = theta0 + p * (2.0 * Math.PI / playerCount);
                Point best = default(Point);
                double bestScore = double.MinValue;

                for (int ri = 0; ri < radiiOffsets.Length; ri++)
                {
                    double r = ringR + radiiOffsets[ri];
                    for (int s = -6; s <= 6; s++)
                    {
                        double ang = baseAng + s * (Math.PI / 24.0);
                        int cx = centerAnchor.X + (int)Math.Round(Math.Cos(ang) * r);
                        int cy = centerAnchor.Y + (int)Math.Round(Math.Sin(ang) * r);
                        if (!IsWithinSafePlayable(cx, cy, playable, gN, 22f)) continue;
                        if (TooCloseToAny(cx, cy, spots, 16)) continue;
                        if (!IsPadSuitableForBase(cx, cy, layerTerrain, layerObjects, blockedCells, gN, allowTrees: true)) continue;

                        double sepPenalty = 0.0;
                        for (int k = 0; k < spots.Count; k++)
                        {
                            double d = Math.Sqrt(DistSq(cx, cy, spots[k].X, spots[k].Y));
                            if (d > 25.0) sepPenalty += (d - 25.0) * 5.0;
                        }
                        double score = (mainWalkable[cx, cy] ? 50.0 : 0.0) - Math.Abs(s) * 2.0 - Math.Abs(radiiOffsets[ri]) * 3.0 - sepPenalty;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Point(cx, cy);
                        }
                    }
                }

                if (best.X == 0 && best.Y == 0)
                {
                    // Emergency fallback near center avoiding existing pads.
                    for (int r = 16; r <= 30 && best.X == 0; r += 2)
                    {
                        for (int step = 0; step < 24 && best.X == 0; step++)
                        {
                            double ang = baseAng + step * (Math.PI / 12.0);
                            int cx = centerAnchor.X + (int)Math.Round(Math.Cos(ang) * r);
                            int cy = centerAnchor.Y + (int)Math.Round(Math.Sin(ang) * r);
                            if (cx < 20 || cy < 20 || cx >= gN - 20 || cy >= gN - 20) continue;
                            if (TooCloseToAny(cx, cy, spots, 14)) continue;
                            best = new Point(cx, cy);
                        }
                    }
                    if (best.X == 0) best = new Point(centerAnchor.X + p * 18, centerAnchor.Y);
                }

                spots.Add(best);
            }
            return spots;
        }

        private static List<Point> PickSeparatedBaseSpots(
            Point centerAnchor,
            bool[,] mainWalkable,
            HashSet<long> blockedCells,
            List<DXEntity> nestEntities,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN,
            int playerCount,
            ref ulong rng)
        {
            double theta0 = NextDouble(ref rng) * Math.PI * 2.0;
            double targetRing = playerCount >= 3 ? 50.0 : 44.0;
            int minCcDist = playerCount >= 3 ? 72 : 76;
            double[] rCandidates = { targetRing, targetRing - 4, targetRing + 4, targetRing - 8, targetRing + 8, targetRing - 12 };

            var sectorCandidates = new List<List<(Point Pos, double Score, int Stone24)>>(playerCount);
            var spots = new List<Point>(playerCount);

            for (int p = 0; p < playerCount; p++)
            {
                double sectorAngle = theta0 + p * (2.0 * Math.PI / playerCount);
                var list = new List<(Point Pos, double Score, int Stone24)>();

                for (int ri = 0; ri < rCandidates.Length; ri++)
                {
                    double r = rCandidates[ri];
                    for (int s = -5; s <= 5; s++)
                    {
                        double ang = sectorAngle + s * (Math.PI / 24.0);
                        int cx = centerAnchor.X + (int)Math.Round(Math.Cos(ang) * r);
                        int cy = centerAnchor.Y + (int)Math.Round(Math.Sin(ang) * r);
                        if (!IsWithinSafePlayable(cx, cy, playable, gN, 30f)) continue;
                        if (TooCloseToAny(cx, cy, spots, minCcDist)) continue;
                        if (!IsPadSuitableForBase(cx, cy, layerTerrain, layerObjects, blockedCells, gN, allowTrees: true)) continue;

                        EvaluateCandidateSpot(cx, cy, layerTerrain, layerObjects, gN, out int buildable16, out int trees16, out int grass16, out int stone24);
                        double walkBonus = mainWalkable[cx, cy] ? 120.0 : 0.0;
                        double nearestNestDist = MinDistanceToEntities(cx, cy, nestEntities);
                        double nestPenalty = nearestNestDist < 38.0 ? (38.0 - nearestNestDist) * 18.0 : 0.0;
                        double score = walkBonus + buildable16 * 0.4 + Math.Min(trees16, 120) * 1.8 + Math.Min(grass16, 300) * 0.25 + Math.Min(stone24, 24) * 6.5 - Math.Abs(s) * 3.0 - nestPenalty;
                        list.Add((new Point(cx, cy), score, stone24));
                    }
                }

                list.Sort((a, b) => b.Score.CompareTo(a.Score));
                sectorCandidates.Add(list);

                if (list.Count > 0)
                {
                    int topK = Math.Min(4, list.Count);
                    // Weighted pick from Top-K (40%, 30%, 20%, 10% slice) so starts stay natural rather than robotic.
                    int pickIdx = NextInt(ref rng, topK);
                    if (pickIdx > 0 && NextInt(ref rng, 100) < 40) pickIdx = 0;
                    spots.Add(list[pickIdx].Pos);
                }
                else
                {
                    // Fallback: relax Terrain check on Mountains/Water if an extreme AL/TM seed blocked the entire sector.
                    Point fallback = FindRelaxedSectorSpot(centerAnchor, sectorAngle, targetRing, spots, minCcDist, playable, gN, mainWalkable);
                    spots.Add(fallback);
                }
            }

            // Team-wide stone guarantee: ensure sum(stone24) >= 4 across all N bases so teammates can share/enclave mine.
            int totalStone = 0;
            int minStoneIdx = 0;
            int minStoneVal = int.MaxValue;
            for (int p = 0; p < spots.Count; p++)
            {
                EvaluateCandidateSpot(spots[p].X, spots[p].Y, layerTerrain, layerObjects, gN, out _, out _, out _, out int st);
                totalStone += st;
                if (st < minStoneVal)
                {
                    minStoneVal = st;
                    minStoneIdx = p;
                }
            }
            if (totalStone < 4)
            {
                int bestBoost = -1;
                Point bestSwap = default(Point);
                int swapPlayer = minStoneIdx;
                for (int p = 0; p < sectorCandidates.Count; p++)
                {
                    var list = sectorCandidates[p];
                    for (int i = 0; i < list.Count; i++)
                    {
                        var cand = list[i];
                        bool clash = false;
                        for (int k = 0; k < spots.Count; k++)
                        {
                            if (k != p && DistSq(cand.Pos.X, cand.Pos.Y, spots[k].X, spots[k].Y) < (long)minCcDist * minCcDist)
                            {
                                clash = true;
                                break;
                            }
                        }
                        if (!clash && cand.Stone24 > bestBoost)
                        {
                            bestBoost = cand.Stone24;
                            bestSwap = cand.Pos;
                            swapPlayer = p;
                        }
                    }
                }
                if (bestBoost >= 4)
                    spots[swapPlayer] = bestSwap;
            }

            return spots;
        }

        private static Point FindRelaxedSectorSpot(
            Point anchor,
            double sectorAngle,
            double targetRing,
            List<Point> existing,
            int minCcDist,
            RectangleF playable,
            int gN,
            bool[,] mainWalkable)
        {
            Point best = default(Point);
            int bestScore = int.MinValue;
            for (double r = targetRing - 10; r <= targetRing + 14; r += 3.0)
            {
                for (int s = -8; s <= 8; s++)
                {
                    double ang = sectorAngle + s * (Math.PI / 24.0);
                    int cx = anchor.X + (int)Math.Round(Math.Cos(ang) * r);
                    int cy = anchor.Y + (int)Math.Round(Math.Sin(ang) * r);
                    if (!IsWithinSafePlayable(cx, cy, playable, gN, 26f)) continue;
                    if (TooCloseToAny(cx, cy, existing, minCcDist - 12)) continue;
                    int sc = (mainWalkable[cx, cy] ? 100 : 0) - Math.Abs(s) * 4;
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        best = new Point(cx, cy);
                    }
                }
            }
            if (best.X == 0 && best.Y == 0)
            {
                int cx = Math.Max(40, Math.Min(gN - 40, anchor.X + (int)Math.Round(Math.Cos(sectorAngle) * (targetRing * 0.8))));
                int cy = Math.Max(40, Math.Min(gN - 40, anchor.Y + (int)Math.Round(Math.Sin(sectorAngle) * (targetRing * 0.8))));
                best = new Point(cx, cy);
            }
            return best;
        }

        private static List<DXEntity> ApplyStarterBases(
            DXEntity cc1,
            List<DXEntity> starterRangers,
            List<DXEntity> starterSoldiers,
            List<Point> spots,
            List<DXEntity> extraEntities,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            var ccTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeCommandCenter);
            var rangerTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeRanger);
            var soldierTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeSoldierRegular);

            var placedCcs = new List<DXEntity>(spots.Count);
            var offsets = new[]
            {
                new Point(-3, 3), new Point(3, 3), new Point(-3, -3), new Point(3, -3), new Point(0, 4)
            };

            for (int p = 0; p < spots.Count; p++)
            {
                Point ccCell = spots[p];
                PrepareBasePad(ccCell, layerTerrain, layerObjects, gN);

                DXEntity cc;
                if (p == 0)
                {
                    cc = cc1;
                    cc.Cell = ccCell;
                }
                else
                {
                    cc = ccTemplate.CreateInstance();
                    cc.Cell = ccCell;
                    GameBridge.SetEntityTeamAndRelation(cc, ZXTeamType.Player, ZXRelationshipType.Friend);
                    extraEntities.Add(cc);
                }
                placedCcs.Add(cc);

                var occupied = new HashSet<long>();
                var area = cc.CellArea;
                for (int x = area.Left; x < area.Right; x++)
                    for (int y = area.Top; y < area.Bottom; y++)
                        occupied.Add(PackCell(x, y));

                if (p == 0)
                {
                    for (int r = 0; r < starterRangers.Count; r++)
                    {
                        Point offset = offsets[r % offsets.Length];
                        Point? uCell = FindFreeWalkableCellNear(ccCell.X + offset.X, ccCell.Y + offset.Y, layerTerrain, layerObjects, gN, occupied);
                        if (uCell.HasValue)
                        {
                            starterRangers[r].Cell = uCell.Value;
                            occupied.Add(PackCell(uCell.Value.X, uCell.Value.Y));
                        }
                    }
                    for (int s = 0; s < starterSoldiers.Count; s++)
                    {
                        Point offset = offsets[(4 + s) % offsets.Length];
                        Point? uCell = FindFreeWalkableCellNear(ccCell.X + offset.X, ccCell.Y + offset.Y, layerTerrain, layerObjects, gN, occupied);
                        if (uCell.HasValue)
                        {
                            starterSoldiers[s].Cell = uCell.Value;
                            occupied.Add(PackCell(uCell.Value.X, uCell.Value.Y));
                        }
                    }
                }
                else
                {
                    for (int r = 0; r < 4 && rangerTemplate != null; r++)
                    {
                        Point? uCell = FindFreeWalkableCellNear(ccCell.X + offsets[r].X, ccCell.Y + offsets[r].Y, layerTerrain, layerObjects, gN, occupied);
                        if (!uCell.HasValue) continue;
                        DXEntity unit = rangerTemplate.CreateInstance();
                        unit.Cell = uCell.Value;
                        GameBridge.SetEntityTeamAndRelation(unit, ZXTeamType.Player, ZXRelationshipType.Friend);
                        extraEntities.Add(unit);
                        occupied.Add(PackCell(uCell.Value.X, uCell.Value.Y));
                    }
                    if (soldierTemplate != null)
                    {
                        Point? sCell = FindFreeWalkableCellNear(ccCell.X + offsets[4].X, ccCell.Y + offsets[4].Y, layerTerrain, layerObjects, gN, occupied);
                        if (sCell.HasValue)
                        {
                            DXEntity soldier = soldierTemplate.CreateInstance();
                            soldier.Cell = sCell.Value;
                            GameBridge.SetEntityTeamAndRelation(soldier, ZXTeamType.Player, ZXRelationshipType.Friend);
                            extraEntities.Add(soldier);
                        }
                    }
                }
            }

            return placedCcs;
        }

        private static void PrepareBasePad(
            Point ccCell,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            for (int x = ccCell.X - 3; x <= ccCell.X + 3; x++)
            {
                for (int y = ccCell.Y - 3; y <= ccCell.Y + 3; y++)
                {
                    if (x < 2 || y < 2 || x >= gN - 2 || y >= gN - 2) continue;
                    var t = layerTerrain[x, y];
                    if (t != ZXMapLayerTerrainType.Earth && t != ZXMapLayerTerrainType.Grass)
                        layerTerrain[x, y] = ZXMapLayerTerrainType.Earth;
                    layerObjects[x, y] = ZXMapLayerObjectType.None;
                }
            }
        }

        private static void EnsureBaseCorridors(
            List<Point> spots,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            if (spots.Count <= 1) return;

            for (int i = 0; i < spots.Count; i++)
            {
                int j = (i + 1) % spots.Count;
                if (i == 1 && spots.Count == 2) break; // Only 1 pair for 2P
                Point a = spots[i];
                Point b = spots[j];
                double straight = Math.Sqrt(DistSq(a.X, a.Y, b.X, b.Y));
                int walkSteps = ComputeShortestWalkableDistance(a, b, layerTerrain, layerObjects, gN, (int)(straight * 2.6));
                if (walkSteps >= 0) continue;

                List<Point> path = FindWeightedCorridorPath(a, b, layerTerrain, layerObjects, gN);
                if (path != null && path.Count > 0)
                {
                    CarveCorridorPath(path, layerTerrain, layerObjects, gN);
                    ModLogger.Info($"[MultiBaseMapGen] Carved {path.Count}-cell A* corridor connecting ({a.X},{a.Y}) <-> ({b.X},{b.Y}).");
                }
            }
        }

        private static int ComputeShortestWalkableDistance(
            Point start,
            Point goal,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            int maxDistLimit)
        {
            var dist = new int[gN, gN];
            var q = new Queue<Point>(4096);
            dist[start.X, start.Y] = 1;
            q.Enqueue(start);

            while (q.Count > 0)
            {
                Point p = q.Dequeue();
                int curD = dist[p.X, p.Y];
                if (Math.Abs(p.X - goal.X) <= 3 && Math.Abs(p.Y - goal.Y) <= 3)
                    return curD - 1;
                if (curD > maxDistLimit)
                    continue;

                for (int d = 0; d < 4; d++)
                {
                    int nx = p.X + (d == 0 ? 1 : (d == 1 ? -1 : 0));
                    int ny = p.Y + (d == 2 ? 1 : (d == 3 ? -1 : 0));
                    if (nx <= 2 || ny <= 2 || nx >= gN - 2 || ny >= gN - 2 || dist[nx, ny] != 0) continue;
                    if (!IsCellWalkable(layerTerrain, layerObjects, gN, nx, ny)) continue;
                    dist[nx, ny] = curD + 1;
                    q.Enqueue(new Point(nx, ny));
                }
            }
            return -1;
        }

        private static List<Point> FindWeightedCorridorPath(
            Point start,
            Point goal,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            var gScore = new Dictionary<long, int>(4096);
            var cameFrom = new Dictionary<long, Point>(4096);
            var closed = new HashSet<long>();
            var open = new List<AStarNode>(1024);

            long startKey = PackCell(start.X, start.Y);
            gScore[startKey] = 0;
            open.Add(new AStarNode { X = start.X, Y = start.Y, F = Math.Abs(goal.X - start.X) + Math.Abs(goal.Y - start.Y) });

            int iters = 0;
            while (open.Count > 0 && iters++ < 60000)
            {
                int bestIdx = 0;
                int bestF = open[0].F;
                for (int i = 1; i < open.Count; i++)
                {
                    if (open[i].F < bestF)
                    {
                        bestF = open[i].F;
                        bestIdx = i;
                    }
                }
                AStarNode cur = open[bestIdx];
                open[bestIdx] = open[open.Count - 1];
                open.RemoveAt(open.Count - 1);

                long curKey = PackCell(cur.X, cur.Y);
                if (!closed.Add(curKey)) continue;

                if (Math.Abs(cur.X - goal.X) <= 2 && Math.Abs(cur.Y - goal.Y) <= 2)
                {
                    var path = new List<Point>();
                    Point trace = new Point(cur.X, cur.Y);
                    path.Add(trace);
                    while (cameFrom.TryGetValue(PackCell(trace.X, trace.Y), out Point prev))
                    {
                        trace = prev;
                        path.Add(trace);
                    }
                    path.Reverse();
                    return path;
                }

                int curG = gScore[curKey];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cur.X + (d == 0 ? 1 : (d == 1 ? -1 : 0));
                    int ny = cur.Y + (d == 2 ? 1 : (d == 3 ? -1 : 0));
                    if (nx <= 10 || ny <= 10 || nx >= gN - 10 || ny >= gN - 10) continue;
                    long nKey = PackCell(nx, ny);
                    if (closed.Contains(nKey)) continue;

                    int stepCost = 1;
                    var tr = layerTerrain[nx, ny];
                    var ob = layerObjects[nx, ny];
                    if (tr != ZXMapLayerTerrainType.Earth && tr != ZXMapLayerTerrainType.Grass)
                        stepCost = 18;
                    else if (ob == ZXMapLayerObjectType.Mountain)
                        stepCost = 16;
                    else if (ob == ZXMapLayerObjectType.Tree)
                        stepCost = 4;
                    else if (ob != ZXMapLayerObjectType.None)
                        stepCost = 10;

                    int nextG = curG + stepCost;
                    if (!gScore.TryGetValue(nKey, out int oldG) || nextG < oldG)
                    {
                        gScore[nKey] = nextG;
                        cameFrom[nKey] = new Point(cur.X, cur.Y);
                        int h = Math.Abs(goal.X - nx) + Math.Abs(goal.Y - ny);
                        open.Add(new AStarNode { X = nx, Y = ny, F = nextG + h });
                    }
                }
            }
            return null;
        }

        private static void CarveCorridorPath(
            List<Point> path,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            for (int i = 0; i < path.Count; i++)
            {
                Point p = path[i];
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        if (dx * dx + dy * dy > 5) continue;
                        int x = p.X + dx;
                        int y = p.Y + dy;
                        if (x <= 2 || y <= 2 || x >= gN - 2 || y >= gN - 2) continue;
                        var tr = layerTerrain[x, y];
                        var ob = layerObjects[x, y];
                        if (tr != ZXMapLayerTerrainType.Earth && tr != ZXMapLayerTerrainType.Grass)
                            layerTerrain[x, y] = ZXMapLayerTerrainType.Earth;
                        if (ob == ZXMapLayerObjectType.Mountain || ob == ZXMapLayerObjectType.Tree)
                            layerObjects[x, y] = ZXMapLayerObjectType.None;
                    }
                }
            }
        }

        private static List<NestCluster> ClusterInfectionNests(List<DXEntity> nests)
        {
            var clusters = new List<NestCluster>();
            if (nests == null || nests.Count == 0) return clusters;

            var visited = new bool[nests.Count];
            for (int i = 0; i < nests.Count; i++)
            {
                if (visited[i]) continue;
                visited[i] = true;
                var group = new List<DXEntity> { nests[i] };
                var q = new Queue<int>();
                q.Enqueue(i);

                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    Point cp = nests[cur].Cell;
                    for (int j = 0; j < nests.Count; j++)
                    {
                        if (visited[j]) continue;
                        if (DistSq(cp.X, cp.Y, nests[j].CellX, nests[j].CellY) <= 12L * 12L &&
                            DistSq(nests[i].CellX, nests[i].CellY, nests[j].CellX, nests[j].CellY) <= 28L * 28L)
                        {
                            visited[j] = true;
                            group.Add(nests[j]);
                            q.Enqueue(j);
                        }
                    }
                }

                long sumX = 0, sumY = 0;
                for (int k = 0; k < group.Count; k++)
                {
                    sumX += group[k].CellX;
                    sumY += group[k].CellY;
                }
                var centroid = new Point((int)(sumX / group.Count), (int)(sumY / group.Count));
                int maxR = 6;
                for (int k = 0; k < group.Count; k++)
                {
                    int d = (int)Math.Ceiling(Math.Sqrt(DistSq(centroid.X, centroid.Y, group[k].CellX, group[k].CellY)));
                    if (d + 3 > maxR) maxR = d + 3;
                }
                clusters.Add(new NestCluster
                {
                    Entities = group,
                    Centroid = centroid,
                    Radius = Math.Min(32, maxR)
                });
            }
            return clusters;
        }

        private static void RelocateAndTopUpDoomVillages(
            List<NestCluster> clusters,
            List<Point> baseSpots,
            List<DXEntity> extraEntities,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN,
            int playerCount,
            float factorPop,
            MultiFactionSpawnMode spawnMode,
            ref ulong rng)
        {
            var placedCentroids = new List<Point>();

            // First pass: keep safe clusters (both centroid clearance and nearest building edge >= 34) and register their centroids; collect unsafe ones.
            var unsafeIdx = new List<int>();
            for (int i = 0; i < clusters.Count; i++)
            {
                var cl = clusters[i];
                double dMin = MinDistanceToBases(cl.Centroid.X, cl.Centroid.Y, baseSpots);
                double edgeMin = MinEntityDistanceToBases(cl.Entities, baseSpots);
                bool insideBounds = IsWithinSafePlayable(cl.Centroid.X, cl.Centroid.Y, playable, gN, 18f);
                if (dMin >= 28 + cl.Radius && edgeMin >= 34.0 && insideBounds)
                    placedCentroids.Add(cl.Centroid);
                else
                    unsafeIdx.Add(i);
            }

            // Relocate unsafe clusters to clean outer-mid spots away from all starter CCs.
            for (int u = 0; u < unsafeIdx.Count; u++)
            {
                var cl = clusters[unsafeIdx[u]];
                Point? newSpot = FindDoomVillageSpot(baseSpots, placedCentroids, layerTerrain, layerObjects, playable, gN, cl.Radius, ref rng);
                if (!newSpot.HasValue)
                {
                    for (int k = 0; k < cl.Entities.Count; k++)
                        extraEntities.Remove(cl.Entities[k]);
                    cl.Entities.Clear();
                    continue;
                }

                int dx = newSpot.Value.X - cl.Centroid.X;
                int dy = newSpot.Value.Y - cl.Centroid.Y;
                ClearCircleTerrainForVillage(newSpot.Value, cl.Radius, layerTerrain, layerObjects, gN);
                for (int k = 0; k < cl.Entities.Count; k++)
                {
                    var b = cl.Entities[k];
                    int nx = Math.Max(8, Math.Min(gN - 8, b.CellX + dx));
                    int ny = Math.Max(8, Math.Min(gN - 8, b.CellY + dy));
                    b.Cell = new Point(nx, ny);
                }
                cl.Centroid = newSpot.Value;
                clusters[unsafeIdx[u]] = cl;
                placedCentroids.Add(newSpot.Value);
            }

            // Guarantee at least targetMinClusters across the map (2P >= 2, 3P >= 3).
            int targetMinClusters = playerCount >= 3 ? 3 : 2;
            if (factorPop < 0.5f) targetMinClusters = Math.Max(1, targetMinClusters - 1);

            int attempts = 0;
            while (placedCentroids.Count < targetMinClusters && attempts++ < 8)
            {
                Point? extraSpot = null;
                // In Separated mode, allow 35% chance for one village to act as a central rendezvous objective.
                if (spawnMode == MultiFactionSpawnMode.Separated && placedCentroids.Count == targetMinClusters - 1 && NextInt(ref rng, 100) < 35)
                {
                    Point center = new Point(gN / 2, gN / 2);
                    if (MinDistanceToBases(center.X, center.Y, baseSpots) >= 40 && !TooCloseToAny(center.X, center.Y, placedCentroids, 24))
                        extraSpot = center;
                }
                if (!extraSpot.HasValue)
                    extraSpot = FindDoomVillageSpot(baseSpots, placedCentroids, layerTerrain, layerObjects, playable, gN, 9, ref rng);

                if (!extraSpot.HasValue) break;

                var created = SpawnExtraDoomVillageCluster(extraSpot.Value, 8, extraEntities, layerTerrain, layerObjects, gN, factorPop, ref rng);
                if (created.Count > 0)
                {
                    clusters.Add(new NestCluster { Entities = created, Centroid = extraSpot.Value, Radius = 8 });
                    placedCentroids.Add(extraSpot.Value);
                }
            }

            // Final per-building safety trim: guarantee no individual infection nest building sits within < 32 cells of any starter CC.
            for (int i = 0; i < clusters.Count; i++)
            {
                var ents = clusters[i].Entities;
                if (ents == null) continue;
                for (int k = ents.Count - 1; k >= 0; k--)
                {
                    var b = ents[k];
                    if (b == null || MinDistanceToBases(b.CellX, b.CellY, baseSpots) < 32.0)
                    {
                        if (b != null) extraEntities.Remove(b);
                        ents.RemoveAt(k);
                    }
                }
            }
        }

        private static Point? FindDoomVillageSpot(
            List<Point> baseSpots,
            List<Point> existingVillages,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN,
            int radius,
            ref ulong rng)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int triesLimit = pass == 0 ? 500 : 300;
                double minClear = (pass == 0 ? 32 : 28) + radius;
                double maxBaseDist = pass == 0 ? 115.0 : 120.0;
                int minVillageSep = pass == 0 ? 24 : 20;

                for (int tries = 0; tries < triesLimit; tries++)
                {
                    int x = 24 + NextInt(ref rng, Math.Max(1, gN - 48));
                    int y = 24 + NextInt(ref rng, Math.Max(1, gN - 48));
                    if (!IsWithinSafePlayable(x, y, playable, gN, 20f)) continue;
                    if (!IsCellWalkable(layerTerrain, layerObjects, gN, x, y)) continue;
                    double dMin = MinDistanceToBases(x, y, baseSpots);
                    if (dMin < minClear || dMin > maxBaseDist) continue;
                    if (TooCloseToAny(x, y, existingVillages, minVillageSep)) continue;
                    return new Point(x, y);
                }
            }
            return null;
        }

        private static void ClearCircleTerrainForVillage(
            Point center,
            int radius,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN)
        {
            int r2 = radius * radius;
            for (int x = Math.Max(4, center.X - radius); x <= Math.Min(gN - 4, center.X + radius); x++)
            {
                int dx = x - center.X;
                for (int y = Math.Max(4, center.Y - radius); y <= Math.Min(gN - 4, center.Y + radius); y++)
                {
                    int dy = y - center.Y;
                    if (dx * dx + dy * dy > r2) continue;
                    var tr = layerTerrain[x, y];
                    if (tr != ZXMapLayerTerrainType.Earth && tr != ZXMapLayerTerrainType.Grass)
                        layerTerrain[x, y] = ZXMapLayerTerrainType.Earth;
                    var ob = layerObjects[x, y];
                    if (ob != ZXMapLayerObjectType.MineralGold && ob != ZXMapLayerObjectType.MineralIron && ob != ZXMapLayerObjectType.MineralStone)
                        layerObjects[x, y] = ZXMapLayerObjectType.None;
                }
            }
        }

        private static List<DXEntity> SpawnExtraDoomVillageCluster(
            Point center,
            int radius,
            List<DXEntity> extraEntities,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            float factorPop,
            ref ulong rng)
        {
            var created = new List<DXEntity>();
            var tLarge = GameBridge.GetDoomNestTemplate(2);
            var tMed = GameBridge.GetDoomNestTemplate(1);
            var tSmall = GameBridge.GetDoomNestTemplate(0);
            if (tMed == null && tSmall == null) return created;

            ClearCircleTerrainForVillage(center, radius, layerTerrain, layerObjects, gN);

            var occupied = new HashSet<long>();
            DXEntity core = (tLarge ?? tMed).CreateInstance();
            core.Cell = center;
            GameBridge.SetInfectionNestFactor(core, factorPop);
            extraEntities.Add(core);
            created.Add(core);
            MarkAreaOccupied(core.CellArea, occupied);

            int extraHouses = 5 + NextInt(ref rng, 5);
            for (int h = 0; h < extraHouses; h++)
            {
                int ox = center.X + NextInt(ref rng, radius * 2 - 2) - (radius - 1);
                int oy = center.Y + NextInt(ref rng, radius * 2 - 2) - (radius - 1);
                if (DistSq(ox, oy, center.X, center.Y) > (long)radius * radius) continue;

                DXEntityTemplate tmpl = (h % 3 == 0 && tMed != null) ? tMed : (tSmall ?? tMed);
                if (tmpl == null) continue;
                int sz = tmpl == tMed ? 5 : 4;
                if (!IsAreaFree(ox - sz / 2, oy - sz / 2, sz, sz, occupied, gN)) continue;

                DXEntity house = tmpl.CreateInstance();
                house.Cell = new Point(ox, oy);
                GameBridge.SetInfectionNestFactor(house, factorPop);
                extraEntities.Add(house);
                created.Add(house);
                MarkAreaOccupied(house.CellArea, occupied);
            }
            return created;
        }

        private static void RelocateBlockedProps(
            List<DXEntity> props,
            List<Point> baseSpots,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN)
        {
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (p == null) continue;
                if (MinDistanceToBases(p.CellX, p.CellY, baseSpots) >= 8.0) continue;

                for (int r = 10; r <= 28; r++)
                {
                    bool moved = false;
                    for (int dx = -r; dx <= r && !moved; dx += 2)
                    {
                        for (int dy = -r; dy <= r && !moved; dy += 2)
                        {
                            int nx = p.CellX + dx;
                            int ny = p.CellY + dy;
                            if (!IsWithinSafePlayable(nx, ny, playable, gN, 18f)) continue;
                            if (MinDistanceToBases(nx, ny, baseSpots) < 10.0) continue;
                            if (!IsCellWalkable(layerTerrain, layerObjects, gN, nx, ny)) continue;
                            if (layerTerrain[nx, ny] != ZXMapLayerTerrainType.Earth) continue;
                            p.Cell = new Point(nx, ny);
                            moved = true;
                        }
                    }
                    if (moved) break;
                }
            }
        }

        private static void RewriteZombieRingsAndBosses(
            List<Point> baseSpots,
            List<DXEntity> nestEntities,
            List<DXEntity> extraEntities,
            List<DXEntity> existingBosses,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            DXGridLayerEnum<ZXMapLayerZombies> layerZombies,
            RectangleF playable,
            int gN,
            int playerCount,
            int mapSeed,
            float factorPop,
            object randomLevelParams,
            MultiFactionSpawnMode spawnMode,
            MutationTendency tendency,
            ref ulong rng)
        {
            GetTendencyWeights(tendency, out int wChubby, out int wVenom, out int wHarpy, out float giantMult);
            GameBridge.GetMapThemeZombieProfile(
                randomLevelParams,
                out double infectedFactor,
                out double infectedDispersion,
                out int minWeak,
                out int minMed,
                out int minStrong,
                out int minPowerful);

            // Reconstruct vanilla dynamic noise layer (DXNoyseLayer) and apply the exact vanilla
            // soft -0.5 SubstractCircle independently to every starter base.
            var noiseLayer = new DXNoyseLayer(gN);
            var noiseRng = new DXRandom(unchecked(mapSeed ^ 0x5A4D4231));
            double z = noiseRng.NextDouble() * 1000.0;
            double ox = noiseRng.NextDouble() * 1000.0;
            double oy = noiseRng.NextDouble() * 1000.0;
            noiseLayer.FillWithNoyse(z, infectedDispersion, infectedDispersion, ox, oy);
            for (int b = 0; b < baseSpots.Count; b++)
                noiseLayer.SubstractCircle(baseSpots[b].X, baseSpots[b].Y, minWeak, 0.5);

            // Build Doom Village footprint exclusion mask so zombies never spawn inside village buildings.
            var villageMask = new bool[gN, gN];
            for (int i = 0; i < nestEntities.Count; i++)
            {
                var ne = nestEntities[i];
                if (ne == null) continue;
                Rectangle area = ne.CellArea;
                int x0 = Math.Max(0, area.Left - 1);
                int y0 = Math.Max(0, area.Top - 1);
                int x1 = Math.Min(gN - 1, area.Right + 1);
                int y1 = Math.Min(gN - 1, area.Bottom + 1);
                for (int vx = x0; vx <= x1; vx++)
                    for (int vy = y0; vy <= y1; vy++)
                        villageMask[vx, vy] = true;
            }

            double num43 = Math.Max(0.001, infectedFactor * factorPop);
            double spawnThreshold = 1.0 - num43;

            int effOuterStart = spawnMode == MultiFactionSpawnMode.Separated
                ? Math.Max(minMed + 8, minPowerful - 10)
                : minPowerful;

            int prePromotionCount = 0;
            double midPromoBudget = 0.0;
            double outerBtkBudget = 0.0;
            int t3Emitted = 0;
            int condensedGiants = 0;
            int maxCondensedGiants = (int)Math.Round((playerCount >= 3 ? 4.0 : 2.5) * giantMult);
            var fillerCandidates = new List<Point>(4096);

            // Pass 1 & 2: Apply per-base vanilla dynamic noise, then BTK-weighted mid/outer promotion.
            for (int x = 0; x < gN; x++)
            {
                for (int y = 0; y < gN; y++)
                {
                    if (!IsCellInPlayableArea(x, y, playable, gN))
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        continue;
                    }

                    var t = layerTerrain[x, y];
                    var o = layerObjects[x, y];
                    if ((t != ZXMapLayerTerrainType.Earth && t != ZXMapLayerTerrainType.Grass)
                        || o == ZXMapLayerObjectType.Mountain
                        || o == ZXMapLayerObjectType.Tree
                        || villageMask[x, y])
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        continue;
                    }

                    // Vanilla CC Enlarge(4) hard-clear (~6 cells Chebyshev from 5x5 CC center) per base.
                    bool inCcClear = false;
                    for (int b = 0; b < baseSpots.Count; b++)
                    {
                        if (Math.Abs(x - baseSpots[b].X) <= 6 && Math.Abs(y - baseSpots[b].Y) <= 6)
                        {
                            inCcClear = true;
                            break;
                        }
                    }
                    if (inCcClear)
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        continue;
                    }

                    double nv = noiseLayer[x, y];
                    if (nv < spawnThreshold)
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        continue;
                    }

                    int num49 = (int)Math.Round(4.0 * (nv - spawnThreshold) / num43);
                    double dMin = MinDistanceToBases(x, y, baseSpots);
                    ulong cellHash = Mix64(((ulong)(uint)x << 32) | (uint)y ^ (ulong)(uint)mapSeed);
                    int num51 = (int)(cellHash % 100UL);

                    ZXMapLayerZombies baseCell = ZXMapLayerZombies.None;
                    if (num51 >= 50 && dMin >= minMed)
                    {
                        if (num51 >= 60 && dMin >= effOuterStart)
                        {
                            baseCell = PickTendencyEliteEnum((int)((cellHash >> 12) % 100UL), num49 > 2, wChubby, wVenom, wHarpy);
                        }
                        else if (num51 >= 40 && dMin >= minStrong)
                        {
                            baseCell = num49 <= 1
                                ? ZXMapLayerZombies.Strong1
                                : (num49 == 2 ? ZXMapLayerZombies.Strong2 : ZXMapLayerZombies.Strong3);
                        }
                        else
                        {
                            baseCell = num49 <= 1
                                ? ZXMapLayerZombies.Medium1
                                : (num49 == 2 ? ZXMapLayerZombies.Medium2 : ZXMapLayerZombies.Medium3);
                        }
                    }
                    else if (num51 >= 30)
                    {
                        baseCell = num49 <= 1
                            ? ZXMapLayerZombies.Weak1
                            : (num49 == 2 ? ZXMapLayerZombies.Weak2 : ZXMapLayerZombies.Weak3);
                    }

                    prePromotionCount += GetLayerZombieCellCount(baseCell);

                    if (baseCell == ZXMapLayerZombies.None)
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        if (dMin >= minWeak)
                            fillerCandidates.Add(new Point(x, y));
                        continue;
                    }

                    // Near ring (< minMed): keep 100% pure vanilla dynamic noise untouched.
                    if (dMin < minMed)
                    {
                        layerZombies[x, y] = baseCell;
                        continue;
                    }

                    int cellCount = GetLayerZombieCellCount(baseCell);
                    bool isT1 = IsLayerZombieT1(baseCell);
                    bool isT2 = IsLayerZombieT2(baseCell);
                    int drawRoll = (int)((cellHash >> 4) % 100UL);

                    // Mid ring [minMed .. effOuterStart): promote 40% of T1 at 2.5:1 into Strong (70%) / Medium (30%).
                    if (dMin < effOuterStart)
                    {
                        if (isT1 && drawRoll < 40)
                        {
                            midPromoBudget += cellCount;
                            if (midPromoBudget >= 2.5)
                            {
                                midPromoBudget -= 2.5;
                                layerZombies[x, y] = ((cellHash >> 8) % 10UL) < 7UL
                                    ? ZXMapLayerZombies.Strong1
                                    : ZXMapLayerZombies.Medium1;
                            }
                            else
                            {
                                layerZombies[x, y] = ZXMapLayerZombies.None;
                                fillerCandidates.Add(new Point(x, y));
                            }
                        }
                        else
                        {
                            layerZombies[x, y] = baseCell;
                        }
                        continue;
                    }

                    // Outer ring (>= effOuterStart): BTK-weighted T3 promotion (Chubby=5.0, Venom=3.0, Harpy=3.0).
                    if (!isT1 && !isT2)
                    {
                        t3Emitted++;
                        if (dMin >= 85.0 && (t3Emitted % 28 == 27) && condensedGiants < maxCondensedGiants)
                        {
                            condensedGiants++;
                            layerZombies[x, y] = ZXMapLayerZombies.None;
                        }
                        else
                        {
                            layerZombies[x, y] = baseCell;
                        }
                        continue;
                    }

                    bool promoteOuter = (isT1 && drawRoll < 70) || (isT2 && drawRoll < 50);
                    if (!promoteOuter)
                    {
                        layerZombies[x, y] = baseCell;
                        continue;
                    }

                    // Pure BTK-weighted outer promotion on native 1.0x InfectedFactor:
                    // compress redundant T1/T2 counts into T3 elites without cutting native T3 spawns.
                    outerBtkBudget += isT1 ? cellCount : cellCount * 1.6;
                    ZXMapLayerZombies singleElite = PickTendencyEliteEnum((int)((cellHash >> 12) % 100UL), false, wChubby, wVenom, wHarpy);
                    double unitCost = (singleElite == ZXMapLayerZombies.Chubbies1) ? 5.0 : 3.0;

                    if (outerBtkBudget >= unitCost)
                    {
                        t3Emitted++;
                        bool twoUnits = num49 >= 2 && (t3Emitted % 2 == 0) && outerBtkBudget >= unitCost * 2.0;
                        outerBtkBudget -= twoUnits ? unitCost * 2.0 : unitCost;

                        if (dMin >= 85.0 && (t3Emitted % 28 == 27) && condensedGiants < maxCondensedGiants)
                        {
                            condensedGiants++;
                            layerZombies[x, y] = ZXMapLayerZombies.None;
                        }
                        else
                        {
                            layerZombies[x, y] = PickTendencyEliteEnum((int)((cellHash >> 12) % 100UL), twoUnits, wChubby, wVenom, wHarpy);
                        }
                    }
                    else
                    {
                        layerZombies[x, y] = ZXMapLayerZombies.None;
                        fillerCandidates.Add(new Point(x, y));
                    }
                }
            }

            // Pass 3: Ensure total entity count stays proportional to prePromotionCount (>=80%)
            // without exceeding the 25k 30Hz lockstep ceiling.
            int currentCount = 0;
            for (int x = 4; x < gN - 4; x++)
                for (int y = 4; y < gN - 4; y++)
                    currentCount += GetLayerZombieCellCount(layerZombies[x, y]);

            int targetTotal = Math.Min(25000, (int)Math.Round(prePromotionCount * 0.85));
            if (currentCount < targetTotal && fillerCandidates.Count > 0)
            {
                int need = targetTotal - currentCount;
                int stride = fillerCandidates.Count > need ? Math.Max(1, fillerCandidates.Count / Math.Max(1, need / 2 + 1)) : 1;
                int startIdx = NextInt(ref rng, fillerCandidates.Count);
                for (int step = 0; step < fillerCandidates.Count && currentCount < targetTotal; step++)
                {
                    int idx = (startIdx + step * stride) % fillerCandidates.Count;
                    Point pt = fillerCandidates[idx];
                    if (layerZombies[pt.X, pt.Y] != ZXMapLayerZombies.None) continue;

                    ulong fh = Mix64(((ulong)(uint)pt.X << 32) | (uint)pt.Y ^ (ulong)(uint)mapSeed ^ 0x46494C4CUL);
                    int r100 = (int)(fh % 100UL);
                    ZXMapLayerZombies fill = r100 < 50
                        ? ZXMapLayerZombies.Weak2
                        : (r100 < 75 ? ZXMapLayerZombies.Runners1 : ZXMapLayerZombies.Medium1);
                    layerZombies[pt.X, pt.Y] = fill;
                    currentCount += GetLayerZombieCellCount(fill);
                }
            }

            // Clamp existing Giants (>=70) and Mutants (>=64) away from all starter bases (matching vanilla).
            var placedBossSpots = new List<Point>();
            for (int i = 0; i < existingBosses.Count; i++)
            {
                var b = existingBosses[i];
                bool isMutant = GameBridge.TypeZombieMutant != null && GameBridge.TypeZombieMutant.IsInstanceOfType(b);
                double minReq = isMutant ? 64.0 : 70.0;
                if (MinDistanceToBases(b.CellX, b.CellY, baseSpots) < minReq)
                {
                    Point? spot = FindOuterBossSpot(baseSpots, placedBossSpots, layerTerrain, layerObjects, playable, gN, minReq, i % baseSpots.Count, ref rng);
                    if (spot.HasValue)
                    {
                        b.Cell = spot.Value;
                        layerZombies[spot.Value.X, spot.Value.Y] = ZXMapLayerZombies.None;
                    }
                }
                placedBossSpots.Add(b.Cell);
            }

            // Spawn condensed Giants evenly distributed across outer player sectors (d_min >= 85).
            var giantTemplate = GameBridge.GetEntityTemplateByType(GameBridge.TypeZombieGiant);
            if (giantTemplate != null && condensedGiants > 0)
            {
                for (int g = 0; g < condensedGiants; g++)
                {
                    int sectorIdx = g % baseSpots.Count;
                    Point? spot = FindOuterBossSpot(baseSpots, placedBossSpots, layerTerrain, layerObjects, playable, gN, 85.0, sectorIdx, ref rng);
                    if (!spot.HasValue) continue;

                    DXEntity giant = giantTemplate.CreateInstance();
                    giant.Cell = spot.Value;
                    GameBridge.SetZombieRoamingDistance(giant, 54);
                    GameBridge.AttachAutoMiniMapInfo(giant);
                    extraEntities.Add(giant);
                    placedBossSpots.Add(spot.Value);
                    layerZombies[spot.Value.X, spot.Value.Y] = ZXMapLayerZombies.None;
                }
            }
        }

        private static Point? FindOuterBossSpot(
            List<Point> baseSpots,
            List<Point> existingBosses,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            RectangleF playable,
            int gN,
            double minBaseDist,
            int preferredSectorIdx,
            ref ulong rng)
        {
            Point center = new Point(gN / 2, gN / 2);
            Point prefBase = baseSpots[preferredSectorIdx % baseSpots.Count];
            double prefAngle = Math.Atan2(prefBase.Y - center.Y, prefBase.X - center.X);

            for (int tries = 0; tries < 450; tries++)
            {
                int x, y;
                if (tries < 260)
                {
                    double ang = prefAngle + (NextDouble(ref rng) - 0.5) * 1.4;
                    double r = minBaseDist + NextDouble(ref rng) * 32.0;
                    x = prefBase.X + (int)Math.Round(Math.Cos(ang) * r);
                    y = prefBase.Y + (int)Math.Round(Math.Sin(ang) * r);
                }
                else
                {
                    x = 16 + NextInt(ref rng, Math.Max(1, gN - 32));
                    y = 16 + NextInt(ref rng, Math.Max(1, gN - 32));
                }

                if (!IsWithinSafePlayable(x, y, playable, gN, 14f)) continue;
                if (!IsCellWalkable(layerTerrain, layerObjects, gN, x, y)) continue;
                if (MinDistanceToBases(x, y, baseSpots) < minBaseDist) continue;
                if (TooCloseToAny(x, y, existingBosses, 24)) continue;
                return new Point(x, y);
            }
            return null;
        }

        private static void GetTendencyWeights(MutationTendency t, out int wChubby, out int wVenom, out int wHarpy, out float giantMult)
        {
            switch (t)
            {
                case MutationTendency.HeavyArmor:
                    wChubby = 70; wVenom = 15; wHarpy = 15; giantMult = 1.3f;
                    return;
                case MutationTendency.Caustic:
                    wChubby = 20; wVenom = 65; wHarpy = 15; giantMult = 1.0f;
                    return;
                case MutationTendency.SwarmHunter:
                    wChubby = 20; wVenom = 20; wHarpy = 60; giantMult = 1.0f;
                    return;
                default:
                    wChubby = 45; wVenom = 35; wHarpy = 20; giantMult = 1.0f;
                    return;
            }
        }

        private static ZXMapLayerZombies PickTendencyEliteEnum(int roll100, bool twoUnits, int wChubby, int wVenom, int wHarpy)
        {
            int total = Math.Max(1, wChubby + wVenom + wHarpy);
            int r = roll100 % total;
            if (r < wChubby)
                return twoUnits ? ZXMapLayerZombies.Chubbies2 : ZXMapLayerZombies.Chubbies1;
            if (r < wChubby + wVenom)
                return twoUnits ? ZXMapLayerZombies.Venoms2 : ZXMapLayerZombies.Venoms1;
            return twoUnits ? ZXMapLayerZombies.Harpies2 : ZXMapLayerZombies.Harpies1;
        }

        private static int GetLayerZombieCellCount(ZXMapLayerZombies v)
        {
            switch (v)
            {
                case ZXMapLayerZombies.Weak3:
                case ZXMapLayerZombies.Runners3:
                case ZXMapLayerZombies.Medium3:
                case ZXMapLayerZombies.Strong3:
                case ZXMapLayerZombies.Harpies3:
                case ZXMapLayerZombies.Venoms3:
                case ZXMapLayerZombies.Chubbies3:
                    return 3;
                case ZXMapLayerZombies.Weak2:
                case ZXMapLayerZombies.Runners2:
                case ZXMapLayerZombies.Medium2:
                case ZXMapLayerZombies.Strong2:
                case ZXMapLayerZombies.Powerful2:
                case ZXMapLayerZombies.Harpies2:
                case ZXMapLayerZombies.Venoms2:
                case ZXMapLayerZombies.Chubbies2:
                    return 2;
                default:
                    return v == ZXMapLayerZombies.None ? 0 : 1;
            }
        }

        private static bool IsLayerZombieT1(ZXMapLayerZombies v)
        {
            return v == ZXMapLayerZombies.Weak1 || v == ZXMapLayerZombies.Weak2 || v == ZXMapLayerZombies.Weak3;
        }

        private static bool IsLayerZombieT2(ZXMapLayerZombies v)
        {
            return v == ZXMapLayerZombies.Runners1 || v == ZXMapLayerZombies.Runners2 || v == ZXMapLayerZombies.Runners3 ||
                   v == ZXMapLayerZombies.Medium1 || v == ZXMapLayerZombies.Medium2 || v == ZXMapLayerZombies.Medium3 ||
                   v == ZXMapLayerZombies.Strong1 || v == ZXMapLayerZombies.Strong2 || v == ZXMapLayerZombies.Strong3;
        }

        private static void EvaluateCandidateSpot(
            int cx,
            int cy,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            out int buildable16,
            out int trees16,
            out int grass16,
            out int stone24)
        {
            buildable16 = 0;
            trees16 = 0;
            grass16 = 0;
            stone24 = 0;
            int xMin = Math.Max(0, cx - 24), xMax = Math.Min(gN - 1, cx + 24);
            int yMin = Math.Max(0, cy - 24), yMax = Math.Min(gN - 1, cy + 24);
            for (int x = xMin; x <= xMax; x++)
            {
                int dx = x - cx;
                for (int y = yMin; y <= yMax; y++)
                {
                    int dy = y - cy;
                    int d2 = dx * dx + dy * dy;
                    if (d2 > 24 * 24) continue;
                    var ob = layerObjects[x, y];
                    var tr = layerTerrain[x, y];
                    if (ob == ZXMapLayerObjectType.MineralStone) stone24++;
                    if (d2 <= 16 * 16)
                    {
                        if (ob == ZXMapLayerObjectType.Tree) trees16++;
                        if (tr == ZXMapLayerTerrainType.Grass) grass16++;
                        if ((tr == ZXMapLayerTerrainType.Earth || tr == ZXMapLayerTerrainType.Grass) && ob == ZXMapLayerObjectType.None)
                            buildable16++;
                    }
                }
            }
        }

        private static bool IsPadSuitableForBase(
            int cx,
            int cy,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            HashSet<long> blockedCells,
            int gN,
            bool allowTrees)
        {
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                for (int y = cy - 3; y <= cy + 3; y++)
                {
                    if (x < 4 || y < 4 || x >= gN - 4 || y >= gN - 4) return false;
                    if (blockedCells != null && blockedCells.Contains(PackCell(x, y))) return false;
                    var t = layerTerrain[x, y];
                    if (t != ZXMapLayerTerrainType.Earth && t != ZXMapLayerTerrainType.Grass) return false;
                    var o = layerObjects[x, y];
                    if (o == ZXMapLayerObjectType.None) continue;
                    if (allowTrees && o == ZXMapLayerObjectType.Tree) continue;
                    return false;
                }
            }
            return true;
        }

        private static bool IsCellWalkable(
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            int x,
            int y)
        {
            if (x < 2 || y < 2 || x >= gN - 2 || y >= gN - 2) return false;
            var t = layerTerrain[x, y];
            if (t != ZXMapLayerTerrainType.Earth && t != ZXMapLayerTerrainType.Grass) return false;
            return layerObjects[x, y] == ZXMapLayerObjectType.None;
        }

        private static bool IsCellInPlayableArea(int x, int y, RectangleF playable, int gN)
        {
            if (x < 4 || y < 4 || x >= gN - 4 || y >= gN - 4) return false;
            if (playable.IsEmpty) return true;
            return playable.Contains(DXWorldGrid.ScenePointFromWorldCell(x, y, gN));
        }

        private static bool IsWithinSafePlayable(int x, int y, RectangleF playable, int gN, float sceneMargin)
        {
            if (x < 16 || y < 16 || x >= gN - 16 || y >= gN - 16) return false;
            if (playable.IsEmpty) return true;
            PointF scenePt = DXWorldGrid.ScenePointFromWorldCell(x, y, gN);
            RectangleF safe = playable;
            safe.Inflate(-sceneMargin, -sceneMargin);
            return safe.Contains(scenePt);
        }

        private static double MinDistanceToBases(int x, int y, List<Point> bases)
        {
            long bestSq = long.MaxValue;
            for (int i = 0; i < bases.Count; i++)
            {
                long d2 = DistSq(x, y, bases[i].X, bases[i].Y);
                if (d2 < bestSq) bestSq = d2;
            }
            return bestSq == long.MaxValue ? 999.0 : Math.Sqrt(bestSq);
        }

        private static double MinDistanceToEntities(int x, int y, List<DXEntity> entities)
        {
            if (entities == null || entities.Count == 0) return 999.0;
            long bestSq = long.MaxValue;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == null) continue;
                long d2 = DistSq(x, y, e.CellX, e.CellY);
                if (d2 < bestSq) bestSq = d2;
            }
            return bestSq == long.MaxValue ? 999.0 : Math.Sqrt(bestSq);
        }

        private static double MinEntityDistanceToBases(List<DXEntity> entities, List<Point> bases)
        {
            if (entities == null || entities.Count == 0 || bases == null || bases.Count == 0) return 999.0;
            long bestSq = long.MaxValue;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == null) continue;
                int ex = e.CellX, ey = e.CellY;
                for (int b = 0; b < bases.Count; b++)
                {
                    long d2 = DistSq(ex, ey, bases[b].X, bases[b].Y);
                    if (d2 < bestSq) bestSq = d2;
                }
            }
            return bestSq == long.MaxValue ? 999.0 : Math.Sqrt(bestSq);
        }

        private static bool TooCloseToAny(int x, int y, List<Point> points, int minDist)
        {
            long minSq = (long)minDist * minDist;
            for (int i = 0; i < points.Count; i++)
            {
                if (DistSq(x, y, points[i].X, points[i].Y) < minSq) return true;
            }
            return false;
        }

        private static Point? FindFreeWalkableCellNear(
            int targetX,
            int targetY,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            int gN,
            HashSet<long> occupiedCells)
        {
            int maxCoord = gN - 2;
            for (int radius = 0; radius <= 12; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                        int cx = targetX + dx;
                        int cy = targetY + dy;
                        if (cx < 2 || cy < 2 || cx > maxCoord || cy > maxCoord) continue;
                        if (occupiedCells != null && occupiedCells.Contains(PackCell(cx, cy))) continue;
                        if (!IsCellWalkable(layerTerrain, layerObjects, gN, cx, cy)) continue;
                        return new Point(cx, cy);
                    }
                }
            }
            return null;
        }

        private static void MarkAreaOccupied(Rectangle area, HashSet<long> occupied)
        {
            for (int x = area.Left - 1; x <= area.Right; x++)
                for (int y = area.Top - 1; y <= area.Bottom; y++)
                    occupied.Add(PackCell(x, y));
        }

        private static bool IsAreaFree(int left, int top, int w, int h, HashSet<long> occupied, int gN)
        {
            if (left < 6 || top < 6 || left + w >= gN - 6 || top + h >= gN - 6) return false;
            for (int x = left; x < left + w; x++)
                for (int y = top; y < top + h; y++)
                    if (occupied.Contains(PackCell(x, y))) return false;
            return true;
        }

        private static int GetSeedFromParams(object randomParams)
        {
            try
            {
                var p = randomParams?.GetType().GetProperty("Seed");
                if (p != null) return (int)p.GetValue(randomParams);
            }
            catch { }
            return 20261109;
        }

        private static float GetPopFromParams(object randomParams)
        {
            try
            {
                var p = randomParams?.GetType().GetProperty("FactorZombiePopulation");
                if (p != null) return (float)p.GetValue(randomParams);
            }
            catch { }
            return 1.0f;
        }

        private static long DistSq(int x1, int y1, int x2, int y2)
        {
            long dx = x1 - x2;
            long dy = y1 - y2;
            return dx * dx + dy * dy;
        }

        private static long PackCell(int x, int y) => ((long)x << 32) | (uint)y;

        internal static ulong Mix64(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        private static int NextInt(ref ulong state, int maxExclusive)
        {
            state = Mix64(state);
            return maxExclusive <= 1 ? 0 : (int)(state % (uint)maxExclusive);
        }

        private static double NextDouble(ref ulong state)
        {
            state = Mix64(state);
            return (state >> 11) * (1.0 / (1UL << 53));
        }
    }
}
