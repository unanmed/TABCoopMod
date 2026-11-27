using System;
using System.Drawing;
using DXVision;
using DXVision.GUI;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Non-blocking HUD alert displayed mid-game when a teammate disconnects.
    /// Does not transfer ownership; allows Host to pause &amp; wait for Steam Join-Game
    /// reconnect, Save &amp; Exit cleanly, or dismiss the alert while keeping the slot reserved.
    /// </summary>
    public static class DisconnectNoticeOverlay
    {
        private const int ZBase = 2147483602;

        private static DXObject _attachedParent;
        private static DXTextObject _panelBg;
        private static DXTextObject _lblMessage;
        private static DXTextObject _btnPause;
        private static DXTextObject _btnSaveExit;
        private static DXTextObject _btnHide;
        private static float _lastUnit = -1f;
        private static double _lastRefreshTicks = -1000.0;
        private static bool _lastBuiltHost;

        public static bool IsVisible => _panelBg != null && _panelBg.Visible;

        public static void Reset()
        {
            Destroy();
            _lastUnit = -1f;
            _lastRefreshTicks = -1000.0;
        }

        public static void Tick()
        {
            bool shouldShow = CoopSessionManager.IsSessionActive
                && CoopSessionManager.HasDisconnectAlert
                && !CoopSessionManager.DisconnectAlertDismissed
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsGameLoading()
                && !GameBridge.IsLevelEnded()
                && GameBridge.CurrentLevelState != null;

            if (!shouldShow)
            {
                if (_panelBg != null) Destroy();
                return;
            }

            var grid = DXIsometricGridObject.Current;
            if (grid == null)
            {
                if (_panelBg != null) Destroy();
                return;
            }

            bool isHost = CoopSessionManager.Transport != null && CoopSessionManager.Transport.IsHost;
            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            bool needRebuild = _panelBg == null
                || !ReferenceEquals(_attachedParent, grid)
                || Math.Abs(_lastUnit - u) > 0.02f
                || _lastBuiltHost != isHost;

            double now = DXHelper_Environment.Ticks;
            if (!needRebuild && now - _lastRefreshTicks < 180.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            if (needRebuild)
            {
                Destroy();
                Build(grid, u, isHost);
            }

            RefreshTexts(isHost);
        }

        private static void Build(DXObject grid, float u, bool isHost)
        {
            LockstepClock.EnterVisualVfxContext();
            try
            {
                _attachedParent = grid;
                _lastUnit = u;
                _lastBuiltHost = isHost;

                float x = 10f * u;
                float y = 68f * u;
                float w = 468f * u;
                float h = (isHost ? 86f : 76f) * u;

                _panelBg = MakeBox(
                    x, y, w, h,
                    9f * u, FontStyle.Regular,
                    Color.Transparent, ContentAlignment.TopLeft,
                    Color.FromArgb(232, 34, 18, 16), Color.OrangeRed, 1.8f, ZBase,
                    clickable: false);
                _panelBg.Text = " ";
                grid.AddObject(_panelBg);

                _lblMessage = MakeBox(
                    x + 10f * u, y + 6f * u, w - 20f * u, 38f * u,
                    9f * u, FontStyle.Bold,
                    Color.FromArgb(255, 255, 210, 140), ContentAlignment.MiddleLeft,
                    Color.Transparent, Color.Transparent, 0f, ZBase + 1,
                    clickable: false);
                grid.AddObject(_lblMessage);

                if (isHost)
                {
                    float by = y + 48f * u;
                    float bh = 28f * u;

                    _btnPause = MakeClickableBox(
                        x + 10f * u, by, 152f * u, bh,
                        9f * u,
                        Color.FromArgb(235, 32, 72, 112), Color.DeepSkyBlue, Color.White,
                        "⏸ 暂停等待重连", ZBase + 2,
                        () => CoopSessionManager.TogglePauseForDisconnectWait());
                    grid.AddObject(_btnPause);

                    _btnSaveExit = MakeClickableBox(
                        x + 170f * u, by, 138f * u, bh,
                        9f * u,
                        Color.FromArgb(235, 112, 42, 32), Color.Coral, Color.White,
                        "保存并退出", ZBase + 2,
                        () => CoopSessionManager.HostSaveAndExitFromAlert());
                    grid.AddObject(_btnSaveExit);

                    _btnHide = MakeClickableBox(
                        x + 316f * u, by, 142f * u, bh,
                        9f * u,
                        Color.FromArgb(225, 42, 46, 54), Color.Gray, Color.LightGray,
                        "隐藏提示 (保留位置)", ZBase + 2,
                        () =>
                        {
                            CoopSessionManager.DismissDisconnectAlert();
                            Destroy();
                        });
                    grid.AddObject(_btnHide);
                }
                else
                {
                    _btnHide = MakeClickableBox(
                        x + w - 134f * u, y + 44f * u, 124f * u, 24f * u,
                        8.5f * u,
                        Color.FromArgb(225, 42, 46, 54), Color.Gray, Color.LightGray,
                        "隐藏提示", ZBase + 2,
                        () =>
                        {
                            CoopSessionManager.DismissDisconnectAlert();
                            Destroy();
                        });
                    grid.AddObject(_btnHide);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[DisconnectOverlay] Build failed: {ex.Message}");
                Destroy();
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void RefreshTexts(bool isHost)
        {
            if (_lblMessage == null) return;
            LockstepClock.EnterVisualVfxContext();
            try
            {
                byte slot = CoopSessionManager.DisconnectAlertSlot;
                string name = CoopSessionManager.DisconnectAlertPlayerName;
                if (string.IsNullOrEmpty(name)) name = $"Player {slot}";
                string colorTag = GetSlotColorName(slot);

                if (isHost)
                {
                    string msg = $"⚠ {name} (P{slot}·{colorTag}) 已断线（基地/部队保持原样）\n可暂停等待其通过 Steam「加入游戏」重连，或保存并退出：";
                    if (!string.Equals(_lblMessage.Text, msg, StringComparison.Ordinal))
                        _lblMessage.Text = msg;

                    if (_btnPause != null)
                    {
                        bool paused = LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
                        string pauseLabel = paused ? "▶ 继续游戏" : "⏸ 暂停等待重连";
                        Color border = paused ? Color.SpringGreen : Color.DeepSkyBlue;
                        Color bg = paused ? Color.FromArgb(235, 32, 92, 62) : Color.FromArgb(235, 32, 72, 112);
                        UpdateButtonVisual(_btnPause, pauseLabel, Color.White, bg, border);
                    }
                }
                else
                {
                    string msg = $"⚠ {name} (P{slot}·{colorTag}) 已断线（基地已保留）\n等待房主暂停等待其 Steam 重连或保存退出。";
                    if (!string.Equals(_lblMessage.Text, msg, StringComparison.Ordinal))
                        _lblMessage.Text = msg;
                }
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static string GetSlotColorName(byte slot)
        {
            switch (slot)
            {
                case 1: return "蓝";
                case 2: return "绿";
                case 3: return "橙";
                default: return "合作";
            }
        }

        private static void UpdateButtonVisual(DXTextObject btn, string text, Color fg, Color bg, Color border)
        {
            string desired = text ?? string.Empty;
            bool textChanged = !string.Equals(btn.Text, desired, StringComparison.Ordinal);
            bool colorChanged = btn.TextRenderer.ColorText != fg
                || btn.TextRenderer.ColorBackground != bg
                || btn.TextRenderer.ColorAreaBorder != border;
            if (!textChanged && !colorChanged) return;

            btn.TextRenderer.ColorText = fg;
            btn.TextRenderer.ColorBackground = bg;
            btn.TextRenderer.ColorAreaBorder = border;
            if (!textChanged)
            {
                if (btn.Image != null) { try { btn.Image.Dispose(); } catch { } btn.Image = null; }
                btn.Text = desired == " " ? "  " : desired + " ";
            }
            btn.Text = desired;
        }

        private static DXTextObject MakeBox(
            float x, float y, float w, float h,
            float fontSize, FontStyle style,
            Color fg, ContentAlignment align,
            Color bg, Color border, float borderW, int z,
            bool clickable)
        {
            var t = new DXTextObject
            {
                Z = z,
                InheritImageAttributes = false,
                MouseClickInteractionEnabled = clickable
            };
            t.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
            t.State.Area = new RectangleF(x, y, w, h);
            t.TextRenderer.Font = new Font("Microsoft YaHei", Math.Max(8f, fontSize), style);
            t.TextRenderer.TextAlign = align;
            t.TextRenderer.ColorText = fg;
            t.TextRenderer.ColorBackground = bg;
            t.TextRenderer.ColorAreaBorder = border;
            t.TextRenderer.AreaBorderWidth = borderW;
            t.TextRenderer.MakeTransparent = bg == Color.Transparent;
            t.Text = string.Empty;
            return t;
        }

        private static DXTextObject MakeClickableBox(
            float x, float y, float w, float h, float fontSize,
            Color bg, Color border, Color fg, string text, int z, Action onClick)
        {
            var t = MakeBox(x, y, w, h, fontSize, FontStyle.Bold, fg, ContentAlignment.MiddleCenter, bg, border, 1.5f, z, clickable: true);
            t.Text = text ?? string.Empty;
            t.MouseLeftUp += _ =>
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { ModLogger.Warn($"[DisconnectOverlay] Click failed: {ex.Message}"); }
            };
            return t;
        }

        private static void Destroy()
        {
            LockstepClock.EnterVisualVfxContext();
            try
            {
                TryRemove(ref _btnPause);
                TryRemove(ref _btnSaveExit);
                TryRemove(ref _btnHide);
                TryRemove(ref _lblMessage);
                TryRemove(ref _panelBg);
                _attachedParent = null;
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void TryRemove(ref DXTextObject obj)
        {
            if (obj == null) return;
            try { obj.RemoveFromParent(); } catch { }
            obj = null;
        }
    }
}
