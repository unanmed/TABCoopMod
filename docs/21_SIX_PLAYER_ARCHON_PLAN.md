# 21 — 6 人执政官模式支持规划（Phase A/B 已实施）

> **状态**: Phase A/B 已实施，等待 6 槽 UI 与 4~6P 实机验证
> **分支**: `feat/6p-archon`
> **目标**: 自用版本，**执政官（Archon）模式支持最多 6 人**；最大人数通过**一处常量**即可修改；独立基地（Multi-Faction）仍限制 3 人。
> **不在本次范围**: 4 人以上独立基地、4 人以上奇观众筹/多基地地图生成、Steam 创意工坊正式发布、官方兼容性承诺。

---

## 1. 背景与结论

作者限制 3 人主要考虑：

1. 设备性能；
2. 多基地模式地图空间不足。

本项目自用场景的取舍：

- **主要玩执政官模式**，所有玩家共享同一基地，感染者数量使用【原版纯净 / Vanilla】预设，不扩图、不加密度、不加波次。
- 独立基地模式很少玩，因此**不为 4 人以上做多基地适配**，超过 3 人时直接禁用/回退到执政官。
- 玩家操作量带来的单位增长可以接受；真正主导性能的是僵尸/实体数量，而 Vanilla 预设下该数量与原版一致。

因此本次目标明确为：

> **6 人执政官 + Vanilla 预设**，保留 2~3 人现有行为；多基地维持 3 人上限。

### 1.1 重同步预期

- `StateHasher` 每 **60 逻辑帧（约 2 秒）** 校验一次。
- 平时用 `PosDriftSync` 增量对齐（约 2 秒 / 8 秒），不是完整重同步。
- 只有检测到硬分叉才触发 `Auto-resync`；当前 2P 实测恢复约 **3 秒**。
- 健康局可能整局 0 次 `Auto-resync`。6 人时快照需要分别发给多个客机，恢复时间可能变长，需实测。

---

## 2. 设计原则

1. **单点配置**：新增 `CoopLimits.MaxPlayers`，默认 `6`；改这一行即可变成 4/5/6 人（UI/传输自动跟随）。7 人以上颜色会自动循环，但大厅布局还需要额外调整。
2. **执政官优先**：>3 人只允许 Archon；Multi-Faction 上限保持 `3`。
3. **不改确定性核心**：锁步、状态哈希、寻路、物理、RNG 隔离逻辑不动。
4. **复用现有传输**：Steam P2P、LAN TCP、Loopback 都通过 `StartHost(maxPlayers)` 参数化。
5. **保持 2P/3P 回归**：现有自动化门禁必须继续通过。
6. **Vanilla 为主**：>3 人推荐【原版纯净】预设；【联机均衡】在 >3 人下的行为需要明确（见 5.3）。

---

## 3. 现状盘点：硬编码 3 的位置

### 3.1 传输与会话

| 文件 / 符号 | 现状 | 影响 |
| :--- | :--- | :--- |
| `Sync/CoopSessionManager.cs` `StartHostSession` | `Transport.StartHost(3)` | 房间最多 3 人 |
| `Networking/INetworkTransport.cs` `StartHost` | 默认 `maxPlayers = 3` | 传输层默认上限 |
| `Networking/SteamP2PTransport.cs` | `_maxPlayers = 3`、`StartHost(int maxPlayers = 3)` | Steam 大厅上限 |
| `Networking/INetworkTransport.cs` `LocalTcpLoopbackTransport` | `_maxPlayers = 3` | LAN / 本地房间上限 |
| `Networking/ChaosTransport.cs` | `StartHost(int maxPlayers = 3)` | 透传默认值 |
| `Sync/CoopSessionManager.cs` `InputDelayTicks` | `1..4` | 6 人可能需要更大范围（见 5.6） |

### 3.2 大厅与 UI

| 文件 / 符号 | 现状 | 影响 |
| :--- | :--- | :--- |
| `UI/CoopLobbyWindow.cs` `SlotColors` | 3 种颜色 | P4~P6 无颜色 |
| `UI/CoopLobbyWindow.cs` `_slots` | `new PlayerSlotRow[3]` | 只显示 3 个槽位 |
| `UI/CoopLobbyWindow.cs` `BuildRoomView` | 循环 `i < 3` | 只创建 3 个槽位 |
| `UI/CoopLobbyWindow.cs` `RefreshRoomView` | 循环 `i < 3` | P4~P6 不显示 |
| `UI/AllyOverlayRenderer.cs` `GetPlayerColor` | 1/2/3 + 默认 Cyan | P4~P6 颜色重复 |
| `Sync/FactionOwnershipTracker.cs` `GetPlayerColor` | 1/2/3 | 多基地颜色（仅 3 人内） |
| `UI/MultiFactionPanel.cs` | 多处 `i < 3` / `Math.Min(3, ...)` | 多基地面板（保持 3 人，不改） |

