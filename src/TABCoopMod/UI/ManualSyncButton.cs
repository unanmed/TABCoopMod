using System;
using System.Drawing;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Small in-game HUD button for manual co-op resync.
    /// Host click -> export/broadcast a fresh snapshot.
    /// Client click -> ask Host to broadcast a fresh snapshot.
    /// The F9 hotkey mirrors this button.
    /// </summary>
    public static class ManualSyncButton
    {
        private const int Z = 2147483600;
        private static DXTextObject _button;
        private static DXObject _parent;
        private static float _lastUnit = -1f;
        private static double _lastRefreshTicks = -1000.0;

        public static void Tick()
        {
            bool visible = CoopSessionManager.IsSessionActive
                && CoopSessionManager.IsInSyncedLevel
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsGameLoading()
                && !GameBridge.IsLevelEnded()
                && (DXGame.Current?.IAIterationInt ?? 0) > 0
                && GameBridge.CurrentLevelState != null;

            if (!visible)
            {
                Destroy();
                return;
            }

            var grid = DXIsometricGridObject.Current;
            if (grid == null)
            {
                Destroy();
                return;
            }

            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            bool unitChanged = Math.Abs(_lastUnit - u) > 0.02f;

            double now = DXHelper_Environment.Ticks;
            if (!unitChanged && _button != null && ReferenceEquals(_parent, grid)
                && now - _lastRefreshTicks < 200.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            if (_button == null || !ReferenceEquals(_parent, grid) || unitChanged)
            {
                Destroy();
                Build(grid);
            }

            if (_button != null)
            {
                _button.Visible = true;
                UpdateArea();
            }
        }

        public static void Reset()
        {
            Destroy();
            _lastUnit = -1f;
            _lastRefreshTicks = -1000.0;
        }

        private static void Build(DXObject grid)
        {
            LockstepClock.EnterVisualVfxContext();
            try
            {
                _parent = grid;

                _button = new DXTextObject
                {
                    Z = Z,
                    InheritImageAttributes = false
                };
                _button.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
                float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
                _lastUnit = u;
                _button.TextRenderer.Font = new Font("Microsoft YaHei", Math.Max(8f, 9f * u), FontStyle.Bold);
                _button.TextRenderer.TextAlign = ContentAlignment.MiddleCenter;
                _button.TextRenderer.ColorText = Color.White;
                _button.TextRenderer.ColorBackground = Color.FromArgb(235, 24, 44, 66);
                _button.TextRenderer.ColorAreaBorder = Color.Cyan;
                _button.TextRenderer.AreaBorderWidth = 1.5f;
                _button.TextRenderer.MakeTransparent = false;
                _button.MouseClickInteractionEnabled = true;
                _button.MouseLeftUp += _ =>
                {
                    try
                    {
                        CoopSessionManager.RequestManualResync();
                    }
                    catch (Exception ex)
                    {
                        ModLogger.Warn($"[ManualSyncButton] Click failed: {ex.Message}");
                    }
                };
                _button.Text = "手动同步 (F9)";
                UpdateArea();
                grid.AddObject(_button);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ManualSyncButton] Build failed: {ex.Message}");
                _button = null;
                _parent = null;
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void UpdateArea()
        {
            if (_button == null) return;
            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            // Top-left, directly under the perf HUD (HUD is 24u tall at y=8u).
            _button.State.Area = new RectangleF(10f * u, 38f * u, 180f * u, 24f * u);
        }

        private static void Destroy()
        {
            if (_button != null)
            {
                try { _button.RemoveFromParent(); } catch { }
                _button = null;
            }
            _parent = null;
        }
    }
}
