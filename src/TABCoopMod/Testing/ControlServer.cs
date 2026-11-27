using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Patches;
using TABCoopMod.Sync;
using TABCoopMod.UI;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// In-process JSON control channel for automated testing (MCP server connects over loopback TCP).
    /// Requests are queued from the socket thread and executed on the game's main/render thread from
    /// StartFrame, because issuing a command mutates live game state and is not thread-safe.
    ///
    /// Protocol (newline-delimited JSON, one request/response per line):
    ///   request : {"id":123,"tool":"move_units","args":{"ids":[...],"x":10,"y":20}}
    ///   response: {"id":123,"ok":true,"result":{...}}  or  {"id":123,"ok":false,"error":"..."}
    /// All game commands go through CommandExecutor/NetCommand so they follow the same path as the
    /// multiplayer replay (never touches local CSelectable).
    /// </summary>
    public static class ControlServer
    {
        private const int MaxListEntities = 5000;

        private sealed class PendingRequest
        {
            public string RequestJson;
            public string ResponseJson;
            public ManualResetEventSlim Done;
        }

        private static readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _port;

        public static bool IsRunning => _running;
        public static int Port => _port;

        /// <summary>Called every render frame from StartFrame. Starts the listener once and drains requests.</summary>
        public static void Tick()
        {
            int port = TestHarnessConfig.ControlPort;
            if (port <= 0) return;
            if (!_running) Start(port);
            ScreenshotService.Tick();
            Drain();
        }

        private static void Start(int port)
        {
            try
            {
                _port = port;
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TABCoopControlAccept" };
                _acceptThread.Start();
                ModLogger.Info($"[ControlServer] listening on 127.0.0.1:{port}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ControlServer] failed to start on port {port}", ex);
                _running = false;
            }
        }

        private static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) break; else continue; }

                var t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "TABCoopControlClient" };
                t.Start();
            }
        }

        private static void ClientLoop(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var pending = new PendingRequest { RequestJson = line, Done = new ManualResetEventSlim(false) };
                        _queue.Enqueue(pending);
                        int waitMs = (line.IndexOf("start_survival_gen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      line.IndexOf("capture_panorama", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      line.IndexOf("trigger_final_swarm", StringComparison.OrdinalIgnoreCase) >= 0)
                            ? 120000
                            : 10000;
                        if (!pending.Done.Wait(waitMs))
                        {
                            writer.WriteLine(ErrorResponse(0, "timeout waiting for game main thread (is the game paused/loading?)"));
                            continue;
                        }
                        writer.WriteLine(pending.ResponseJson ?? ErrorResponse(0, "no response"));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ControlServer] client loop ended: {ex.Message}");
            }
        }

        private static void Drain()
        {
            while (_queue.TryDequeue(out var pending))
            {
                Determinism.DeterministicSandboxPatches.FlushPendingAsyncTick(DXGame.Current);
                try { pending.ResponseJson = HandleRequest(pending.RequestJson); }
                catch (Exception ex)
                {
                    ModLogger.Error("[ControlServer] handler failed", ex);
                    pending.ResponseJson = ErrorResponse(0, ex.Message);
                }
                finally { pending.Done.Set(); }
            }
        }

        // ====================================================================
        // Request dispatch (runs on the game main thread)
        // ====================================================================

        private static string HandleRequest(string requestJson)
        {
            long id = 0;
            string tool = null;
            try
            {
                Dictionary<string, object> req = _json.Deserialize<Dictionary<string, object>>(requestJson);
                if (req == null) return ErrorResponse(0, "empty request");

                id = GetLong(req, "id", 0);
                tool = GetStr(req, "tool");
                var args = req.TryGetValue("args", out object a) ? a as Dictionary<string, object> : null;
                if (args == null) args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                object result = Dispatch(tool, args);
                AttachScreenshot(tool, args, result);
                return OkResponse(id, result);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ControlServer] tool '{tool}' failed: {ex.Message}");
                return ErrorResponse(id, ex.Message);
            }
        }

        private static object Dispatch(string tool, Dictionary<string, object> args)
        {
            switch ((tool ?? string.Empty).ToLowerInvariant())
            {
                case "ping": return Ping();
                case "get_state":
                case "game_state": return GetState();
                case "list_entities": return ListEntities(args);
                case "move_units": return MoveUnits(args);
                case "place_building": return PlaceBuilding(args);
                case "train_unit": return TrainUnit(args);
                case "cancel_current_order": return CancelCurrent(args);
                case "cancel_queued_order": return CancelQueued(args);
                case "set_paused": return SetPaused(args);
                case "set_game_speed": return SetGameSpeed(args);
                case "request_pause": return RequestPauseTool(args);
                case "list_mayors": return ListMayors(args);
                case "mayor_select": return SelectMayor(args);
                case "issue_command": return IssueRawCommand(args);
                case "screenshot": return TakeScreenshot(args);
                case "start_recording":
                    return ScreenshotService.StartRecording(
                        GetStr(args, "dir"),
                        GetInt(args, "width", 1440),
                        GetInt(args, "height", 900),
                        GetInt(args, "fps", 30));
                case "stop_recording":
                    return ScreenshotService.StopRecording();
                case "get_camera": return GetCamera();
                case "set_camera": return SetCamera(args);
                case "reveal_map": return RevealMap(args);
                case "open_lobby":
                    CoopLobbyWindow.ShowLobbyWindow();
                    return new Dictionary<string, object> { ["opened"] = true };
                case "lobby_state":
                    return GetLobbyState();
                case "lobby_action":
                    return RunLobbyAction(args);
                case "start_survival_gen":
                    return StartSurvivalGen(args);
                case "capture_panorama":
                    return CapturePanoramaTool(args);
                case "map_metrics":
                    return GameBridge.CollectCurrentMapMetrics();
                case "faction_state":
                    return GetFactionState();
                case "wonder_status":
                    return GetWonderStatus();
                case "transfer_resource":
                    return DispatchTransferResource(args);
                case "wonder_contribute":
                    return DispatchWonderCrowdfund(args, withdraw: false);
                case "wonder_withdraw":
                    return DispatchWonderCrowdfund(args, withdraw: true);
                case "toggle_mf_panel":
                    return ToggleMfPanel(args);
                case "test_grant_resources":
                    return TestGrantResources(args);
                case "find_build_spot":
                    return FindBuildSpot(args);
                case "set_cc_invuln":
                    return SetCcInvulnTool(args);
                case "trigger_final_swarm":
                    return TriggerFinalSwarmTool();
                case "save_game":
                    return SaveGameTool(args);
                case "get_inn_slots":
                    return GetInnSlotsInfo(args);
                case "invoke_inn_slot":
                    return InvokeInnSlot(args);
                case "force_place_building":
                    return ForcePlaceBuilding(args);
                default: throw new InvalidOperationException($"unknown tool '{tool}'");
            }
        }

        // ====================================================================
        // Tools
        // ====================================================================

        private static object Ping()
        {
            return new Dictionary<string, object>
            {
                ["pong"] = true,
                ["tick"] = CurrentTick(),
                ["port"] = _port
            };
        }

        private static object GetState()
        {
            var game = DXGame.Current;
            object ls = GameBridge.CurrentLevelState;

            int entityCount = 0, playerCount = 0;
            if (game?.Entities != null)
            {
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    entityCount++;
                    if (GameBridge.IsPlayerEntity(e)) playerCount++;
                }
            }

            object gameMode = GameBridge.GetGameStateProperty("GameMode");
            object inCampaign = GameBridge.GetGameStateProperty("InCampaign");
            object difficulty = GameBridge.GetGameStateProperty("Difficulty");
            object survivalParams = GameBridge.GetGameStateProperty("SurvivalModeParams");
            var curSys = GameBridge.CurrentGameSystem as DXSystem;

            GameBridge.GetLevelStateCommandCenterExtras(out int ccExtraWorkers, out int ccExtraFood);
            Determinism.DeterministicSandboxPatches.GetPerfMetrics(out double perfMspt, out double perfTps);
            bool pausedNow = Determinism.LockstepClock.PausedBySession || (game?.Paused ?? false);
            double targetTps = pausedNow ? 0.0 : 30.0 * Math.Max(0.1, game?.GameSpeed ?? 1.0);
            string bottleneck = pausedNow
                ? string.Empty
                : Sync.CoopSessionManager.ClassifyTpsBottleneck(
                    perfMspt, perfTps, targetTps, Determinism.DeterministicSandboxPatches.DiagAvgFrameMs);

            return new Dictionary<string, object>
            {
                ["tick"] = game?.IAIterationInt ?? 0,
                ["gameTimeMs"] = game?.GameTimeInt ?? 0,
                ["paused"] = game?.Paused ?? false,
                ["pausedBySession"] = Determinism.LockstepClock.PausedBySession,
                ["inputEnabled"] = game?.InputEnabled ?? false,
                ["focused"] = game?.Focused ?? false,
                ["visible"] = game?.Visible ?? false,
                ["systemEnabled"] = curSys?.Enabled ?? false,
                ["inModal"] = DXVision.GUI.DXModalScreen.IsInModal,
                ["fadeInScene"] = GameBridge.IsLoadingFadeInScene(),
                ["avgFrameMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgFrameMs, 1),
                ["avgLogicMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgTickLogicMs, 1),
                ["avgPhysMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgPhysicsMs, 1),
                ["mspt"] = Math.Round(perfMspt, 1),
                ["tps"] = Math.Round(perfTps, 1),
                ["tpsBottleneck"] = bottleneck,
                ["localRttToHostMs"] = Math.Round(Sync.CoopSessionManager.GetLocalEffectiveRttToHostMs(), 1),
                ["lastWantedTicks"] = Determinism.DeterministicSandboxPatches.DiagLastWantedTicks,
                ["lastRanTicks"] = Determinism.DeterministicSandboxPatches.DiagLastRanTicks,
                ["cursorLocked"] = game?.CursorLocked ?? false,
                ["gameSpeed"] = game?.GameSpeed ?? 0,
                ["countdownActive"] = PauseSyncController.CountdownActive,
                ["mayorCount"] = GameBridge.GetLevelCollectionCount("MayorStates"),
                ["levelLoaded"] = ls != null,
                ["endStatus"] = GameBridge.GetLevelEndStatus(),
                ["levelEnded"] = GameBridge.IsLevelEnded(),
                ["gameMode"] = gameMode?.ToString(),
                ["inCampaign"] = inCampaign is bool ic && ic,
                ["difficulty"] = difficulty?.ToString(),
                ["survivalMode"] = survivalParams != null,
                ["survivalNCells"] = survivalParams != null ? GameBridge.GetRandomParamsNCells(survivalParams) : 0,
                ["levelNCells"] = DXLevel.Current?.NCells ?? 0,
                ["commandCenterExtraWorkers"] = ccExtraWorkers,
                ["commandCenterExtraFood"] = ccExtraFood,
                ["balancePreset"] = Sync.CoopSessionManager.BalancePreset.ToString(),
                ["coopGameMode"] = Sync.CoopSessionManager.GameMode.ToString(),
                ["defeatRule"] = Sync.CoopSessionManager.DefeatRule.ToString(),
                ["spawnMode"] = Sync.FactionOwnershipTracker.ActiveSpawnMode.ToString(),
                ["mutationTendency"] = Sync.FactionOwnershipTracker.ActiveMutationTendency.ToString(),
                ["trackedOwners"] = Sync.FactionOwnershipTracker.TrackedEntityCount,
                ["aliveCommandCenters"] = Sync.FactionOwnershipTracker.CountAliveCommandCenters(),
                ["localPlayerExiled"] = Sync.FactionOwnershipTracker.IsExiled(Patches.CommandInterceptor.LocalPlayerId),
                ["hudDiag"] = GameBridge.GetHudDiagSummary(),
                ["gold"] = GameBridge.GetLevelInt("Gold"),
                ["wood"] = GameBridge.GetLevelInt("Wood"),
                ["stone"] = GameBridge.GetLevelInt("Stone"),
                ["iron"] = GameBridge.GetLevelInt("Iron"),
                ["oil"] = GameBridge.GetLevelInt("Oil"),
                ["entityCount"] = entityCount,
                ["playerEntityCount"] = playerCount
            };
        }

        private static object ListEntities(Dictionary<string, object> args)
        {
            string typeContains = GetStr(args, "typeContains");
            bool onlyPlayer = GetBool(args, "onlyPlayer", false);
            bool movable = GetBool(args, "movable", false);
            bool includeProjectiles = GetBool(args, "includeProjectiles", false);
            int limit = GetInt(args, "limit", 50);
            if (limit <= 0 || limit > MaxListEntities) limit = MaxListEntities;

            var list = new List<Dictionary<string, object>>();
            var game = DXGame.Current;
            if (game?.Entities != null)
            {
                Type movableType = movable ? GameBridge.TypeCMovable : null;

                // Sorted by ID so output is stable across calls (agents diff the result).
                var matched = new List<DXEntity>();
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    string tn = e.GetType().Name;
                    bool isProj = tn.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tn.IndexOf("Proyectile", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!includeProjectiles && isProj) continue;
                    if (onlyPlayer && !GameBridge.IsPlayerEntity(e)) continue;
                    if (!string.IsNullOrEmpty(typeContains) && tn.IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (movableType != null && !GameBridge.HasComponent(e, movableType)) continue;
                    matched.Add(e);
                }
                matched.Sort((a, b) => a.ID.CompareTo(b.ID));

                int count = 0;
                foreach (var e in matched)
                {
                    if (count++ >= limit) break;
                    list.Add(new Dictionary<string, object>
                    {
                        // IDs are 64-bit and exceed JS safe-integer range → always render as strings.
                        ["id"] = e.ID.ToString(CultureInfo.InvariantCulture),
                        ["type"] = e.GetType().Name,
                        ["x"] = Math.Round(e.Position.X, 2),
                        ["y"] = Math.Round(e.Position.Y, 2),
                        ["hp"] = GameBridge.GetLifeValue(e),
                        ["player"] = GameBridge.IsPlayerEntity(e),
                        ["owner"] = Sync.FactionOwnershipTracker.GetOwner(e.ID),
                        ["movable"] = GameBridge.HasComponent(e, GameBridge.TypeCMovable),
                        ["enabled"] = GameBridge.IsEntityEnabled(e)
                    });
                }
            }

            return new Dictionary<string, object> { ["count"] = list.Count, ["entities"] = list };
        }

        private static object MoveUnits(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");
            float x = GetFloat(args, "x");
            float y = GetFloat(args, "y");

            Type travelType = GameBridge.GameAssembly?.GetType("ZX.Commands.Travel");
            object travel = travelType != null ? GameBridge.GetCommandGeneric(travelType) : null;
            if (travel == null) throw new InvalidOperationException("Travel command singleton unresolved");

            var cmd = new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = CommandInterceptor.LocalPlayerId,
                CommandId = GameBridge.GetCommandId(travel),
                ExecutorEntityIds = ids,
                TargetPosition = new PointF(x, y),
                IsShiftQueued = GetBool(args, "shift", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object PlaceBuilding(Dictionary<string, object> args)
        {
            string typeName = GetStr(args, "buildingType") ?? "ZX.Entities.TentHouse";
            float x = GetFloat(args, "x");
            float y = GetFloat(args, "y");
            float w = GetFloat(args, "sizeW", 1f);
            float h = GetFloat(args, "sizeH", 1f);

            ulong builderId = GetULong(args, "builderId", 0);
            var ids = builderId != 0UL ? new[] { builderId } : Array.Empty<ulong>();

            var cmd = new NetCommand
            {
                Type = NetCommandType.PlaceBuilding,
                PlayerId = CommandInterceptor.LocalPlayerId,
                BuildingTypeName = typeName,
                ExecutorEntityIds = ids,
                TargetPosition = new PointF(x, y),
                TargetSize = new SizeF(w, h),
                TargetOrigin = new PointF(x, y),
                IsBonusBuilding = GetBool(args, "isBonus", false),
                InstantBuild = GetBool(args, "instant", false),
                IsShiftQueued = GetBool(args, "shift", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object TrainUnit(Dictionary<string, object> args)
        {
            ulong barracksId = GetULong(args, "barracksId", 0);
            if (barracksId == 0UL) throw new ArgumentException("barracksId is required");
            string unitType = GetStr(args, "unitType") ?? "ZX.Entities.Ranger";

            Type ut = GameBridge.GameAssembly?.GetType(unitType);
            if (ut == null) throw new ArgumentException($"unknown unit type '{unitType}'");

            object trainCmd = GameBridge.GetTrainCommandForType(ut);
            if (trainCmd == null) throw new InvalidOperationException($"no Train command for '{unitType}'");

            var cmd = new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = CommandInterceptor.LocalPlayerId,
                CommandId = GameBridge.GetCommandId(trainCmd),
                ExecutorEntityIds = new[] { barracksId }
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object CancelCurrent(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");

            var cmd = new NetCommand { Type = NetCommandType.CancelCurrentOrder, PlayerId = CommandInterceptor.LocalPlayerId, ExecutorEntityIds = ids };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object CancelQueued(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");

            var cmd = new NetCommand
            {
                Type = NetCommandType.CancelQueuedOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                ExecutorEntityIds = ids,
                QueueSlotIndex = (byte)GetInt(args, "slot", 0)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object SetPaused(Dictionary<string, object> args)
        {
            bool paused = GetBool(args, "paused", true);
            var cmd = new NetCommand { Type = NetCommandType.SetPaused, PlayerId = CommandInterceptor.LocalPlayerId, TargetPausedState = paused };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["paused"] = DXGame.Current?.Paused ?? false };
        }

        private static object IssueRawCommand(Dictionary<string, object> args)
        {
            int cmdId = GetInt(args, "commandId", 0);
            if (cmdId == 0)
            {
                string cmdName = GetStr(args, "commandName");
                if (!string.IsNullOrEmpty(cmdName))
                {
                    Type cmdType = GameBridge.FindCommandTypeByName(cmdName);
                    object inst = cmdType != null ? GameBridge.GetCommandInstanceByType(cmdType) : null;
                    if (inst != null)
                        cmdId = GameBridge.GetCommandId(inst);
                }
            }
            var cmd = new NetCommand
            {
                Type = (NetCommandType)GetInt(args, "type", 1),
                PlayerId = (byte)GetInt(args, "playerId", CommandInterceptor.LocalPlayerId),
                CommandId = cmdId,
                BuildingTypeName = GetStr(args, "buildingType") ?? string.Empty,
                ExecutorEntityIds = GetULongArray(args, "ids"),
                TargetEntityId = GetULong(args, "targetEntityId", 0),
                TargetPosition = new PointF(GetFloat(args, "x"), GetFloat(args, "y")),
                TargetSize = new SizeF(GetFloat(args, "sizeW", 0f), GetFloat(args, "sizeH", 0f)),
                TargetOrigin = new PointF(GetFloat(args, "originX"), GetFloat(args, "originY")),
                IsShiftQueued = GetBool(args, "shift", false),
                QueueSlotIndex = (byte)GetInt(args, "slot", 0),
                TargetPausedState = GetBool(args, "paused", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object TakeScreenshot(Dictionary<string, object> args)
        {
            string tag = GetStr(args, "tag") ?? "manual";
            string path = ScreenshotService.CaptureNow(tag);
            return new Dictionary<string, object>
            {
                ["saved"] = path != null,
                ["path"] = path ?? string.Empty
            };
        }

        private static object GetCamera()
        {
            var c = CameraProbe.Capture();
            float wx = 0f, wy = 0f;
            if (c.Valid)
            {
                var wpt = DXWorldGrid.WorldPointFromScenePoint(new PointF(c.X + c.W / 2f, c.Y + c.H / 2f));
                wx = wpt.X;
                wy = wpt.Y;
            }
            return new Dictionary<string, object>
            {
                ["valid"] = c.Valid,
                ["worldX"] = wx,
                ["worldY"] = wy,
                ["x"] = c.X,
                ["y"] = c.Y,
                ["w"] = c.W,
                ["h"] = c.H,
                ["screenW"] = c.ScreenW,
                ["screenH"] = c.ScreenH,
                ["windowW"] = c.WindowW,
                ["windowH"] = c.WindowH
            };
        }

        private static object SetCamera(Dictionary<string, object> args)
        {
            var scene = DXScene.Current;
            if (scene == null) throw new InvalidOperationException("no active scene");

            RectangleF cur = scene.CameraArea;
            float w = GetFloat(args, "w", cur.Width > 0f ? cur.Width : 0f);
            float h = GetFloat(args, "h", 0f);
            float zoom = GetFloat(args, "zoom", 1f);
            if (zoom > 0f && Math.Abs(zoom - 1f) > 1e-4f) { w /= zoom; h /= zoom; }

            // x/y and entity positions are WORLD-grid coordinates; GameBridge.SetCameraWorld converts
            // them to isometric scene space before centering the camera.
            float wx, wy;
            ulong eid = GetULong(args, "entityId", 0);
            if (eid != 0UL)
            {
                var ent = DXGame.Current?.GetEntityByID(eid);
                if (ent == null) throw new ArgumentException($"entity {eid} not found");
                wx = ent.Position.X; wy = ent.Position.Y;
            }
            else
            {
                var worldCenter = DXWorldGrid.WorldPointFromScenePoint(
                    new PointF(cur.X + cur.Width / 2f, cur.Y + cur.Height / 2f));
                wx = GetFloat(args, "x", worldCenter.X);
                wy = GetFloat(args, "y", worldCenter.Y);
            }

            if (!GameBridge.SetCameraWorld(new PointF(wx, wy), w, h))
                throw new InvalidOperationException("failed to set camera");

            RectangleF applied = scene.CameraArea;
            return new Dictionary<string, object>
            {
                ["worldX"] = wx, ["worldY"] = wy,
                ["x"] = applied.X, ["y"] = applied.Y, ["w"] = applied.Width, ["h"] = applied.Height,
                ["centerX"] = applied.X + applied.Width / 2f,
                ["centerY"] = applied.Y + applied.Height / 2f
            };
        }

        private static object RequestPauseTool(Dictionary<string, object> args)
        {
            bool paused = GetBool(args, "paused", true);
            PauseSyncController.RequestPause(paused);
            return new Dictionary<string, object>
            {
                ["requested"] = paused,
                ["countdownActive"] = PauseSyncController.CountdownActive,
                ["secondsRemaining"] = PauseSyncController.SecondsRemaining,
                ["paused"] = DXGame.Current?.Paused ?? false
            };
        }

        private static object ListMayors(Dictionary<string, object> args)
        {
            int level = GetInt(args, "level", 0);
            var list = GameBridge.GetMayorList(level);
            return new Dictionary<string, object> { ["count"] = list.Count, ["mayors"] = list };
        }

        private static object SelectMayor(Dictionary<string, object> args)
        {
            string id = GetStr(args, "mayorId");
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("mayorId is required");

            var cmd = new NetCommand { Type = NetCommandType.MayorVoteOrSelect, PlayerId = CommandInterceptor.LocalPlayerId, MayorId = id };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object>
            {
                ["dispatched"] = true,
                ["mayorId"] = id,
                ["mayorCount"] = GameBridge.GetLevelCollectionCount("MayorStates")
            };
        }

        private static object SetGameSpeed(Dictionary<string, object> args)
        {
            float speed = GetFloat(args, "speed", 1f);
            speed = Math.Max(0f, Math.Min(20f, speed));
            var cmd = new NetCommand { Type = NetCommandType.SetGameSpeed, PlayerId = CommandInterceptor.LocalPlayerId, GameSpeed = speed };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object>
            {
                ["dispatched"] = true,
                ["gameSpeed"] = DXGame.Current?.GameSpeed ?? 0
            };
        }

        private static object RevealMap(Dictionary<string, object> args)
        {
            bool reveal = GetBool(args, "enabled", true);
            bool ok = GameBridge.RevealAll(reveal);
            return new Dictionary<string, object>
            {
                ["reveal"] = reveal,
                ["applied"] = ok
            };
        }

        private static void AttachScreenshot(string tool, Dictionary<string, object> args, object result)
        {
            if (!(result is Dictionary<string, object> dict)) return;
            if (!TestHarnessConfig.AutoShot) return;
            if (!GetBool(args, "screenshot", true)) return; // per-call opt-out
            if (!IsMutatingTool(tool)) return;

            // Deferred capture: the file appears a couple of frames after the operation is applied.
            dict["screenshot"] = ScreenshotService.RequestAfterFrames(tool);
        }

        private static bool IsMutatingTool(string tool)
        {
            switch ((tool ?? string.Empty).ToLowerInvariant())
            {
                case "move_units":
                case "place_building":
                case "force_place_building":
                case "train_unit":
                case "cancel_current_order":
                case "cancel_queued_order":
                case "set_paused":
                case "set_game_speed":
                case "request_pause":
                case "mayor_select":
                case "issue_command":
                case "set_camera":
                case "reveal_map":
                    return true;
                default:
                    return false;
            }
        }

        private static object GetLobbyState()
        {
            var peers = new List<Dictionary<string, object>>();
            foreach (var p in Sync.CoopSessionManager.GetEffectivePeers())
            {
                peers.Add(new Dictionary<string, object>
                {
                    ["id"] = p.PlayerId,
                    ["factionSlot"] = p.EffectiveFactionSlot,
                    ["name"] = p.PlayerName,
                    ["ready"] = p.IsReady
                });
            }

            var peerLinks = new List<Dictionary<string, object>>();
            foreach (var link in Sync.CoopSessionManager.GetRoomPeerConnectionSnapshots())
            {
                peerLinks.Add(new Dictionary<string, object>
                {
                    ["playerId"] = link.PlayerId,
                    ["playerName"] = link.PlayerName,
                    ["pathSummary"] = link.PathSummary,
                    ["rawRttMs"] = Math.Round(link.RawRttMs, 1),
                    ["estimatedActualDelayMs"] = Math.Round(link.EstimatedActualDelayMs, 1)
                });
            }

            return new Dictionary<string, object>
            {
                ["onMainMenu"] = GameBridge.IsOnMainMenu(),
                ["mainMenuReady"] = GameBridge.IsMainMenuReadyForInput(),
                ["inLevel"] = GameBridge.CurrentLevelState != null,
                ["loading"] = GameBridge.IsGameLoading(),
                ["anyModalOpen"] = GameBridge.IsAnyModalOpen(),
                ["modalHasEditBox"] = DXVision.GUI.DXModalScreen.IsInModal && HasModalEditBox(DXVision.GUI.DXModalScreen.CurrentModalObject),
                ["lobbyWindowOpen"] = CoopLobbyWindow.IsOpen,
                ["sessionActive"] = Sync.CoopSessionManager.IsSessionActive,
                ["isHost"] = Sync.CoopSessionManager.Transport?.IsHost ?? false,
                ["localPlayerId"] = Sync.CoopSessionManager.Transport?.LocalPlayerId ?? 0,
                ["localFactionSlot"] = Patches.CommandInterceptor.LocalPlayerId,
                ["hasDisconnectAlert"] = Sync.CoopSessionManager.HasDisconnectAlert,
                ["disconnectAlertSlot"] = Sync.CoopSessionManager.DisconnectAlertSlot,
                ["disconnectAlertName"] = Sync.CoopSessionManager.DisconnectAlertPlayerName,
                ["disconnectAlertVisible"] = UI.DisconnectNoticeOverlay.IsVisible,
                ["roomPhase"] = Sync.CoopSessionManager.RoomPhase.ToString(),
                ["canStart"] = Sync.CoopSessionManager.CanHostStartGame,
                ["balancePreset"] = Sync.CoopSessionManager.BalancePreset.ToString(),
                ["coopGameMode"] = Sync.CoopSessionManager.GameMode.ToString(),
                ["defeatRule"] = Sync.CoopSessionManager.DefeatRule.ToString(),
                ["spawnMode"] = Sync.CoopSessionManager.SpawnMode.ToString(),
                ["sessionSeed"] = Sync.CoopSessionManager.CurrentSessionSeed,
                ["survivalGenBusy"] = GameBridge.SurvivalGenInProgress,
                ["survivalGenOk"] = GameBridge.SurvivalGenLastOk,
                ["survivalGenError"] = GameBridge.SurvivalGenLastError ?? "",
                ["isLocalLoopback"] = Sync.CoopSessionManager.IsLocalLoopbackSession,
                ["chaosEnabled"] = Sync.CoopSessionManager.LocalChaosEnabled,
                ["chaosLatencyMs"] = Sync.CoopSessionManager.LocalChaosLatencyMs,
                ["chaosJitterMs"] = Sync.CoopSessionManager.LocalChaosJitterMs,
                ["chaosLossRate"] = Sync.CoopSessionManager.LocalChaosLossRate,
                ["smoothedRttMs"] = Sync.CoopSessionManager.CurrentSmoothedRttMs,
                ["inputDelayTicks"] = Sync.CoopSessionManager.InputDelayTicks,
                ["peerLinks"] = peerLinks,
                ["peers"] = peers
            };
        }

        private static object RunLobbyAction(Dictionary<string, object> args)
        {
            string act = GetStr(args, "action") ?? "";
            switch (act.ToLowerInvariant())
            {
                case "close_top_modal":
                    var modal = DXVision.GUI.DXModalScreen.CurrentModalObject;
                    if (modal != null) GameBridge.CloseZXWindow(modal);
                    return new Dictionary<string, object> { ["ok"] = modal != null };
                case "disconnect_pause":
                    Sync.CoopSessionManager.TogglePauseForDisconnectWait();
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["paused"] = Determinism.LockstepClock.PausedBySession || (DXGame.Current?.Paused ?? false)
                    };
                case "disconnect_save_exit":
                    return new Dictionary<string, object>
                    {
                        ["ok"] = Sync.CoopSessionManager.HostSaveAndExitFromAlert(GetStr(args, "saveName"))
                    };
                case "disconnect_dismiss":
                    Sync.CoopSessionManager.DismissDisconnectAlert();
                    return new Dictionary<string, object> { ["ok"] = true };
                case "test_select":
                {
                    ulong eid = GetULong(args, "entityId", 0);
                    var ent = eid != 0UL ? DXGame.Current?.GetEntityByID(eid) : null;
                    if (ent == null) return new Dictionary<string, object> { ["ok"] = false, ["selectedCount"] = 0 };
                    GameBridge.SelectOnlyEntity(ent);
                    var selIds = GameBridge.GetSelectedPlayerEntityIds();
                    bool canSel = Sync.FactionOwnershipTracker.CanLocalPlayerSelect(ent);
                    bool selected = GameBridge.IsEntitySelected(ent);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["canSelect"] = canSel,
                        ["selectedCount"] = selIds.Length,
                        ["isEntitySelected"] = selected
                    };
                }
                case "test_kill_entity":
                {
                    ulong eid = GetULong(args, "entityId", 0);
                    var ent = eid != 0UL ? DXGame.Current?.GetEntityByID(eid) : null;
                    bool killed = ent != null && GameBridge.TryCorrectLife(ent, 0, forceKill: true);
                    return new Dictionary<string, object> { ["ok"] = killed };
                }
                case "test_swarm_audit":
                {
                    bool triggerLive = GetBool(args, "triggerLive", false);
                    return Patches.MultiWaveSwarmContext.RunSwarmAudit(triggerLive);
                }
                case "confirm_modal":
                {
                    var topModal = DXVision.GUI.DXModalScreen.CurrentModalObject;
                    bool hasEdit = HasModalEditBox(topModal);
                    string inputText = GetStr(args, "inputText");
                    if (!string.IsNullOrEmpty(inputText) && topModal != null)
                        SetModalEditBoxText(topModal, inputText);

                    DXVision.GUI.DXButton confirmBtn = FindRightmostEnabledButton(topModal);
                    ModLogger.Info($"[ControlServer] confirm_modal: topModal={topModal?.GetType().Name ?? "null"} hasEdit={hasEdit} inputText='{inputText ?? ""}' btn={(confirmBtn != null ? confirmBtn.X + "," + confirmBtn.Y : "null")}");
                    if (confirmBtn != null)
                    {
                        confirmBtn.Activate();
                        return new Dictionary<string, object> { ["ok"] = true, ["hasEdit"] = hasEdit, ["x"] = confirmBtn.X, ["y"] = confirmBtn.Y };
                    }
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "no enabled confirm button found on modal" };
                }
                case "kick_fade":
                    GameBridge.KickLoadingFade(700.0);
                    return new Dictionary<string, object> { ["ok"] = true };
                case "return_to_menu":
                    bool r = GameBridge.ReturnToMainMenu();
                    return new Dictionary<string, object> { ["ok"] = r };
                case "trigger_game_end":
                    bool won = GetBool(args, "won", false);
                    bool endOk = GameBridge.TriggerGameEnd(won);
                    return new Dictionary<string, object> { ["ok"] = endOk, ["won"] = won, ["endStatus"] = GameBridge.GetLevelEndStatus() };
                case "trigger_mayor_popup":
                    bool popOk = GameBridge.TriggerMayorPopup();
                    return new Dictionary<string, object> { ["ok"] = popOk, ["inModal"] = DXVision.GUI.DXModalScreen.IsInModal };
                case "load_save":
                    if (!Sync.CoopSessionManager.CanHostStartGame)
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = "cannot start before clients are ready" };
                    string save = GetStr(args, "save") ?? "1_Backup";
                    ModLogger.InfoImmediate($"[ControlServer] load_save('{save}') requested (inModal={DXVision.GUI.DXModalScreen.IsInModal}, lobbyOpen={CoopLobbyWindow.IsOpen}).");
                    CoopLobbyWindow.ForceHideWithoutStopping();
                    DXVision.GUI.DXModalScreen.CloseAll();
                    Sync.CoopSessionManager.SetHostRoomPhase(Sync.LobbyRoomPhase.HostLoadingLevel, "房主正在加载存档...");
                    GameBridge.PrepareForProgrammaticLoad();
                    GameBridge.LoadSaveFileAndEnter(save);
                    Sync.CoopSessionManager.NotifyExternalLoadStarted();
                    GameBridge.KickLoadingFade(700.0);
                    return new Dictionary<string, object> { ["ok"] = true, ["save"] = save };
                case "probe":
                    var res = Networking.LocalTcpLoopbackTransport.ProbeLocalHost();
                    return new Dictionary<string, object>
                    {
                        ["online"] = res.IsOnline,
                        ["players"] = res.PlayerCount,
                        ["max"] = res.MaxPlayers,
                        ["phase"] = res.RoomPhase,
                        ["hostName"] = res.HostName
                    };
                case "set_chaos":
                    Sync.CoopSessionManager.SetLocalChaosSettings(
                        GetBool(args, "enabled", Sync.CoopSessionManager.LocalChaosEnabled),
                        GetInt(args, "latencyMs", Sync.CoopSessionManager.LocalChaosLatencyMs),
                        GetInt(args, "jitterMs", Sync.CoopSessionManager.LocalChaosJitterMs),
                        GetFloat(args, "lossRate", (float)Sync.CoopSessionManager.LocalChaosLossRate));
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["enabled"] = Sync.CoopSessionManager.LocalChaosEnabled,
                        ["latencyMs"] = Sync.CoopSessionManager.LocalChaosLatencyMs,
                        ["jitterMs"] = Sync.CoopSessionManager.LocalChaosJitterMs,
                        ["lossRate"] = Sync.CoopSessionManager.LocalChaosLossRate
                    };
                case "bonus_slots":
                    return GetBonusSlotsInfo();
                case "invoke_bonus_slot":
                    return InvokeBonusSlot(args);
                default:
                    bool ok = CoopLobbyWindow.InvokeTestAction(act);
                    return new Dictionary<string, object> { ["ok"] = ok, ["action"] = act };
            }
        }

        private static object StartSurvivalGen(Dictionary<string, object> args)
        {
            int seed = GetInt(args, "seed", 12345);
            string theme = GetStr(args, "theme") ?? "BR";
            float factorPop = GetFloat(args, "factorPop", 1.0f);
            float factorDuration = GetFloat(args, "factorDuration", 1.0f);
            string mode = (GetStr(args, "mode") ?? "vanilla").ToLowerInvariant();

            Sync.CoopBalancePreset preset = Sync.CoopBalancePreset.Vanilla;
            int playerCount = 1;
            bool? multiFaction = args != null && args.ContainsKey("multiFaction") ? (bool?)GetBool(args, "multiFaction", false) : null;
            if (mode == "coop2p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 2;
            }
            else if (mode == "coop3p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 3;
            }
            else if (mode == "mf2p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 2;
                multiFaction = true;
            }
            else if (mode == "mf3p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 3;
                multiFaction = true;
            }

            Sync.MultiFactionSpawnMode? spawnMode = null;
            string rawSpawn = GetStr(args, "spawnMode");
            if (!string.IsNullOrEmpty(rawSpawn))
            {
                if (string.Equals(rawSpawn, "clustered", StringComparison.OrdinalIgnoreCase))
                    spawnMode = Sync.MultiFactionSpawnMode.Clustered;
                else if (string.Equals(rawSpawn, "separated", StringComparison.OrdinalIgnoreCase))
                    spawnMode = Sync.MultiFactionSpawnMode.Separated;
            }

            bool started = GameBridge.StartSurvivalAsync(seed, theme, factorPop, factorDuration, preset, playerCount, multiFaction, spawnMode);
            return new Dictionary<string, object>
            {
                ["ok"] = started,
                ["started"] = started,
                ["mode"] = mode,
                ["spawnMode"] = spawnMode?.ToString() ?? Sync.CoopSessionManager.SpawnMode.ToString()
            };
        }

        private static object CapturePanoramaTool(Dictionary<string, object> args)
        {
            string path = GetStr(args, "path");
            if (string.IsNullOrEmpty(path))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "path is required" };
            int res = GetInt(args, "resolution", 2048);
            bool reveal = GetBool(args, "reveal", true);

            bool ok = GameBridge.CaptureMapPanorama(path, res, reveal);
            return new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["path"] = path,
                ["resolution"] = res
            };
        }

        private static object GetBonusSlotsInfo()
        {
            var cc = GameBridge.GetCommandCenterEntity();
            var slots = new List<Dictionary<string, object>>();
            var cmds = GameBridge.GetAvailableCommandsForEntity(cc);
            if (cmds != null)
            {
                foreach (object cmd in cmds)
                {
                    if (!GameBridge.TryGetEntityBonusInfo(cmd, out int slot, out Type entType, out bool isStructure, out _))
                        continue;
                    if (entType == null) continue;
                    int count = GameBridge.GetBonusEntityCount(entType, out _);
                    slots.Add(new Dictionary<string, object>
                    {
                        ["slot"] = slot,
                        ["entityType"] = entType.FullName,
                        ["isStructure"] = isStructure,
                        ["count"] = count
                    });
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = cc != null,
                ["ccId"] = cc?.ID ?? 0UL,
                ["slots"] = slots
            };
        }

        private static object InvokeBonusSlot(Dictionary<string, object> args)
        {
            int targetSlot = GetInt(args, "slot", 1);
            var cc = GameBridge.GetCommandCenterEntity();
            if (cc == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "CommandCenter not found" };

            var cmds = GameBridge.GetAvailableCommandsForEntity(cc);
            object matched = null;
            if (cmds != null)
            {
                foreach (object cmd in cmds)
                {
                    if (GameBridge.TryGetEntityBonusInfo(cmd, out int slot, out _, out _, out _) && slot == targetSlot)
                    {
                        matched = cmd;
                        break;
                    }
                }
            }
            if (matched == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"Bonus slot {targetSlot} not found" };

            float x = GetFloat(args, "x", 0f);
            float y = GetFloat(args, "y", 0f);
            float w = GetFloat(args, "sizeW", 0f);
            float h = GetFloat(args, "sizeH", 0f);
            object cmdTarget = GameBridge.CreateCommandTarget(
                (x != 0f || y != 0f) ? new PointF(x, y) : PointF.Empty,
                (w > 0f && h > 0f) ? new SizeF(w, h) : SizeF.Empty,
                (x != 0f || y != 0f) ? new PointF(x, y) : PointF.Empty,
                0UL);

            // Invokes ZXCommand.Execute cleanly through Harmony Patch_ZXCommand_Execute just like a real UI click.
            GameBridge.ExecuteCommandOnEntity(matched, cc, cmdTarget);
            GameBridge.TryGetEntityBonusInfo(matched, out _, out Type entType, out bool isStructure, out _);
            return new Dictionary<string, object>
            {
                ["ok"] = entType != null,
                ["slot"] = targetSlot,
                ["entityType"] = entType?.FullName ?? string.Empty,
                ["isStructure"] = isStructure
            };
        }

        private static DXEntity ResolveTargetInn(Dictionary<string, object> args)
        {
            ulong innId = GetULong(args, "innId", 0UL);
            if (innId != 0UL)
            {
                var ent = DXGame.Current?.GetEntityByID(innId);
                return GameBridge.IsTheInnEntity(ent) ? ent : null;
            }
            foreach (var ent in GameBridge.GetPlayerEntities())
            {
                if (!GameBridge.IsTheInnEntity(ent)) continue;
                if (Sync.CoopSessionManager.IsMultiFactionActive
                    && !Sync.FactionOwnershipTracker.CanPlayerControl(CommandInterceptor.LocalPlayerId, ent))
                    continue;
                return ent;
            }
            return null;
        }

        private static object GetInnSlotsInfo(Dictionary<string, object> args)
        {
            DXEntity inn = ResolveTargetInn(args);
            var slots = new List<Dictionary<string, object>>();
            int mercCount = inn != null ? GameBridge.GetTheInnMercenaryCount(inn) : -1;
            if (inn != null)
            {
                var cmds = GameBridge.GetAvailableCommandsForEntity(inn);
                if (cmds != null)
                {
                    foreach (object cmd in cmds)
                    {
                        if (!GameBridge.TryGetContractMercenaryInfo(cmd, inn, out int slot, out Type entType, out _, out _))
                            continue;
                        if (slot <= 0 || entType == null) continue;
                        bool canExec = GameBridge.CanExecuteCommandOnEntity(cmd, inn);
                        slots.Add(new Dictionary<string, object>
                        {
                            ["slot"] = slot,
                            ["entityType"] = entType.FullName,
                            ["canExecute"] = canExec
                        });
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = inn != null,
                ["innId"] = inn?.ID ?? 0UL,
                ["mercCount"] = mercCount,
                ["slots"] = slots
            };
        }

        private static object InvokeInnSlot(Dictionary<string, object> args)
        {
            int targetSlot = GetInt(args, "slot", 1);
            DXEntity inn = ResolveTargetInn(args);
            if (inn == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "TheInn not found" };

            var cmds = GameBridge.GetAvailableCommandsForEntity(inn);
            object matched = null;
            if (cmds != null)
            {
                foreach (object cmd in cmds)
                {
                    if (GameBridge.TryGetContractMercenaryInfo(cmd, inn, out int slot, out _, out _, out _) && slot == targetSlot)
                    {
                        matched = cmd;
                        break;
                    }
                }
            }
            if (matched == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"Mercenary slot {targetSlot} not found" };

            GameBridge.TryGetContractMercenaryInfo(matched, inn, out _, out Type entType, out _, out int mercCount);
            object cmdTarget = GameBridge.CreateCommandTarget(PointF.Empty, SizeF.Empty, PointF.Empty, 0UL);
            // Invokes ZXCommand.Execute cleanly through Harmony Patch_ZXCommand_Execute just like a real UI click.
            GameBridge.ExecuteCommandOnEntity(matched, inn, cmdTarget);
            return new Dictionary<string, object>
            {
                ["ok"] = entType != null,
                ["innId"] = inn.ID,
                ["slot"] = targetSlot,
                ["mercCountBefore"] = mercCount,
                ["entityType"] = entType?.FullName ?? string.Empty
            };
        }

        private static bool HasModalEditBox(DXVision.DXObject root)
        {
            if (root == null) return false;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is DXVision.DXEditBoxObject) return true;
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
            return false;
        }

        private static void SetModalEditBoxText(DXVision.DXObject root, string text)
        {
            if (root == null) return;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is DXVision.DXEditBoxObject editBox)
                {
                    editBox.Text = text;
                }
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
        }

        private static DXVision.GUI.DXButton FindRightmostEnabledButton(DXVision.DXObject root)
        {
            if (root == null) return null;
            DXVision.GUI.DXButton best = null;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null || !cur.Visible) continue;
                if (cur is DXVision.GUI.DXButton btn && btn.Enabled)
                {
                    // Prefer the bottom-right confirmation button (higher Y, then higher X).
                    if (best == null || btn.Y > best.Y + 5f || (Math.Abs(btn.Y - best.Y) <= 5f && btn.X > best.X))
                        best = btn;
                }
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
            return best;
        }

        private static int CurrentTick() => DXGame.Current?.IAIterationInt ?? 0;

        // ====================================================================
        // JSON helpers
        // ====================================================================

        private static object GetFactionState()
        {
            FactionContextManager.FlushActiveOwnerFromLevelState();
            var players = new List<Dictionary<string, object>>();
            for (byte pid = 1; pid <= 3; pid++)
            {
                if (!Sync.FactionOwnershipTracker.IsSlotParticipating(pid)) continue;
                var l = FactionContextManager.GetLedger(pid);
                if (l == null) continue;
                var e = l.Economy;
                players.Add(new Dictionary<string, object>
                {
                    ["playerId"] = pid,
                    ["exiled"] = FactionOwnershipTracker.IsExiled(pid),
                    ["gold"] = e.Gold,
                    ["wood"] = e.Wood,
                    ["stone"] = e.Stone,
                    ["iron"] = e.Iron,
                    ["oil"] = e.Oil,
                    ["foodSupply"] = e.FoodSupply,
                    ["foodNeeded"] = e.FoodNeeded,
                    ["workersSupply"] = e.WorkersSupply,
                    ["workersNeeded"] = e.WorkersNeeded,
                    ["energySupply"] = e.EnergySupply,
                    ["energyNeeded"] = e.EnergyNeeded,
                    ["ownEnergySupply"] = l.OwnEnergySupply,
                    ["ownEnergyNeeded"] = l.OwnEnergyNeeded,
                    ["nMarkets"] = e.NMarkets,
                    ["showFullMap"] = l.ShowFullMap,
                    ["onlyOneCmdCount"] = l.OnlyOneExecutedCommands.Count
                });
            }

            return new Dictionary<string, object>
            {
                ["isMultiFaction"] = CoopSessionManager.IsMultiFactionActive,
                ["initialized"] = FactionContextManager.IsInitialized,
                ["localPlayerId"] = CommandInterceptor.LocalPlayerId,
                ["hasAnyAliveMarket"] = FactionContextManager.HasAnyAliveMarket(),
                ["zeroTransferFeeActive"] = FactionContextManager.ZeroTransferFeeActive,
                ["sharedEnergyGridActive"] = FactionContextManager.SharedEnergyGridActive,
                ["sharedEnergySupply"] = FactionContextManager.SharedEnergySupply,
                ["sharedEnergyNeeded"] = FactionContextManager.SharedEnergyNeeded,
                ["economyHash"] = $"0x{FactionContextManager.ComputeEconomyHash():X16}",
                ["players"] = players
            };
        }

        private static object GetWonderStatus()
        {
            var wonders = new List<Dictionary<string, object>>();
            for (int i = 0; i < 3; i++)
            {
                var k = (FunctionalWonderType)i;
                var st = WonderCrowdfundManager.GetState(k);
                if (st == null) continue;
                wonders.Add(new Dictionary<string, object>
                {
                    ["kind"] = k.ToString(),
                    ["phase"] = st.Phase.ToString(),
                    ["requiredWorkshop"] = WonderCrowdfundManager.GetRequiredWorkshopDisplayNameEn(k),
                    ["hasRequiredWorkshop"] = WonderCrowdfundManager.HasRequiredWorkshopBuilt(k),
                    ["researchUnlocked"] = st.ResearchUnlocked,
                    ["researchTicksElapsed"] = st.ResearchTicksElapsed,
                    ["researchTicksTotal"] = st.ResearchTicksTotal,
                    ["researchRemainingSeconds"] = st.ResearchRemainingSeconds,
                    ["builderPlayerId"] = st.BuilderPlayerId,
                    ["buildingEntityId"] = st.BuildingEntityId,
                    ["researchTarget"] = BundleToDict(st.ResearchTarget),
                    ["researchContributed"] = BundleToDict(st.ResearchContributed),
                    ["buildTarget"] = BundleToDict(st.BuildTarget),
                    ["buildContributed"] = BundleToDict(st.BuildContributed)
                });
            }
            return new Dictionary<string, object>
            {
                ["initialized"] = WonderCrowdfundManager.IsInitialized,
                ["playerCount"] = WonderCrowdfundManager.PlayerCount,
                ["scaledSpireEnergy"] = WonderCrowdfundManager.GetScaledSpireEnergy(800),
                ["wonderHash"] = $"0x{WonderCrowdfundManager.ComputeHash():X16}",
                ["wonders"] = wonders
            };
        }

        private static Dictionary<string, object> BundleToDict(WonderCrowdfundManager.CostBundle b)
        {
            return new Dictionary<string, object>
            {
                ["gold"] = b.Gold,
                ["wood"] = b.Wood,
                ["stone"] = b.Stone,
                ["iron"] = b.Iron,
                ["oil"] = b.Oil
            };
        }

        private static ResourceTransferKind ParseResourceKind(string raw)
        {
            if (Enum.TryParse<ResourceTransferKind>(raw ?? "Gold", true, out var k))
                return k;
            return ResourceTransferKind.Gold;
        }

        private static FunctionalWonderType ParseWonderKind(string raw)
        {
            if (Enum.TryParse<FunctionalWonderType>(raw ?? "Telescope", true, out var w))
                return w;
            return FunctionalWonderType.Telescope;
        }

        private static object DispatchTransferResource(Dictionary<string, object> args)
        {
            byte toPlayer = (byte)GetInt(args, "toPlayer", 2);
            var res = ParseResourceKind(GetStr(args, "resource"));
            int amount = GetInt(args, "amount", 100);
            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = NetCommandType.TransferResource,
                PlayerId = CommandInterceptor.LocalPlayerId,
                MayorOptionIndex = toPlayer,
                QueueSlotIndex = (byte)res,
                CommandId = amount
            });
            return new Dictionary<string, object> { ["dispatched"] = true, ["toPlayer"] = toPlayer, ["resource"] = res.ToString(), ["amount"] = amount };
        }

        private static object DispatchWonderCrowdfund(Dictionary<string, object> args, bool withdraw)
        {
            var wonder = ParseWonderKind(GetStr(args, "wonder"));
            var res = ParseResourceKind(GetStr(args, "resource"));
            int amount = GetInt(args, "amount", 100);
            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = withdraw ? NetCommandType.WonderWithdraw : NetCommandType.WonderContribute,
                PlayerId = CommandInterceptor.LocalPlayerId,
                MayorOptionIndex = (byte)wonder,
                QueueSlotIndex = (byte)res,
                CommandId = amount
            });
            return new Dictionary<string, object> { ["dispatched"] = true, ["withdraw"] = withdraw, ["wonder"] = wonder.ToString(), ["resource"] = res.ToString(), ["amount"] = amount };
        }

        private static object ToggleMfPanel(Dictionary<string, object> args)
        {
            bool open = GetBool(args, "open", !MultiFactionPanel.IsOpen);
            int? tab = args != null && args.ContainsKey("tab") ? (int?)GetInt(args, "tab", 0) : null;
            FunctionalWonderType? wonder = args != null && args.ContainsKey("wonder") ? (FunctionalWonderType?)ParseWonderKind(GetStr(args, "wonder")) : null;
            MultiFactionPanel.SetOpen(open, tab, wonder);
            return new Dictionary<string, object> { ["isOpen"] = MultiFactionPanel.IsOpen, ["tab"] = MultiFactionPanel.ActiveTab, ["wonder"] = MultiFactionPanel.SelectedWonder.ToString() };
        }

        private static object TestGrantResources(Dictionary<string, object> args)
        {
            byte pid = (byte)GetInt(args, "playerId", CommandInterceptor.LocalPlayerId);
            int g = GetInt(args, "gold", 0);
            int w = GetInt(args, "wood", 0);
            int s = GetInt(args, "stone", 0);
            int i = GetInt(args, "iron", 0);
            int o = GetInt(args, "oil", 0);
            int extraStorage = GetInt(args, "extraStorage", 0);
            int extraWorkers = GetInt(args, "extraWorkers", 0);
            int extraFood = GetInt(args, "extraFood", 0);
            int extraEnergy = GetInt(args, "extraEnergy", 0);
            int extraColonists = GetInt(args, "extraColonists", 0);
            if (FactionContextManager.IsInitialized)
            {
                FactionContextManager.AdjustResourcesForTesting(pid, g, w, s, i, o, extraStorage, extraWorkers, extraColonists, extraFood, extraEnergy);
            }
            else if (GameBridge.ReadRawLevelEconomyState(out _))
            {
                if (extraWorkers != 0 || extraFood != 0 || extraEnergy != 0)
                {
                    GameBridge.AddLevelStateCommandCenterExtras(extraWorkers, extraFood, extraEnergy);
                    if (GameBridge.TypeCommandCenter != null && DXGame.Current != null)
                    {
                        foreach (var cc in DXGame.Current.EntitiesOfType(GameBridge.TypeCommandCenter))
                            GameBridge.CallBuildingRecalculateNear(cc);
                    }
                    GameBridge.RecalcLevelStateResources();
                }
                if (GameBridge.ReadRawLevelEconomyState(out var raw))
                {
                    if (extraStorage != 0) raw.ResourceStorage = Math.Max(50, raw.ResourceStorage + extraStorage);
                    if (extraColonists != 0) raw.Colonists += extraColonists;
                    raw.Gold = Math.Max(0, Math.Min(Math.Max(50, raw.ResourceStorage) * 40, raw.Gold + g));
                    raw.Wood = Math.Max(0, Math.Min(Math.Max(50, raw.ResourceStorage), raw.Wood + w));
                    raw.Stone = Math.Max(0, Math.Min(Math.Max(50, raw.ResourceStorage), raw.Stone + s));
                    raw.Iron = Math.Max(0, Math.Min(Math.Max(50, raw.ResourceStorage), raw.Iron + i));
                    raw.Oil = Math.Max(0, Math.Min(Math.Max(50, raw.ResourceStorage), raw.Oil + o));
                    GameBridge.WriteRawLevelEconomyState(in raw);
                }
            }
            return GetFactionState();
        }

        private static DXGame _cachedResCalcGame;
        private static object _cachedResCalcSystem;
        private static MethodInfo _cachedCalcYieldMethod;
        private static PropertyInfo _cachedCostWoodProp;

        private static bool TryComputeWoodYield(Rectangle rect, DXEntity templateEntity, out int woodYield)
        {
            woodYield = 0;
            DXGame game = DXGame.Current;
            if (game == null || templateEntity == null) return false;
            try
            {
                if (!object.ReferenceEquals(_cachedResCalcGame, game) || _cachedResCalcSystem == null || _cachedCalcYieldMethod == null)
                {
                    _cachedResCalcGame = game;
                    _cachedResCalcSystem = null;
                    _cachedCalcYieldMethod = null;
                    _cachedCostWoodProp = null;
                    foreach (DXSystem sys in game.Systems)
                    {
                        if (sys == null) continue;
                        foreach (MethodInfo m in sys.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (m.ReturnType == null || m.ReturnType.Name != "ZXResourcesCost") continue;
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length == 2 && ps[0].ParameterType == typeof(Rectangle) && typeof(DXEntity).IsAssignableFrom(ps[1].ParameterType))
                            {
                                _cachedResCalcSystem = sys;
                                _cachedCalcYieldMethod = m;
                                _cachedCostWoodProp = m.ReturnType.GetProperty("Wood", BindingFlags.Public | BindingFlags.Instance);
                                break;
                            }
                        }
                        if (_cachedCalcYieldMethod != null) break;
                    }
                }
                if (_cachedResCalcSystem == null || _cachedCalcYieldMethod == null || _cachedCostWoodProp == null)
                    return false;
                object costObj = _cachedCalcYieldMethod.Invoke(_cachedResCalcSystem, new object[] { rect, templateEntity });
                if (costObj == null) return false;
                woodYield = (int)_cachedCostWoodProp.GetValue(costObj, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindNearestWoodTarget(float cx, float cy, int maxR, out int treeX, out int treeY)
        {
            treeX = 0;
            treeY = 0;
            Type sawmillType = GameBridge.GameAssembly?.GetType("ZX.Entities.Sawmill");
            DXEntity sawmillTmpl = sawmillType != null ? (DXGame.Current?.GetEntityFromType(sawmillType) as DXEntity) : null;
            if (sawmillTmpl == null) return false;
            var size = new SizeF(Math.Max(1, sawmillTmpl.CellSize.Width), Math.Max(1, sawmillTmpl.CellSize.Height));
            for (int r = 4; r <= maxR; r += 2)
            {
                for (int dx = -r; dx <= r; dx += 2)
                {
                    for (int dy = -r; dy <= r; dy += 2)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < r - 1) continue;
                        int ix = (int)cx + dx;
                        int iy = (int)cy + dy;
                        if (!DXWorldGrid.ExistCell(ix, iy)) continue;
                        var pt = new PointF(ix, iy);
                        Rectangle rect = GameBridge.ResolveBuildCellArea(sawmillTmpl, pt, size, pt);
                        if (rect.Width > 0 && rect.Height > 0 && TryComputeWoodYield(rect, sawmillTmpl, out int wy) && wy >= 4)
                        {
                            treeX = ix;
                            treeY = iy;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static object FindBuildSpot(Dictionary<string, object> args)
        {
            string typeName = GetStr(args, "buildingType") ?? "ZX.Entities.TentHouse";
            float cx = GetFloat(args, "cx");
            float cy = GetFloat(args, "cy");
            int w = GetInt(args, "sizeW", 3);
            int h = GetInt(args, "sizeH", 3);
            int maxR = Math.Max(4, Math.Min(64, GetInt(args, "maxRadius", 28)));
            bool isSawmill = string.Equals(typeName, "ZX.Entities.Sawmill", StringComparison.Ordinal);
            int minWood = GetInt(args, "minWood", isSawmill ? 4 : 0);
            bool towardWood = GetBool(args, "towardWood", false);

            Type bt = GameBridge.GameAssembly?.GetType(typeName);
            if (bt == null) throw new ArgumentException($"unknown building type '{typeName}'");

            DXEntity tmpl = DXGame.Current?.GetEntityFromType(bt) as DXEntity;
            if (tmpl != null && tmpl.CellSize.Width > 0 && tmpl.CellSize.Height > 0)
            {
                w = tmpl.CellSize.Width;
                h = tmpl.CellSize.Height;
            }
            var size = new SizeF(w, h);

            int targetTreeX = 0, targetTreeY = 0;
            bool hasWoodGoal = towardWood && TryFindNearestWoodTarget(cx, cy, Math.Max(maxR, 56), out targetTreeX, out targetTreeY);
            bool foundAny = false;
            PointF bestPt = PointF.Empty;
            int bestWood = int.MinValue;
            double bestDistSq = double.MaxValue;

            for (int r = 4; r <= maxR; r++)
            {
                int step = r <= 32 ? 1 : 2;
                for (int dx = -r; dx <= r; dx += step)
                {
                    for (int dy = -r; dy <= r; dy += step)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int ix = (int)cx + dx;
                        int iy = (int)cy + dy;
                        if (!DXWorldGrid.ExistCell(ix, iy) || !DXWorldGrid.IsCellAvailable(ix, iy))
                            continue;

                        var pt = new PointF(ix, iy);
                        if (!GameBridge.CanPlaceBuildingAtTarget(bt, pt, size, pt))
                            continue;

                        if (minWood > 0 && tmpl != null)
                        {
                            Rectangle rect = GameBridge.ResolveBuildCellArea(tmpl, pt, size, pt);
                            if (rect.Width > 0 && rect.Height > 0 && TryComputeWoodYield(rect, tmpl, out int wy))
                            {
                                if (wy > bestWood)
                                {
                                    bestWood = wy;
                                    bestPt = pt;
                                    foundAny = wy >= minWood;
                                }
                                if (wy >= Math.Max(minWood, 8))
                                {
                                    return new Dictionary<string, object>
                                    {
                                        ["found"] = true,
                                        ["x"] = pt.X,
                                        ["y"] = pt.Y,
                                        ["sizeW"] = w,
                                        ["sizeH"] = h,
                                        ["woodYield"] = wy
                                    };
                                }
                            }
                            continue;
                        }

                        if (hasWoodGoal)
                        {
                            double d2 = (double)(ix - targetTreeX) * (ix - targetTreeX) + (double)(iy - targetTreeY) * (iy - targetTreeY);
                            if (d2 < bestDistSq)
                            {
                                bestDistSq = d2;
                                bestPt = pt;
                                foundAny = true;
                            }
                            continue;
                        }

                        return new Dictionary<string, object>
                        {
                            ["found"] = true,
                            ["x"] = pt.X,
                            ["y"] = pt.Y,
                            ["sizeW"] = w,
                            ["sizeH"] = h
                        };
                    }
                }
            }

            if (foundAny)
            {
                var res = new Dictionary<string, object>
                {
                    ["found"] = true,
                    ["x"] = bestPt.X,
                    ["y"] = bestPt.Y,
                    ["sizeW"] = w,
                    ["sizeH"] = h
                };
                if (bestWood > int.MinValue) res["woodYield"] = bestWood;
                return res;
            }

            return new Dictionary<string, object> { ["found"] = false };
        }

        private static string OkResponse(long id, object result)
        {
            var payload = new Dictionary<string, object> { ["id"] = id, ["ok"] = true, ["result"] = result };
            return _json.Serialize(payload);
        }

        private static object SetCcInvulnTool(Dictionary<string, object> args)
        {
            bool enabled = GetBool(args, "enabled", true);
            bool persist = GetBool(args, "persistInSave", true);
            int ccCount = GameBridge.SetCommandCenterInvulnerableFlag(enabled, persist);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["invulnerable"] = CommandCenterInvulnerability.IsActive,
                ["persistedInSave"] = persist && GameBridge.ReadCommandCenterInvulnerableFlagFromScriptVars(),
                ["ccCount"] = ccCount
            };
        }

        private static object TriggerFinalSwarmTool()
        {
            if (Sync.CoopSessionManager.IsInSyncedLevel)
            {
                CommandInterceptor.Dispatch(new NetCommand
                {
                    Type = NetCommandType.TestTriggerFinalSwarm,
                    PlayerId = CommandInterceptor.LocalPlayerId,
                    TargetSimTick = CurrentTick()
                });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["mode"] = "lockstep_synced",
                    ["dispatchedTick"] = CurrentTick()
                };
            }

            int triggered = 0;
            int reqs = 0;
            int units = 0;
            if (Determinism.LockstepClock.IsDeterministicModeActive)
            {
                Determinism.LockstepClock.RunInDeterministicContext(() =>
                    triggered = GameBridge.TriggerFinalSwarmEvents(out reqs, out units));
            }
            else
            {
                triggered = GameBridge.TriggerFinalSwarmEvents(out reqs, out units);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = triggered > 0,
                ["mode"] = "direct",
                ["triggeredEvents"] = triggered,
                ["queuedRequests"] = reqs,
                ["queuedUnits"] = units
            };
        }

        private static object ForcePlaceBuilding(Dictionary<string, object> args)
        {
            string typeName = GetStr(args, "buildingType") ?? "ZX.Entities.TheInn";
            if (GameBridge.GameAssembly?.GetType(typeName) == null)
                throw new ArgumentException($"unknown building type '{typeName}'");

            float x = GetFloat(args, "x");
            float y = GetFloat(args, "y");
            float w = GetFloat(args, "sizeW", 3f);
            float h = GetFloat(args, "sizeH", 3f);
            ulong builderId = GetULong(args, "builderId", 0);
            var ids = builderId != 0UL ? new[] { builderId } : Array.Empty<ulong>();
            byte pid = (byte)GetInt(args, "playerId", CommandInterceptor.LocalPlayerId);
            bool fastInn = GetBool(args, "fastInn", true);

            var cmd = new NetCommand
            {
                Type = NetCommandType.TestForcePlaceBuilding,
                PlayerId = pid,
                BuildingTypeName = typeName,
                ExecutorEntityIds = ids,
                TargetPosition = new PointF(x, y),
                TargetSize = new SizeF(w, h),
                TargetOrigin = new PointF(x, y),
                InstantBuild = fastInn
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object SaveGameTool(Dictionary<string, object> args)
        {
            string name = GetStr(args, "name") ?? "bench_save";
            foreach (char ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            if (name.EndsWith(".zxsav", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 6);

            string savesDir = GameBridge.GetSavesDirectory();
            if (string.IsNullOrEmpty(savesDir))
                throw new InvalidOperationException("Saves directory unavailable");
            if (!Directory.Exists(savesDir))
                Directory.CreateDirectory(savesDir);

            string fullPath = Path.Combine(savesDir, name + ".zxsav");
            if (Sync.FactionOwnershipTracker.IsInitialized && Sync.FactionOwnershipTracker.ActivePlayerCount >= 2)
                Sync.FactionOwnershipTracker.SaveToLevelScriptVariables();

            GameBridge.WriteSaveFile(fullPath);
            long size = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;
            return new Dictionary<string, object>
            {
                ["ok"] = size > 0,
                ["path"] = fullPath,
                ["sizeBytes"] = size
            };
        }

        private static string ErrorResponse(long id, string message)
        {
            var payload = new Dictionary<string, object> { ["id"] = id, ["ok"] = false, ["error"] = message ?? "unknown error" };
            return _json.Serialize(payload);
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static long GetLong(Dictionary<string, object> d, string key, long fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static float GetFloat(Dictionary<string, object> d, string key, float fallback = 0f)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static ulong GetULong(Dictionary<string, object> d, string key, ulong fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static ulong[] GetULongArray(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null)
                return Array.Empty<ulong>();

            if (v is IEnumerable en && !(v is string))
            {
                var list = new List<ulong>();
                foreach (object item in en)
                {
                    if (item == null) continue;
                    try { list.Add(Convert.ToUInt64(item, CultureInfo.InvariantCulture)); } catch { }
                }
                return list.ToArray();
            }

            try { return new[] { Convert.ToUInt64(v, CultureInfo.InvariantCulture) }; } catch { return Array.Empty<ulong>(); }
        }
    }
}
