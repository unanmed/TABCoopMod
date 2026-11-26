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
        private static double _lastRefreshTicks = -1000.0;

        public static void Tick()
        {
            bool visible = CoopSessionManager.IsSessionActive
                && CoopSessionManager.IsInSyncedLevel
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsGameLoading()
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

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRefreshTicks < 200.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            float sw = DXScene.ScreenArea.Width > 0 ? DXScene.ScreenArea.Width : 1920f;
            if (_button == null || !ReferenceEquals(_parent, grid))
            {
                Destroy();
                Build(grid, sw);
            }

            if (_button != null)
            {
                _button.Visible = true;
                UpdateArea(sw);
            }
        }

        public static void Reset()
        {
            Destroy();
            _lastRefreshTicks = -1000.0;
        }

        private static void Build(DXObject grid, float sw)
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
                _button.TextRenderer.Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold);
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
                UpdateArea(sw);
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

        private static void UpdateArea(float screenWidth)
        {
            if (_button == null) return;
            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            float w = 190f * u;
            _button.State.Area = new RectangleF((screenWidth - w) * 0.5f, 44f * u, w, 28f * u);
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