### 3.3 仅多基地相关（本次不扩展，保持 3）

| 文件 / 符号 | 现状 | 处理 |
| :--- | :--- | :--- |
| `Sync/FactionContextManager.cs` | `FactionLedger[4]`、`GetLedger` 限 1..3、多处 `p <= 3` | 保持 3；>3 人禁用多基地 |
| `Sync/WonderCrowdfundManager.cs` | `CostBundle[4]`、`PlayerCount` 限 3、多处 `p <= 3` | 保持 3 |
| `Sync/FactionOwnershipTracker.cs` | `AssignInitialOwnershipFromLevel` 限 3 | 保持 3 |
| `Sync/CoopBalanceManager.cs` | `GetMultiFactionProfileForPlayerCount` 限 3 | 保持 3 |
| `Sync/MultiBaseMapGenerator.cs` | `playerCount` 限 3 | 保持 3 |
| `Commands/NetCommand.cs` | 多基地上下文切换 `PlayerId <= 3` | 改为 `<= MaxFactionPlayers` 并加账本空判 |
| `Testing/ControlServer.cs` | `faction_state` 限 3 | 保持 3（仅多基地测试用） |

### 3.4 平衡与测试

| 文件 / 符号 | 现状 | 处理 |
| :--- | :--- | :--- |
| `Sync/CoopBalanceManager.cs` `GetProfileForPlayerCount` | 只有 2P/3P 档案 | >3 人 Vanilla 直接 inactive；Balanced 需明确策略 |
| `Core/GameBridge.cs` `TestOverridePlayerCount` | 限 2..3 | 仅测试用；6P 全景图测试可后续扩展 |
| `scripts/verify_*.ps1` | 以 2P/3P 为主 | 新增 6P 冒烟脚本 |

---

## 4. 目标设计

### 4.1 单点最大人数配置

新增：

```csharp
// src/TABCoopMod/Core/CoopLimits.cs
public static class CoopLimits
{
    // 只需修改这一行即可改变最大联机人数。
    public const int MaxPlayers = 6;

    // 独立基地模式仍保持 3 人上限。
    public const int MaxFactionPlayers = 3;

    public static readonly Color[] PlayerColors =
    {
        Color.DodgerBlue,   // P1
        Color.SpringGreen,  // P2
        Color.Orange,       // P3
        Color.MediumPurple, // P4
        Color.Gold,         // P5
        Color.DeepPink      // P6
    };
}
```

原则：

- Archon 相关上限全部引用 `CoopLimits.MaxPlayers`。
- Multi-Faction 相关上限全部引用 `CoopLimits.MaxFactionPlayers`。
- UI 数组、槽位、颜色、传输默认值都从常量派生。
- 以后改人数只改 `MaxPlayers` 一行；UI 和传输自动跟随。

> `Color` 来自 `System.Drawing`，`CoopLimits` 放在 `Core` 命名空间即可被 UI/Sync/Networking 引用。

### 4.2 传输层

- `INetworkTransport.StartHost(int maxPlayers = CoopLimits.MaxPlayers)`。
- `SteamP2PTransport`、`LocalTcpLoopbackTransport` 的 `_maxPlayers` 默认值改为 `CoopLimits.MaxPlayers`。
- `CoopSessionManager.StartHostSession` 使用 `Transport.StartHost(CoopLimits.MaxPlayers)`。
- Steam 大厅 `CreateLobby(..., maxPlayers)` 已经是参数化，确认 6 人正常。
- 现有 `AllocateLowestFreePlayerId` 上限是 `min(255, maxPlayers)`，6 人无需改。
- LAN TCP 的 `StartHost` 同样按参数工作。

### 4.3 大厅 UI

