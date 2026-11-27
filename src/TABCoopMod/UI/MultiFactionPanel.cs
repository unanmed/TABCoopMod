using System;
using System.Collections.Generic;
using System.Drawing;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Lightweight in-game Multi-Faction HUD panel (toggled via F7 or top-center badge):
    /// Tab 0: Ally Market Resource Transfer (20% fee, 0% when CoopMayor_ZeroTransferFee is active)
    /// Tab 1: Functional Wonder Two-Phase Crowdfunding (The Beholder, The Academy, The Lightning Spire)
    /// Attaches flat AreaInPixels DXTextObjects directly to DXIsometricGridObject.Current (matching
    /// PauseSyncController's proven screen-anchored pattern) with Z > 2147483600 so Fog of War never dims it.
    /// </summary>
    public static class MultiFactionPanel
    {
        public static bool IsOpen { get; private set; }
        public static int ActiveTab { get; private set; } // 0 = Market, 1 = Wonders
        public static FunctionalWonderType SelectedWonder { get; private set; } = FunctionalWonderType.Telescope;
        public static byte SelectedRecipientPlayer { get; private set; } = 2;

        private static readonly List<DXTextObject> _allPanelWidgets = new List<DXTextObject>();
        private static readonly List<DXTextObject> _marketWidgets = new List<DXTextObject>();
        private static readonly List<DXTextObject> _wonderWidgets = new List<DXTextObject>();

        private static DXTextObject _badgeBtn;
        private static DXTextObject _panelBg;
        private static DXTextObject _headerText;
        private static DXTextObject _btnTabMarket;
        private static DXTextObject _btnTabWonder;
        private static DXTextObject _btnClose;

        // Tab 0
        private static DXTextObject _marketStatusLabel;
        private static readonly DXTextObject[] _recipientButtons = new DXTextObject[3];
        private static readonly DXTextObject[] _resRowLabels = new DXTextObject[5];

        // Tab 1
        private static readonly DXTextObject[] _wonderSubTabs = new DXTextObject[3];
        private static DXTextObject _wonderPhaseLabel;
        private static readonly DXTextObject[] _wonderResLabels = new DXTextObject[5];
        private static DXTextObject _btnWithdrawAll;
        private static DXTextObject _wonderProgBg;
        private static DXTextObject _wonderProgFill;
        private static DXTextObject _wonderProgText;
        private static RectangleF _wonderProgFillFullRect;

        private static double _lastRefreshTicks = -1000.0;
        private static DXObject _attachedParent;
        private static float _lastScreenW;

        private static readonly Color BgDark = Color.FromArgb(242, 16, 22, 26);
        private static readonly Color BgRow = Color.FromArgb(236, 26, 36, 44);
        private static readonly Color BgBtn = Color.FromArgb(242, 38, 54, 66);
        private static readonly Color BorderGold = Color.Goldenrod;
        private static readonly Color BorderDim = Color.FromArgb(235, 86, 108, 120);

        public static void Toggle() => SetOpen(!IsOpen);

        public static void SetOpen(bool open, int? tab = null, FunctionalWonderType? wonder = null)
        {
            if (tab.HasValue) ActiveTab = tab.Value == 1 ? 1 : 0;
            if (wonder.HasValue) SelectedWonder = wonder.Value;
            IsOpen = open && CoopSessionManager.IsMultiFactionActive && CoopSessionManager.IsInSyncedLevel;
            if (IsOpen)
                EnsureDefaultRecipient();
            RefreshNow();
        }

        public static void OpenToWonderTab(FunctionalWonderType wonder) => SetOpen(true, tab: 1, wonder: wonder);

        public static void Reset()
        {
            IsOpen = false;
            ActiveTab = 0;
            _lastRefreshTicks = -1000.0;
            DestroyUi();
        }

        public static void Tick()
        {
            if (!CoopSessionManager.IsMultiFactionActive || !CoopSessionManager.IsInSyncedLevel
                || LockstepClock.LoadingLevel || GameBridge.CurrentLevelState == null)
            {
                if (_attachedParent != null) DestroyUi();
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRefreshTicks < 120.0 && now >= _lastRefreshTicks)
                return;
            _lastRefreshTicks = now;

            RefreshNow();
        }

        private static void EnsureDefaultRecipient()
        {
            byte me = CommandInterceptor.LocalPlayerId;
            if (SelectedRecipientPlayer >= 1 && SelectedRecipientPlayer <= 3
                && SelectedRecipientPlayer != me
                && FactionOwnershipTracker.IsSlotParticipating(SelectedRecipientPlayer)
                && !FactionOwnershipTracker.IsExiled(SelectedRecipientPlayer))
            {
                return;
            }
            for (byte p = 1; p <= 3; p++)
            {
                if (p != me && FactionOwnershipTracker.IsSlotParticipating(p) && !FactionOwnershipTracker.IsExiled(p))
                {
                    SelectedRecipientPlayer = p;
                    return;
                }
            }
        }

        private static void RefreshNow()
        {
            var grid = DXIsometricGridObject.Current;
            if (grid == null) return;

            float sw = DXScene.ScreenArea.Width > 0 ? DXScene.ScreenArea.Width : 1920f;
            LockstepClock.EnterVisualVfxContext();
            try
            {
                if (!object.ReferenceEquals(_attachedParent, grid) || Math.Abs(_lastScreenW - sw) > 4f)
                {
                    DestroyUi();
                    BuildUi(grid, sw);
                }

                if (_badgeBtn != null)
                {
                    _badgeBtn.Visible = true;
                    bool anyReady = false;
                    bool anyResearching = false;
                    int bestResearchPct = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        var ws = WonderCrowdfundManager.GetState((FunctionalWonderType)i);
                        if (ws == null) continue;
                        if (ws.Phase == WonderPhase.ReadyVoucher)
                            anyReady = true;
                        else if (ws.Phase == WonderPhase.Researching)
                        {
                            anyResearching = true;
                            int pct = (int)(ws.ResearchProgress01 * 100f);
                            if (pct > bestResearchPct) bestResearchPct = pct;
                        }
                    }
                    string badgeText = anyReady
                        ? "[F7] 盟友/奇观 (就绪!)"
                        : (anyResearching ? $"[F7] 奇观研究中 ({bestResearchPct}%)" : "[F7] 盟友贸易 & 奇观");
                    Color badgeFg = anyReady ? Color.LimeGreen : (anyResearching ? Color.DeepSkyBlue : Color.Gold);
                    Color badgeBorder = anyReady ? Color.LimeGreen : (anyResearching ? Color.DeepSkyBlue : BorderGold);
                    SetDynamicText(_badgeBtn, badgeText, badgeFg, badgeBorder, BgDark);
                }

                for (int i = 0; i < _allPanelWidgets.Count; i++)
                    _allPanelWidgets[i].Visible = IsOpen;
                for (int i = 0; i < _marketWidgets.Count; i++)
                    _marketWidgets[i].Visible = IsOpen && ActiveTab == 0;
                for (int i = 0; i < _wonderWidgets.Count; i++)
                    _wonderWidgets[i].Visible = IsOpen && ActiveTab == 1;

                if (!IsOpen) return;

                byte localPid = CommandInterceptor.LocalPlayerId;
                Color myColor = FactionOwnershipTracker.GetPlayerColor(localPid);
                string energyTag = FactionContextManager.SharedEnergyGridActive
                    ? $" | 共享电网 {FactionContextManager.SharedEnergyNeeded}/{FactionContextManager.SharedEnergySupply}"
                    : "";
                SetDynamicText(_headerText,
                    $"分基地合作面板 (你: P{localPid}{energyTag})",
                    myColor, null, null);

                SetDynamicText(_btnTabMarket, "1. 盟友贸易传输 (Market)",
                    ActiveTab == 0 ? Color.Gold : Color.Silver,
                    ActiveTab == 0 ? BorderGold : BorderDim, BgBtn);
                SetDynamicText(_btnTabWonder, "2. 功能奇观众筹 (Wonders)",
                    ActiveTab == 1 ? Color.Gold : Color.Silver,
                    ActiveTab == 1 ? BorderGold : BorderDim, BgBtn);

                if (ActiveTab == 0) RefreshMarketTab(localPid);
                else RefreshWonderTab(localPid);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[MultiFactionPanel] Refresh failed: {ex.Message}");
            }
            finally
            {
                LockstepClock.ExitVisualVfxContext();
            }
        }

        private static void RefreshMarketTab(byte localPid)
        {
            bool unlocked = FactionContextManager.HasAnyAliveMarket();
            int feePct = FactionContextManager.ZeroTransferFeeActive ? 0 : 20;
            SetDynamicText(_marketStatusLabel,
                unlocked
                    ? $"市场已连通 · 盟友传输税率: {feePct}% {(feePct == 0 ? "(零税市长生效)" : "(20% 常规税率)")}"
                    : "市场未解锁：队伍任一玩家建成【市场】后解锁盟友资源传输",
                unlocked ? (feePct == 0 ? Color.LightGreen : Color.Gold) : Color.OrangeRed,
                BorderDim, BgRow);

            EnsureDefaultRecipient();
            for (int i = 0; i < 3; i++)
            {
                byte pid = (byte)(i + 1);
                var btn = _recipientButtons[i];
                if (btn == null) continue;
                bool show = IsOpen && ActiveTab == 0 && FactionOwnershipTracker.IsSlotParticipating(pid) && pid != localPid;
                btn.Visible = show;
                if (!show) continue;
                bool exiled = FactionOwnershipTracker.IsExiled(pid);
                bool sel = SelectedRecipientPlayer == pid;
                SetDynamicText(btn,
                    exiled ? $"P{pid}(沦陷)" : $"发送至 P{pid}",
                    exiled ? Color.Gray : FactionOwnershipTracker.GetPlayerColor(pid),
                    sel ? BorderGold : BorderDim, BgBtn);
            }

            var myLedger = FactionContextManager.GetLedger(localPid);
            var dstLedger = FactionContextManager.GetLedger(SelectedRecipientPlayer);
            ResourceTransferKind[] kinds =
            {
                ResourceTransferKind.Gold, ResourceTransferKind.Wood, ResourceTransferKind.Stone,
                ResourceTransferKind.Iron, ResourceTransferKind.Oil
            };
            string[] names = { "金币 Gold", "木材 Wood", "石头 Stone", "铁矿 Iron", "石油 Oil" };
            for (int i = 0; i < 5; i++)
            {
                if (_resRowLabels[i] == null) continue;
                int mine = myLedger != null ? FactionContextManager.GetStockByResourceType(in myLedger.Economy, (int)kinds[i]) : 0;
                int theirs = dstLedger != null ? FactionContextManager.GetStockByResourceType(in dstLedger.Economy, (int)kinds[i]) : 0;
                SetDynamicText(_resRowLabels[i], $"{names[i]}:  你 {mine}  ->  P{SelectedRecipientPlayer} {theirs}",
                    Color.WhiteSmoke, BorderDim, BgRow);
            }
        }

        private static void RefreshWonderTab(byte localPid)
        {
            for (int i = 0; i < 3; i++)
            {
                var k = (FunctionalWonderType)i;
                var ws = WonderCrowdfundManager.GetState(k);
                string shortName = i == 0 ? "1.观星者望远镜" : (i == 1 ? "2.不朽者学院" : "3.闪电尖塔");
                string tag = "";
                if (ws != null)
                {
                    if (ws.Phase == WonderPhase.ResearchFunding && !WonderCrowdfundManager.HasRequiredWorkshopBuilt(k)) tag = " [锁]";
                    else if (ws.Phase == WonderPhase.Researching) tag = " [研究中]";
                    else if (ws.Phase == WonderPhase.ReadyVoucher) tag = " [就绪]";
                    else if (ws.Phase == WonderPhase.Completed) tag = " [完成]";
                }
                SetDynamicText(_wonderSubTabs[i], shortName + tag,
                    SelectedWonder == k ? Color.Gold : Color.WhiteSmoke,
                    SelectedWonder == k ? BorderGold : BorderDim, BgBtn);
            }

            var st = WonderCrowdfundManager.GetState(SelectedWonder);
            if (st == null) return;

            bool hasReqWorkshop = WonderCrowdfundManager.HasRequiredWorkshopBuilt(SelectedWonder);
            string reqWsName = WonderCrowdfundManager.GetRequiredWorkshopDisplayNameZh(SelectedWonder);

            string phaseTxt;
            Color phaseColor = Color.Gold;
            switch (st.Phase)
            {
                case WonderPhase.ResearchFunding:
                    if (!hasReqWorkshop)
                    {
                        phaseTxt = $"未解锁：需队伍任一玩家建成【{reqWsName}】方可开启研究众筹";
                        phaseColor = Color.OrangeRed;
                    }
                    else
                    {
                        phaseTxt = $"阶段 1/2：联合研究众筹 (已满足【{reqWsName}】，倍率 {(2 + WonderCrowdfundManager.PlayerCount - 1) * 0.5:0.0}x)";
                    }
                    break;
                case WonderPhase.Researching:
                    if (!hasReqWorkshop)
                    {
                        phaseTxt = $"研究暂停：队伍缺少【{reqWsName}】，重建后继续 ({(int)(st.ResearchProgress01 * 100f)}%)";
                        phaseColor = Color.OrangeRed;
                    }
                    else
                    {
                        phaseTxt = $"阶段 1/2：联合研究中... {(int)(st.ResearchProgress01 * 100f)}% (剩余 {st.ResearchRemainingSeconds}s，完成后全队解锁)";
                        phaseColor = Color.DeepSkyBlue;
                    }
                    break;
                case WonderPhase.BuildFunding:
                    phaseTxt = SelectedWonder == FunctionalWonderType.Spire
                        ? $"阶段 2/2：建造众筹 (供电 {WonderCrowdfundManager.GetScaledSpireEnergy(800)} + 全局共享电网)"
                        : "阶段 2/2：建造众筹 (集齐前可全额撤回个人出资)";
                    break;
                case WonderPhase.ReadyVoucher:
                    phaseTxt = "已集齐建造款！任一玩家放置该奇观即可免资源动工 (0成本凭证)";
                    phaseColor = Color.LimeGreen;
                    break;
                case WonderPhase.Building:
                    phaseTxt = $"施工中 (由 P{st.BuilderPlayerId} 承建，取消按出资比例退回)";
                    break;
                default:
                    phaseTxt = "全局已完成！全队共享该奇观加成";
                    phaseColor = Color.LightGreen;
                    break;
            }
            SetDynamicText(_wonderPhaseLabel, phaseTxt, phaseColor, BorderDim, BgRow);

            ResourceTransferKind[] kinds =
            {
                ResourceTransferKind.Gold, ResourceTransferKind.Wood, ResourceTransferKind.Stone,
                ResourceTransferKind.Iron, ResourceTransferKind.Oil
            };
            string[] names = { "金币 Gold", "木材 Wood", "石头 Stone", "铁矿 Iron", "石油 Oil" };
            var target = st.CurrentTarget;
            var contrib = st.CurrentContributed;
            var mine = st.CurrentPlayerContrib[localPid];
            for (int i = 0; i < 5; i++)
            {
                int tVal = target.Get(kinds[i]);
                int cVal = contrib.Get(kinds[i]);
                int mVal = mine.Get(kinds[i]);
                string txt = tVal > 0
                    ? $"{names[i]}: {cVal} / {tVal}   (你已投: {mVal})"
                    : $"{names[i]}: 无需投入 (0)";
                Color fg = (tVal > 0 && cVal >= tVal) ? Color.LightGreen : Color.WhiteSmoke;
                SetDynamicText(_wonderResLabels[i], txt, fg, BorderDim, BgRow);
            }

            if (_btnWithdrawAll != null)
            {
                bool canW = IsOpen && ActiveTab == 1 && st.IsCurrentPhaseFunding;
                _btnWithdrawAll.Visible = canW;
                if (canW)
                {
                    SetDynamicText(_btnWithdrawAll,
                        st.Phase == WonderPhase.ResearchFunding ? "撤回我当前阶段研究出资" : "撤回我当前阶段建造出资",
                        Color.Orange, BorderDim, Color.FromArgb(236, 72, 32, 32));
                }
            }

            bool showProg = IsOpen && ActiveTab == 1 && st.Phase == WonderPhase.Researching;
            if (_wonderProgBg != null) _wonderProgBg.Visible = showProg;
            if (_wonderProgText != null) _wonderProgText.Visible = showProg;
            if (_wonderProgFill != null)
            {
                if (showProg)
                {
                    float pct01 = st.ResearchProgress01;
                    float fillW = _wonderProgFillFullRect.Width * pct01;
                    _wonderProgFill.Visible = fillW >= 2f;
                    if (fillW >= 2f)
                    {
                        _wonderProgFill.State.Area = new RectangleF(
                            _wonderProgFillFullRect.X,
                            _wonderProgFillFullRect.Y,
                            fillW,
                            _wonderProgFillFullRect.Height);
                    }
                    int pctInt = (int)(pct01 * 100f);
                    string progLabel = hasReqWorkshop
                        ? $"联合研究进度：{pctInt}%   (剩余 {st.ResearchRemainingSeconds} 秒)"
                        : $"联合研究已暂停（需重建【{reqWsName}】）：{pctInt}%";
                    SetDynamicText(_wonderProgText, progLabel, hasReqWorkshop ? Color.White : Color.Orange, null, null);
                }
                else
                {
                    _wonderProgFill.Visible = false;
                }
            }
        }

        private static void BuildUi(DXObject grid, float sw)
        {
            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            _attachedParent = grid;
            _lastScreenW = sw;

            float bx = (sw - 208f * u) * 0.5f;
            _badgeBtn = MakeClickableBox(bx, 10f * u, 208f * u, 28f * u, 9.5f * u, BgDark, BorderGold, Color.Gold,
                "[F7] 盟友贸易 & 奇观", () => Toggle(), ZBase);
            grid.AddObject(_badgeBtn);

            float pw = 524f * u;
            float ph = 404f * u;
            float px = (sw - pw) * 0.5f;
            float py = 48f * u;

            _panelBg = AddPanelWidget(grid, _allPanelWidgets, MakeTextBox(px, py, pw, ph, 8f, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleCenter, BgDark, BorderGold, 2f, ZBase + 1));
            _panelBg.Text = " ";

            _headerText = AddPanelWidget(grid, _allPanelWidgets, MakeTextBox(px + 12f * u, py + 8f * u, pw - 56f * u, 26f * u, 11f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f, ZBase + 10));

            _btnClose = AddPanelWidget(grid, _allPanelWidgets, MakeClickableBox(px + pw - 36f * u, py + 6f * u, 28f * u, 24f * u, 10f * u, BgBtn, BorderDim, Color.Silver,
                "X", () => SetOpen(false), ZBase + 10));

            _btnTabMarket = AddPanelWidget(grid, _allPanelWidgets, MakeClickableBox(px + 12f * u, py + 38f * u, 244f * u, 28f * u, 10f * u, BgBtn, BorderGold, Color.Gold,
                "1. 盟友贸易传输 (Market)", () => { ActiveTab = 0; RefreshNow(); }, ZBase + 10));

            _btnTabWonder = AddPanelWidget(grid, _allPanelWidgets, MakeClickableBox(px + 268f * u, py + 38f * u, 244f * u, 28f * u, 10f * u, BgBtn, BorderDim, Color.Silver,
                "2. 功能奇观众筹 (Wonders)", () => { ActiveTab = 1; RefreshNow(); }, ZBase + 10));

            // Tab 0 widgets
            float ty = py + 72f * u;
            _marketStatusLabel = AddPanelWidget(grid, _marketWidgets, MakeTextBox(px + 12f * u, ty + 2f * u, pw - 24f * u, 26f * u, 9.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, BgRow, BorderDim, 1f, ZBase + 15));

            for (int i = 0; i < 3; i++)
            {
                byte pid = (byte)(i + 1);
                _recipientButtons[i] = AddPanelWidget(grid, _marketWidgets, MakeClickableBox(
                    px + 12f * u + i * 118f * u, ty + 34f * u, 110f * u, 26f * u, 9.5f * u, BgBtn, BorderDim, Color.White,
                    $"发送至 P{pid}", () => { SelectedRecipientPlayer = pid; RefreshNow(); }, ZBase + 20));
            }

            ResourceTransferKind[] rKinds =
            {
                ResourceTransferKind.Gold, ResourceTransferKind.Wood, ResourceTransferKind.Stone,
                ResourceTransferKind.Iron, ResourceTransferKind.Oil
            };
            int[] sendAmounts = { 50, 200, 500 };
            for (int row = 0; row < 5; row++)
            {
                var rKind = rKinds[row];
                float ry = ty + 68f * u + row * 46f * u;
                _resRowLabels[row] = AddPanelWidget(grid, _marketWidgets, MakeTextBox(px + 12f * u, ry, 258f * u, 38f * u, 9.5f * u, FontStyle.Bold, Color.WhiteSmoke,
                    ContentAlignment.MiddleLeft, BgRow, BorderDim, 1f, ZBase + 15));

                for (int b = 0; b < 3; b++)
                {
                    int amt = sendAmounts[b];
                    AddPanelWidget(grid, _marketWidgets, MakeClickableBox(
                        px + 276f * u + b * 76f * u, ry + 5f * u, 70f * u, 28f * u, 9f * u, BgBtn, BorderDim, Color.LightSkyBlue,
                        $"+{amt}", () => DispatchTransfer(rKind, amt), ZBase + 20));
                }
            }

            // Tab 1 widgets
            for (int i = 0; i < 3; i++)
            {
                var k = (FunctionalWonderType)i;
                _wonderSubTabs[i] = AddPanelWidget(grid, _wonderWidgets, MakeClickableBox(
                    px + 12f * u + i * 168f * u, ty + 2f * u, 162f * u, 26f * u, 9f * u, BgBtn, BorderDim, Color.White,
                    k.ToString(), () => { SelectedWonder = k; RefreshNow(); }, ZBase + 20));
            }

            _wonderPhaseLabel = AddPanelWidget(grid, _wonderWidgets, MakeTextBox(px + 12f * u, ty + 34f * u, pw - 24f * u, 28f * u, 9f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, BgRow, BorderDim, 1f, ZBase + 15));

            int[] goldAmounts = { 500, 1000 };
            int[] resourceAmounts = { 50, 100 };
            for (int row = 0; row < 5; row++)
            {
                var rKind = rKinds[row];
                int[] rowAmounts = rKind == ResourceTransferKind.Gold ? goldAmounts : resourceAmounts;
                float ry = ty + 68f * u + row * 40f * u;
                _wonderResLabels[row] = AddPanelWidget(grid, _wonderWidgets, MakeTextBox(px + 12f * u, ry, 240f * u, 34f * u, 9.5f * u, FontStyle.Bold, Color.WhiteSmoke,
                    ContentAlignment.MiddleLeft, BgRow, BorderDim, 1f, ZBase + 15));

                float btnX = px + 258f * u;
                for (int b = 0; b < 2; b++)
                {
                    int amt = rowAmounts[b];
                    float btnW = b == 0 ? 78f * u : 88f * u;
                    AddPanelWidget(grid, _wonderWidgets, MakeClickableBox(
                        btnX, ry + 3f * u, btnW, 28f * u, 9f * u, BgBtn, BorderDim, Color.Gold,
                        $"投+{amt}", () => DispatchWonderContribute(rKind, amt), ZBase + 20));
                    btnX += btnW + 6f * u;
                }
                AddPanelWidget(grid, _wonderWidgets, MakeClickableBox(
                    btnX, ry + 3f * u, pw - (btnX - px) - 12f * u, 28f * u, 9f * u, BgBtn, BorderDim, Color.LightGreen,
                    "投满", () => DispatchWonderContribute(rKind, 100000), ZBase + 20));
            }

            _btnWithdrawAll = AddPanelWidget(grid, _wonderWidgets, MakeClickableBox(
                px + 12f * u, ty + 276f * u, pw - 24f * u, 30f * u, 9.5f * u, Color.FromArgb(236, 72, 32, 32), BorderDim, Color.Orange,
                "撤回我当前阶段出资", DispatchWonderWithdrawAll, ZBase + 20));

            float progX = px + 12f * u;
            float progW = pw - 24f * u;
            float textY = ty + 268f * u;
            float textH = 22f * u;
            float barY = ty + 294f * u;
            float barH = 14f * u;

            _wonderProgText = AddPanelWidget(grid, _wonderWidgets, MakeTextBox(
                progX, textY, progW, textH, 9.5f * u, FontStyle.Bold, Color.White,
                ContentAlignment.MiddleCenter, BgRow, BorderDim, 1f, ZBase + 20));
            _wonderProgText.Text = "联合研究进度：0%   (剩余 45 秒)";

            _wonderProgBg = AddPanelWidget(grid, _wonderWidgets, MakeTextBox(
                progX, barY, progW, barH, 8f, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleCenter, Color.FromArgb(245, 14, 24, 34), Color.DeepSkyBlue, 1.5f, ZBase + 16));
            _wonderProgBg.Text = " ";

            _wonderProgFillFullRect = new RectangleF(progX + 2f * u, barY + 2f * u, progW - 4f * u, barH - 4f * u);
            _wonderProgFill = AddPanelWidget(grid, _wonderWidgets, MakeTextBox(
                _wonderProgFillFullRect.X, _wonderProgFillFullRect.Y, _wonderProgFillFullRect.Width, _wonderProgFillFullRect.Height,
                8f, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleCenter, Color.FromArgb(255, 42, 168, 224), Color.Transparent, 0f, ZBase + 18));
            _wonderProgFill.Text = " ";
        }

        private const int ZBase = 2147483580;

        private static DXTextObject AddPanelWidget(DXObject grid, List<DXTextObject> bucket, DXTextObject w)
        {
            w.Visible = false;
            bucket.Add(w);
            grid.AddObject(w);
            return w;
        }

        private static DXTextObject MakeTextBox(float x, float y, float w, float h, float fontSize, FontStyle style,
            Color fg, ContentAlignment align, Color bg, Color border, float borderW, int z)
        {
            var t = new DXTextObject { Z = z, InheritImageAttributes = false };
            t.State.AreaStyle = DXObjectState.TAreaStyle.AreaInPixels;
            t.State.Area = new RectangleF(x, y, w, h);
            t.TextRenderer.Font = new Font("Microsoft YaHei", Math.Max(8f, fontSize), style);
            t.TextRenderer.ColorText = fg;
            t.TextRenderer.TextAlign = align;
            t.TextRenderer.ColorBackground = bg;
            t.TextRenderer.ColorAreaBorder = border;
            t.TextRenderer.AreaBorderWidth = borderW;
            t.TextRenderer.MakeTransparent = bg == Color.Transparent;
            return t;
        }

        private static DXTextObject MakeClickableBox(float x, float y, float w, float h, float fontSize,
            Color bg, Color border, Color fg, string text, Action onClick, int z)
        {
            var t = MakeTextBox(x, y, w, h, fontSize, FontStyle.Bold, fg, ContentAlignment.MiddleCenter, bg, border, 1.5f, z);
            t.MouseClickInteractionEnabled = true;
            t.Text = text;
            t.MouseLeftUp += _ =>
            {
                try { onClick?.Invoke(); }
                catch (Exception ex) { ModLogger.Warn($"[MultiFactionPanel] Click error: {ex.Message}"); }
            };
            return t;
        }

        private static void SetDynamicText(DXTextObject obj, string text, Color? textColor, Color? borderColor, Color? bgColor)
        {
            if (obj == null) return;
            string desired = text ?? string.Empty;
            bool colorChanged =
                (textColor.HasValue && obj.TextRenderer.ColorText != textColor.Value) ||
                (borderColor.HasValue && obj.TextRenderer.ColorAreaBorder != borderColor.Value) ||
                (bgColor.HasValue && obj.TextRenderer.ColorBackground != bgColor.Value);
            bool textChanged = !string.Equals(obj.Text, desired, StringComparison.Ordinal);

            if (!colorChanged && !textChanged) return;

            if (textColor.HasValue) obj.TextRenderer.ColorText = textColor.Value;
            if (borderColor.HasValue) obj.TextRenderer.ColorAreaBorder = borderColor.Value;
            if (bgColor.HasValue)
            {
                obj.TextRenderer.ColorBackground = bgColor.Value;
                obj.TextRenderer.MakeTransparent = bgColor.Value == Color.Transparent;
            }

            if (!textChanged)
            {
                if (obj.Image != null) { try { obj.Image.Dispose(); } catch { } obj.Image = null; }
                obj.Text = desired == " " ? "  " : desired + " ";
            }

            if (obj.Image != null) { try { obj.Image.Dispose(); } catch { } obj.Image = null; }
            obj.Text = desired;
        }

        private static void DispatchTransfer(ResourceTransferKind res, int amount)
        {
            if (SelectedRecipientPlayer < 1 || SelectedRecipientPlayer > 3) return;
            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = NetCommandType.TransferResource,
                PlayerId = CommandInterceptor.LocalPlayerId,
                MayorOptionIndex = SelectedRecipientPlayer,
                QueueSlotIndex = (byte)res,
                CommandId = amount
            });
        }

        private static void DispatchWonderContribute(ResourceTransferKind res, int amount)
        {
            var st = WonderCrowdfundManager.GetState(SelectedWonder);
            if (st != null && st.Phase == WonderPhase.ResearchFunding && !WonderCrowdfundManager.HasRequiredWorkshopBuilt(SelectedWonder))
            {
                string wsName = WonderCrowdfundManager.GetRequiredWorkshopDisplayNameZh(SelectedWonder);
                CommandExecutor.ShowBannerOutsideDet($"需队伍任一玩家建成【{wsName}】后，方可投入该奇观研究", Color.OrangeRed, 2600);
                return;
            }
            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = NetCommandType.WonderContribute,
                PlayerId = CommandInterceptor.LocalPlayerId,
                MayorOptionIndex = (byte)SelectedWonder,
                QueueSlotIndex = (byte)res,
                CommandId = amount
            });
        }

        private static void DispatchWonderWithdrawAll()
        {
            var st = WonderCrowdfundManager.GetState(SelectedWonder);
            if (st == null || !st.IsCurrentPhaseFunding) return;
            byte me = CommandInterceptor.LocalPlayerId;
            var contrib = st.CurrentPlayerContrib[me];
            foreach (ResourceTransferKind res in Enum.GetValues(typeof(ResourceTransferKind)))
            {
                int amt = contrib.Get(res);
                if (amt > 0)
                {
                    CommandInterceptor.Dispatch(new NetCommand
                    {
                        Type = NetCommandType.WonderWithdraw,
                        PlayerId = me,
                        MayorOptionIndex = (byte)SelectedWonder,
                        QueueSlotIndex = (byte)res,
                        CommandId = amt
                    });
                }
            }
        }

        private static void DestroyUi()
        {
            try { _badgeBtn?.RemoveFromParent(); } catch { }
            _badgeBtn = null;
            for (int i = 0; i < _allPanelWidgets.Count; i++)
            {
                try { _allPanelWidgets[i]?.RemoveFromParent(); } catch { }
            }
            for (int i = 0; i < _marketWidgets.Count; i++)
            {
                try { _marketWidgets[i]?.RemoveFromParent(); } catch { }
            }
            for (int i = 0; i < _wonderWidgets.Count; i++)
            {
                try { _wonderWidgets[i]?.RemoveFromParent(); } catch { }
            }
            _allPanelWidgets.Clear();
            _marketWidgets.Clear();
            _wonderWidgets.Clear();
            _attachedParent = null;
        }
    }
}
