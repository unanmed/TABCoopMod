using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Networking;
using TABCoopMod.Sync;
using ZX;
using ZX.GUI;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Renders ally viewport rectangles on OMiniMap and handles Tactical Pings (Alt + Left Click).
    /// </summary>
    public static class AllyOverlayRenderer
    {
        private static FieldInfo _miniMapField;
        private static MethodInfo _pingCellMethod;
        private static readonly Dictionary<long, DXObject> _pendingBuildGhosts = new Dictionary<long, DXObject>();
        private static readonly object _ghostsSync = new object();

        // Pooled world-space faction foot-rings for mobile player units in Multi-Faction mode
        private static readonly Dictionary<byte, DXImage> _ringImages = new Dictionary<byte, DXImage>();
        private static readonly List<DXObject> _unitRingPool = new List<DXObject>();
        private static readonly List<KeyValuePair<ulong, byte>> _trackedOwnersBuffer = new List<KeyValuePair<ulong, byte>>(256);
        private static int _activeRingCount;
        private static double _lastRingUpdateMs = -1000.0;
        private static DXObject _ringParent;

        public static void SetPendingBuildGhost(long ghostId, Rectangle footprint, byte playerId)
        {
            if (ghostId <= 0 || footprint.Width <= 0 || footprint.Height <= 0) return;

            try
            {
                var middle = DXIsometricGridObject.Current?.IsometricMiddle;
                if (middle == null) return;

                Color color = GetPlayerColor(playerId);
                lock (_ghostsSync)
                {
                    if (_pendingBuildGhosts.TryGetValue(ghostId, out var existing) && existing != null)
                    {
                        try { existing.RemoveFromScene(); } catch { }
                    }

                    var ghost = new DXObject($"CoopPendingBuild_{ghostId}", DXImage.FromColor(Color.FromArgb(88, color)))
                    {
                        Area = new RectangleF(footprint.X, footprint.Y, footprint.Width, footprint.Height),
                        Z = 150,
                        InheritImageAttributes = false,
                        MouseClickInteractionEnabled = false,
                        Border = new DXShaderEffect_Border(
                            color,
                            3,
                            DXShaderEffect_Border.TBorderUnit.InPixels,
                            DXShaderEffect_Border.TBorderType.Frame)
                    };
                    _pendingBuildGhosts[ghostId] = ghost;
                    middle.AddObject(ghost);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[AllyOverlayRenderer] SetPendingBuildGhost failed: {ex.Message}");
            }
        }

        public static void RemovePendingBuildGhost(long ghostId)
        {
            if (ghostId <= 0) return;

            lock (_ghostsSync)
            {
                if (_pendingBuildGhosts.TryGetValue(ghostId, out var ghost))
                {
                    _pendingBuildGhosts.Remove(ghostId);
                    if (ghost != null)
                    {
                        try { ghost.RemoveFromScene(); } catch { }
                    }
                }
            }
        }

        public static void ClearPendingBuildGhosts()
        {
            lock (_ghostsSync)
            {
                foreach (var ghost in _pendingBuildGhosts.Values)
                {
                    if (ghost != null)
                    {
                        try { ghost.RemoveFromScene(); } catch { }
                    }
                }
                _pendingBuildGhosts.Clear();
            }
        }

        public static Color GetPlayerColor(byte playerId)
        {
            return CoopLimits.GetPlayerColor(playerId);
        }

        public static void TriggerMinimapAndGroundPing(byte playerId, Point cell, string label)
        {
            try
            {
                var levelSys = GameBridge.LevelSystemInstance;
                if (levelSys == null) return;

                Color color = GetPlayerColor(playerId);
                PointF worldPos = DXWorldGrid.PointFromCell_Center(cell);

                // Show ground indicator ring
                AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator)
                    ?.Invoke(levelSys, new object[] { worldPos, ZXCommandTargetIndicatorColor.Green, 1.5f });

                // Pulse minimap ping ring via OMiniMap.#=zvr$ku1Q=
                try
                {
                    if (_miniMapField == null && GameBridge.TypeZXSystemGameLevel != null)
                        _miniMapField = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_MiniMap);
                    object miniMap = _miniMapField?.GetValue(levelSys);
                    if (miniMap != null)
                    {
                        if (_pingCellMethod == null)
                            _pingCellMethod = AccessTools.Method(miniMap.GetType(), ObfuscatedSymbols.Method_OMiniMap_PingCell);
                        _pingCellMethod?.Invoke(miniMap, new object[] { cell, label ?? string.Empty, null, 2000 });
                    }
                }
                catch (Exception mmEx)
                {
                    ModLogger.Warn($"[AllyOverlayRenderer] Minimap ping failed: {mmEx.Message}");
                }

                // Show top banner alert
                GameBridge.ShowTopBanner($"[PING] {label} ({cell.X}, {cell.Y})", color, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[AllyOverlayRenderer] Error in TriggerMinimapAndGroundPing", ex);
            }
        }

        private static DXImage GetOrCreateFactionRingImage(byte playerId)
        {
            if (_ringImages.TryGetValue(playerId, out var img) && img != null)
                return img;

            Color c = GetPlayerColor(playerId);
            img = DXImage.FromFunction(() =>
            {
                var bmp = new Bitmap(48, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var shadowPen = new Pen(Color.FromArgb(200, 10, 12, 16), 5f))
                        g.DrawEllipse(shadowPen, 4f, 4f, 40f, 24f);
                    using (var pen = new Pen(Color.FromArgb(245, c), 3f))
                        g.DrawEllipse(pen, 4f, 4f, 40f, 24f);
                }
                return bmp;
            });
            _ringImages[playerId] = img;
            return img;
        }

        public static void ClearUnitFactionRings()
        {
            for (int i = 0; i < _unitRingPool.Count; i++)
            {
                var r = _unitRingPool[i];
                if (r != null)
                {
                    r.Visible = false;
                    try { r.RemoveFromScene(); } catch { }
                }
            }
            _unitRingPool.Clear();
            _activeRingCount = 0;
            _ringParent = null;
        }

        public static void UpdateUnitFactionRings()
        {
            if (!CoopSessionManager.IsMultiFactionActive
                || !CoopSessionManager.IsInSyncedLevel
                || !FactionOwnershipTracker.IsInitialized
                || LockstepClock.LoadingLevel)
            {
                if (_unitRingPool.Count > 0) ClearUnitFactionRings();
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRingUpdateMs < 50.0 && now >= _lastRingUpdateMs)
                return;
            _lastRingUpdateMs = now;

            var grid = DXIsometricGridObject.Current;
            var middle = grid?.IsometricMiddle;
            var game = DXGame.Current;
            if (middle == null || game == null) return;

            LockstepClock.EnterVisualVfxContext();
            try
            {
                if (!object.ReferenceEquals(_ringParent, middle))
                {
                    ClearUnitFactionRings();
                    _ringParent = middle;
                }

                RectangleF camScene = DXScene.Current != null ? DXScene.Current.CameraArea : RectangleF.Empty;
                if (!camScene.IsEmpty) camScene.Inflate(4f, 4f);

                FactionOwnershipTracker.CopyTrackedOwners(_trackedOwnersBuffer);
                Type movableType = GameBridge.TypeCMovable;
                int used = 0;
                const int MaxRings = 350;

                for (int i = 0; i < _trackedOwnersBuffer.Count && used < MaxRings; i++)
                {
                    var kv = _trackedOwnersBuffer[i];
                    byte owner = kv.Value;
                    if (owner < 1 || owner > 3) continue;

                    var ent = game.GetEntityByID(kv.Key);
                    if (ent == null || ent.Disposed) continue;

                    PointF pos = ent.Position;
                    PointF scenePos = DXWorldGrid.ScenePointFromWorldPoint(pos);
                    if (!camScene.IsEmpty && !camScene.Contains(scenePos)) continue;

                    if (!GameBridge.IsPlayerEntity(ent)) continue;
                    if (movableType != null && !GameBridge.HasComponent(ent, movableType)) continue;

                    DXObject ring;
                    if (used < _unitRingPool.Count)
                    {
                        ring = _unitRingPool[used];
                    }
                    else
                    {
                        ring = new DXObject
                        {
                            Z = 10,
                            InheritImageAttributes = false,
                            MouseClickInteractionEnabled = false
                        };
                        _unitRingPool.Add(ring);
                        middle.AddObject(ring);
                    }

                    ring.Image = GetOrCreateFactionRingImage(owner);
                    ring.Z = Math.Max(1, DXWorldGrid.GetZ(pos) - 10);
                    ring.Area = new RectangleF(pos.X - 0.58f, pos.Y - 0.36f, 1.16f, 0.72f);
                    ring.Visible = true;
                    used++;
                }

                for (int i = used; i < _activeRingCount && i < _unitRingPool.Count; i++)
                    _unitRingPool[i].Visible = false;
                _activeRingCount = used;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[AllyOverlayRenderer] UpdateUnitFactionRings failed: {ex.Message}");
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }
    }

    /// <summary>
    /// Captures Alt + Left Click on the world scene in an active co-op session and emits a
    /// synchronized tactical ping without disturbing local unit selection.
    /// </summary>
    [HarmonyPatch]
    public static class AllyTacticalPingInputPatch
    {
        private static FieldInfo _mouseCellField;

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnSceneLeftClick);
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!CoopSessionManager.IsSessionActive || !CoopSessionManager.IsInSyncedLevel) return true;
            if (LockstepClock.LoadingLevel || DXModalScreen.IsInModal) return true;

            var input = DXInputState.Current;
            if (input == null || !input.IsKeyPressed(DXKeys.Menu)) return true;

            object levelSys = GameBridge.LevelSystemInstance;
            if (levelSys == null) return true;

            if (_mouseCellField == null && GameBridge.TypeZXSystemGameLevel != null)
                _mouseCellField = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_MouseWorldCell);
            if (_mouseCellField == null) return true;

            Point cell = (Point)_mouseCellField.GetValue(levelSys);
            if (!DXWorldGrid.ExistCell(cell)) return true;

            CoopSessionManager.SendTacticalPing(cell);
            return false;
        }
    }

    /// <summary>
    /// Draws one colored viewport rectangle per ally on the minimap. Hooked on
    /// ZX.GUI.OMiniMap.OnRender (protected override); boxes are lazily created as children of the
    /// minimap and re-positioned each render from each ally's world-space camera area.
    /// </summary>
    [HarmonyPatch]
    public static class AllyMiniMapOverlayPatch
    {
        private static readonly Dictionary<byte, DXObject> _boxes = new Dictionary<byte, DXObject>();
        private static readonly object _boxesSync = new object();

        public static MethodBase TargetMethod()
        {
            var miniMapType = GameBridge.GameAssembly?.GetType("ZX.GUI.OMiniMap");
            return miniMapType == null ? null : AccessTools.Method(miniMapType, "OnRender", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix(DXObject __instance)
        {
            if (__instance == null || !CoopSessionManager.IsSessionActive || !CoopSessionManager.IsInSyncedLevel) return;
            if (DXLevel.Current == null) return;

            RectangleF playable = DXLevel.Current.PlayableArea;
            RectangleF inner = __instance.InnerArea;
            if (playable.Width <= 0f || playable.Height <= 0f || inner.Width <= 0f || inner.Height <= 0f) return;

            lock (_boxesSync)
            {
                foreach (var kv in _boxes)
                {
                    if (kv.Value != null && !CoopSessionManager.AllyStates.ContainsKey(kv.Key))
                        kv.Value.Visible = false;
                }

                foreach (var kv in CoopSessionManager.AllyStates)
                {
                    byte playerId = kv.Key;
                    var state = kv.Value;
                    if (state == null || state.CameraWorldArea.Width <= 0f) continue;

                    // Create a fresh overlay box whenever the minimap instance changes across level reloads;
                    // reusing a DXObject whose Parent points to a disposed old OMiniMap throws in AddObject.
                    if (!_boxes.TryGetValue(playerId, out var box) || box == null || !object.ReferenceEquals(box.Parent, __instance))
                    {
                        box = new DXObject("CoopAllyView" + playerId, DXImage.FromColor(Color.Transparent))
                        {
                            Z = 1001,
                            PixelAligned = true,
                            InheritImageAttributes = false,
                            Border = new DXShaderEffect_Border(
                                AllyOverlayRenderer.GetPlayerColor(playerId),
                                2,
                                DXShaderEffect_Border.TBorderUnit.InPixels,
                                DXShaderEffect_Border.TBorderType.Frame)
                        };
                        _boxes[playerId] = box;
                        __instance.AddObject(box);
                        ModLogger.Info($"[AllyOverlayRenderer] Created minimap viewport box for Player {playerId}");
                    }

                    RectangleF rect = Rectangle.Truncate(DXHelper_Rectangle.ChangeCoordinates(state.CameraWorldArea, playable, inner));
                    box.Area = rect;
                    box.Visible = true;
                }
            }
        }
    }
}