- `_slots` 改为 `new PlayerSlotRow[CoopLimits.MaxPlayers]`。
- `SlotColors` 改为 `CoopLimits.PlayerColors`。
- `BuildRoomView` / `RefreshRoomView` 的 `i < 3` 改为 `i < CoopLimits.MaxPlayers`。
- 6 个槽位的布局：
  - 当前 `slotH = 50u`、`slotGap = 8u` 在 6 人时会超出 `bodyH`。
  - 建议 >3 人时使用紧凑布局：`slotH = 38u`、`slotGap = 6u`，6 槽总高约 `258u`，加上 banner 与底部面板仍能放进现有窗口。
  - 若视觉上仍拥挤，备选方案是 2 列 × 3 行，或加高窗口/滚动区域。
- 游戏模式下拉：
  - 有效人数 > 3 时禁用【独立基地 (M4)】或自动回退到【执政官共控】。
  - 显示提示：`独立基地最多 3 人，已回退到执政官模式`。
- 玩家颜色：
  - 大厅槽位、小地图队友视野框、单位脚底色环、Ping 颜色统一使用 `CoopLimits.PlayerColors`。
  - 保持 P1 蓝 / P2 绿 / P3 橙不变。

### 4.4 会话与指令

- `NetCommand` 的多基地上下文切换：
  - 由 `PlayerId <= 3` 改为 `PlayerId <= CoopLimits.MaxFactionPlayers`，并在 `GetLedger` 返回 null 时安全跳过。
  - Archon 模式下 P4~P6 不需要阵营上下文，保持现状即可。
- 暂停、倍速、市长、胜负结算：
  - 现有实现按 `PlayerId` 动态处理，理论上支持 N 人；需要 6P 实测。
  - `PauseSyncController` 的提示文本应能显示 `Player 4..6`。
- 队友视野框、Ping、颜色：
  - 使用统一颜色表，避免 P4~P6 落到默认色。

### 4.5 平衡预设与“指定增强人数”

用户确认：**不要抛弃 Balanced，而是允许 6 人在大厅手动选择按 2 人或 3 人档位增强**，以避免 6 人扩图/加怪带来的性能压力。

设计：

- 新增一个独立于“实际联机人数”的**平衡增强人数**参数：
  - `ActualPlayerCount`：当前房间实际人数（2~6）。
  - `BalanceEnhancementPlayers`：用于地图/初始兵力/工人/野怪/波次缩放的“增强档位”（2 或 3；0=自动）。
- 大厅增加下拉/选择：
  - `平衡增强: 自动 / 2人 / 3人`
  - `自动` 规则：实际人数 ≤3 时取实际人数；实际人数 >3 时取 **3人**（当前支持的最高档）。
  - 6 人房可选 `2人` 或 `3人` 增强。
- `CoopBalanceManager` 接口调整：
  - `GetProfileForPlayerCount(int actualPlayers, CoopBalancePreset preset, int balanceEnhancementPlayers)`
  - `Vanilla`：仍返回 `IsActive=false`，不扩图/不加怪。
  - `Balanced`：使用 `effective = balanceEnhancementPlayers > 0 ? balanceEnhancementPlayers : Math.Min(actualPlayers, 3)` 生成 profile；地图尺寸、额外单位/工人、`EarlySwarmScale/LateSwarmScale`、野怪密度均按 `effective` 取值。
  - profile 中保留 `ActualPlayerCount` 与 `BalanceScalePlayerCount` 两个字段，避免 UI/日志混淆。
- 独立基地模式：
  - 紫色/多基地仍限制 3 人；该模式下 `BalanceEnhancementPlayers` 强制等于实际人数，避免“3 人房只生成 2 个基地”的不一致。
- 推荐：
  - 6 人执政官默认 `3人增强`（比 2 人稍强，但远低于 6 人扩图）。
  - 如果 6 人仍觉得压力大，可手动选 `2人增强`。
  - 纯原版手感选 `Vanilla`。

### 4.6 性能与网络

- **网络优先级**：本自用版以 **LAN TCP / SakuraFRP** 为主；Steam P2P 只保持参数化兼容，不做 6 人专项调优/重点验证。
- **输入延迟**：`InputDelayTicks` 当前 `1..4`。6 人时可先保持，实测 `clientLag/tickLag` 后再决定是否扩到 `1..6`。
- **反压阈值**：现有 `5t/6t/9t` 阈值按 2~3 人调过；6 人需观察慢机影响，必要时调整。
- **快照扇出**：主机对每个客机分别发送 `.zxsav` 分片；6 人时上传总量约 5 份。开局/重同步时间会变长，需实测。
- **LAN TCP 发送**：当前 `LocalTcpLoopbackTransport` 是主线程同步写；6 人 + 大存档时可能卡一下。若实测明显，再加后台发送队列（作为 Phase D 可选优化）。
- **主机帧预算**：Vanilla 或低档增强下，主机 `[Perf] logic` 应保持 < 33ms；若接近上限，降低游戏倍速、选择更低增强档或减少玩家爆兵量。

---

## 5. 分阶段实施

### Phase A — 基础设施与 6 人大厅

- 新增 `Core/CoopLimits.cs`。
- 传输层默认值、`StartHostSession` 改为 `CoopLimits.MaxPlayers`。
- 大厅 6 槽位、6 颜色、紧凑布局。
- `AllyOverlayRenderer` / 槽位颜色统一。
- 构建 0 警告 0 错误。

### Phase B — 模式约束、平衡增强与会话细节

- >3 人禁用/回退 Multi-Faction。
- `NetCommand` 多基地范围改为 `MaxFactionPlayers` 并加空判。
- 实现“平衡增强人数”：
  - 大厅选择 `自动 / 2人 / 3人`；
  - `CoopBalanceManager` 增加 `BalanceEnhancementPlayers` 参数；
  - profile 分离 `ActualPlayerCount` 与 `BalanceScalePlayerCount`。
- 检查暂停/市长/结算/队友 UI 的 P4~P6 显示。

### Phase C — 自动化验证

- 2P/3P 回归：`run_replay_verify`、`verify_lobby_flow`、`verify_multifaction_*`。
- 4P/6P Loopback：开房、加入、准备、读档、暂停、建造、市长、结算。
- 6P LAN：真实多机或虚拟网卡，检查 `StateChecksum MATCH`、`0 DESYNC`、`0 ERROR`。
- 采集主机 `[Perf]`、`clientLag`、`tickLag`、快照耗时、重同步次数/时长。

### Phase D — 可选性能优化

- 若 6P 快照/发送卡顿明显：给 LAN TCP 增加后台发送队列。
- 若慢机拖累明显：调整 `InputDelayTicks` 上限或反压阈值。
- 若 UI 拥挤：改为 2 列布局或加高窗口。

---

## 6. 测试计划

### 6.1 构建与基础回归

- `dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release`：0 警告 0 错误。
- 启动日志：`GameBridge 0 missing`、`Harmony Classes OK=..., failed-tolerant=0`。
- 2P/3P：`1_Backup` 120t 回放 `identical`；`verify_lobby_flow` 通过；M1/M2/M3/M4/Survival 保持通过。

### 6.2 6P Loopback

- 1 个 Host + 5 个 Client，控制端口 `27901..27906`。
- 流程：`open_lobby` → `host_loopback` → 5×`join_loopback` → 全员 `toggle_ready` → `load_save`。
- 检查：
  - 大厅 6 个槽位与颜色正确；
  - `P1..P6` 都能显示、准备、操作；
  - `StateChecksum MATCH` 连续通过；
  - `0 DESYNC`、`0 ERROR`、`0 Auto-resync`（至少 10 分钟）。

### 6.3 6P LAN

- Host + 5 台机器/虚拟网卡，LAN TCP 或 SakuraFRP。
- 同样流程，额外检查：
  - 主机 `[Perf] logic` / `phys` / `coopTick`；
  - 各客机 `clientLag` / `tickLag`；
  - 开局快照总耗时；
  - 重同步次数与恢复时间；
  - Windows 防火墙 / frp TCP 映射。

### 6.4 UI 验收

- 6 槽位无重叠、无文字截断；
- P1~P6 颜色在大厅、小地图、单位色环一致；
- 游戏模式下拉在 >3 人时正确限制多基地；
- 暂停/恢复/市长/结算界面正常。

---

## 7. 验收标准

必须全部满足：

1. 修改 `CoopLimits.MaxPlayers` 一行，可在 4/5/6 人之间切换，UI 和传输随之变化。
2. 6P 执政官（Vanilla 或 2/3 人增强）：建房、加入、准备、开局、读档、暂停、建造、训练、市长、结算全流程可用。
3. 6P 连续运行 ≥10 分钟：`StateChecksum MATCH`、`0 DESYNC`、`0 ERROR`；允许偶发 `Auto-resync`，但需记录耗时。
4. 2P/3P 现有回归全部通过，行为不变。
5. Multi-Faction 在 >3 人时被阻止或自动回退 Archon。
6. 6 个玩家颜色在大厅/小地图/色环一致且可区分。
7. 主机帧耗在 Vanilla 或低档增强下保持可玩（目标 `logic < 33ms`，具体以实测为准）。
8. 若测试环境无法稳定开 6 个实例：至少完成 4/5 人自动化 + 6 人代码审查，最终以实玩反馈补齐。

---

## 8. 风险与未决问题

### 8.1 风险

- **漏改硬编码 3**：P4~P6 可能出现“能进房但指令/UI 不生效”的静默问题。需要全局 grep + 6P 实测覆盖。
- **大厅布局溢出**：6 槽位可能挤压 banner/chaos 面板，需要截图验收。
- **Steam P2P / DirectFastPath 多 Peer**：本自用版不重点支持；LAN TCP 路径已完成 6P 传输握手测试。
- **最慢客机**：6 人锁步等待概率更高，表现为输入延迟/追帧，不一定是 CPU 问题。
- **快照扇出**：6 人重同步/开局更慢。
- **6P 实机环境不足**：4/5 人可自动化验证；6 人最终以实际游玩反馈为准。

### 8.2 已确认决策

1. **>3 人只允许执政官**：Multi-Faction 上限保持 3；超过自动回退。
2. **保留 Balanced，支持指定增强人数**：6 人房可选 `自动 / 2人增强 / 3人增强`；地图、初始兵力、工人、野怪、波次按增强档位计算，实际人数独立。
3. **玩家颜色**：P1 蓝 / P2 绿 / P3 橙 / P4 紫 / P5 金 / P6 品红。
4. **Steam P2P 不重点支持**：只保持参数化兼容，主测 LAN / SakuraFRP。
5. **大厅采用紧凑 6 槽**：先压缩槽高；不够再改 2 列。
6. **LAN 改动已提交**：`master` 与 `feat/6p-archon` 都包含 LAN TCP 提交。
7. **验收人数**：优先 6 人；4/5 人顺带验证；6 人无环境时以代码审查 + 实玩反馈补齐。

---

## 9. 回滚

- 所有 6P 改动只在新分支 `feat/6p-archon`。
- 不改 `master`。
- 若实测不可接受，直接丢弃分支或回退到 2~3 人基线；LAN 自用改动可单独保留。

---

## 10. 实施进度

### 已完成（2026-11-26）

- **版本标识**
  - `ModInfo.Version` 改为 `0.2.8-6p`，`AssemblyVersion` 保持 `0.2.8.0`；避免与官方 0.2.8 混连。
- **Phase A**
  - 新增 `Core/CoopLimits.cs`：`MaxPlayers = 6`、`MaxFactionPlayers = 3`、6 色玩家调色板。
  - 传输层默认值与 `StartHostSession` 改为 `CoopLimits.MaxPlayers`。
  - 大厅 6 槽位、紧凑布局、动态颜色；本地/Steam 房间文本同步人数。
- **Phase B**
  - >3 人自动阻止/回退 Multi-Faction。
  - 新增“平衡增强档位”：`自动 / 2人 / 3人`，通过 `LobbyRosterSync` 同步。
  - `CoopBalanceManager` profile 分离 `ActualPlayerCount` 与 `BalanceScalePlayerCount`。
  - `NetCommand` 多基地上下文范围改为 `MaxFactionPlayers`。
- **验证**
  - Release 构建：**0 警告 0 错误**。
  - 6P LAN TCP 握手测试：Host + 5 Client，`P2..P6` 全部分配成功。
  - 平衡 profile 反射测试：6P + 3人增强 → `NCells=304 / scale=3`；6P + 2人增强 → `NCells=280 / scale=2`；6P Vanilla → inactive。

### 待完成

- 大厅 6 槽 UI 截图验收（需要实机/多实例）。
- 4P/5P/6P 实机 Loopback / LAN 全流程（准备、开局、读档、暂停、建造、市长、结算）。
- 长时间 `StateChecksum` / 重同步 / 帧耗采集。
- 可选：6P 快照扇出优化、LAN TCP 后台发送队列。

---

**下一步**：实机验证 6 槽 UI 与 4~6P 联机流程；发现问题再进入 Phase C/D。
