# 00 — 开发指南、工程铁律与踩坑手册 (Development Guide)

> **目标游戏版本**: `TheyAreBillions.exe` v1.0.14.29 (x64, .NET Framework 4.6)

本文档为所有人类开发者提供**工程规范与工具链手册**，规定必须遵守的铁律、构建部署方法及实机验证过的 CLR / HarmonyX 避坑要点。

---

## 1. 绝对工程铁律（Golden Rules — 严禁违反）

| # | 铁律 | 原因与约束说明 |
| :-: | :--- | :--- |
| **1** | **严禁修改磁盘上的 `TheyAreBillions.exe` 或 `DXVision.dll` 字节** | 内置 Eazfuscator.NET 完整性与调用栈校验（`#=qDfSFIK4oxaAg6Chf$Hd4YoX3S8bUcaBW2qfFmC44D9A=`）会读取 `AssemblyName`、`MetadataToken` 及 `StackTrace.GetFrame(3)`；任何静态改写都会导致密钥错误或崩溃。 |
| **2** | **只用运行时无损注入 + HarmonyX 内存补丁** | 通过修改 `TheyAreBillions.exe.config` 挂载 `.NET AppDomainManager`（`ModAppDomainManager.cs`），在 `TheyAreBillions` 加载后、`<Module>..cctor` 执行完毕的第一时间逐类挂载 Harmony 补丁。 |
| **3** | **跨程序集 `internal` 成员必须用反射/委托调用** | Desktop CLR 4.6 下 `IgnoresAccessChecksTo` 只在编译期生效（`Krafs.Publicizer`），运行时 JIT 对 `internal` 类型做直接 IL 调用、`castclass`/`isinst` 或泛型实例化（如 `e.HasComponent<CMovable>()`）会抛 `MethodAccessException` / `TypeAccessException`。必须改用 `AccessTools` + `MethodInfo.Invoke`、`object` 传参或 `GameBridge.HasComponent(entity, type)`。 |
| **4** | **`NetCommand` 必须自带 `ExecutorEntityIds`，禁止读本地 `CSelectable`** | 本地 `CSelectable.#=zIiHCgn_odqH7` 与摄像机 `CameraArea` 属于玩家本地状态。回放端必须通过 `ExecutorEntityIds` 定位实体执行，绝不能触碰本地选择集或本地鼠标输入。 |
| **5** | **任何改变世界状态的操作都必须走 `NetCommand`** | 禁止在本地逻辑中直接修改游戏世界；单机测试拦截开关（`F8` / `TABCOOP_INTERCEPT=1`）时也必须经过 `CommandInterceptor.Dispatch` → `CommandExecutor.Execute`。 |

---

## 2. 环境事实与工具链

| 环境项 | 实测值 / 路径 | 注意事项 |
| :--- | :--- | :--- |
| **操作系统 / Shell** | Windows 11 x64，PowerShell | 命令行脚本优先使用 PowerShell 语法（如 `Select-String`、`Stop-Process`、`Get-Content -Tail 50 -Wait`）。 |
| **.NET SDK** | `.NET SDK 10.0.100` | Mod 主工程目标框架 `net46` / `x64`（`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`）；MCP 服务端为 `net10.0`。 |
| **游戏存档目录** | `%USERPROFILE%\Documents\My Games\They Are Billions\Saves` | 存档由 `.zxsav`（加密 ZIP）+ `.zxcheck`（校验文本，与 `.zxsav` 文件名绑定，改名拷贝会导致 `ReadSaveFile` 报错无法加载）成对组成。**生存模式测试前必须将主档、`_Backup`、`_Crash` 及对应 `.zxcheck` 原名备份到 `Saves\` 目录外**（详见 §4 坑点）。 |
| **Mod 运行日志** | `<游戏目录>\TABCoopMod.log` 与 `TABCoopMod_Logs\` | `ModLogger` 路径基于 `typeof(ModLogger).Assembly.Location`；正常启动会自动在 `TABCoopMod_Logs/` 按版本号+时间戳归档（最多保留 30 份）；多开可通过环境变量 `TABCOOP_LOG_NAME` 分离日志。 |
| **依赖库说明** | `lib/`（由 `extract_sdk.ps1` 导出） | `TABCoopMod.csproj` 统一从 `lib/` 引用游戏与引擎程序集（`<Private>False</Private>`，不复制到游戏目录）；运行构建前请先执行 `scripts/extract_sdk.ps1`。 |

---

## 3. 常用构建、部署与逆向命令

### 3.1 构建与部署（每次部署前必须先关掉游戏进程）

`0Harmony.dll` 被运行中的游戏进程占用会导致 `Copy-Item` 抛 `IOException`（部署失败最常见原因）：

```powershell
# 1. 构建 Mod
dotnet build src/TABCoopMod/TABCoopMod.csproj -c Release

# 2. 强杀游戏进程
Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. 部署到游戏目录（自动复制 DLL、配置 AppDomainManager 并写入 steam_appid.txt=644930）
powershell -ExecutionPolicy Bypass -File scripts/install_mod.ps1

# 4. 打包玩家发行包（输出到 dist/TABCoopMod-vX.Y.Z.zip）
powershell -ExecutionPolicy Bypass -File scripts/package_release.ps1

# 5. 卸载恢复原版
powershell -ExecutionPolicy Bypass -File scripts/uninstall_mod.ps1
```

- 构建/启动健康判定标准（查看 `TABCoopMod.log`）：
  - `[GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing).`
  - `[Harmony] All Harmony patches applied. Classes OK=64, failed-tolerant=0.`
  - `[MainMenuPatch] Injected 'CO-OP MULTIPLAYER' button into TMenuCategory.Main successfully!`
- 若 `missing > 0`，立即停止并核对 `src/TABCoopMod/Core/ObfuscatedSymbols.cs`。
- **发版版本号 4 处同步铁律**：每次发版必须同步递增 [`ModInfo.cs`](../src/TABCoopMod/Core/ModInfo.cs)、[`scripts/install_mod.ps1`](../scripts/install_mod.ps1)、[`scripts/package_release.ps1`](../scripts/package_release.ps1) 与 [`docs/06_PROJECT_STATUS.md`](06_PROJECT_STATUS.md)（并同步更新 Steam 创意工坊双语标题，详见 `.agents/skills/tab-release-and-workshop/SKILL.md`）。

### 3.2 提取内嵌 SDK 与反编译游戏类

```powershell
# 首次或 lib/ 为空时，从主程序加密 ManifestResource 提取未混淆引擎 DLL 到 lib/
powershell -ExecutionPolicy Bypass -File scripts/extract_sdk.ps1

# 反编译 TheyAreBillions.exe 中的指定类（混淆名含 $/# 时务必用单引号）
powershell -ExecutionPolicy Bypass -File scripts/decompile_type.ps1 -TypeName 'ZX.GUI.OMiniMap'
powershell -ExecutionPolicy Bypass -File scripts/decompile_type.ps1 -TypeName '#=zxRcpu6e7NYzT7tGWqPjpOkc='

# 反编译 DXVision.dll 中的引擎类
powershell -ExecutionPolicy Bypass -File scripts/decompile_type.ps1 -AssemblyPath lib/DXVision.dll -TypeName 'DXVision.DXGame'
```

> 提示：反编译输出可能超过数千行，请配合 `Select-String` 或截取查看，避免撑爆上下文窗口。

---

## 4. 已踩过的 CLR / HarmonyX 深坑（切勿重蹈覆辙）

| 坑点 | 错误表现 / 症状 | 正确处理方式 |
| :--- | :--- | :--- |
| **过早访问主程序类型** | 在 `ModAppDomainManager.InitializeNewDomain` 直接引用 `ZX.Program` 导致 `0x8000FFFF` 崩溃 | 订阅 `AppDomain.CurrentDomain.AssemblyLoad`，等 `TheyAreBillions` 加载并运行 `RuntimeHelpers.RunModuleConstructor` 后再初始化 `ModEntry`。 |
| **`Harmony.PatchAll` 一损俱损** | 单类目标方法重写失败中断整个 Mod 加载 | `ModEntry.Initialize` 已改为逐类 `new PatchClassProcessor(...).Patch()` 包 `try/catch`，**绝对不要改回 `PatchAll`**。 |
| **`OMiniMap.OnMouseRightDown`** | 含 Eazfuscator 异常块，MonoMod IL 重写抛 `异常块的错误代码生成` | 不可直接 Patch 该方法，已改为在 `ZXSystemGameLevel` 与 `OMiniMap.OnRender` 侧处理。 |
| **`DXBehaviour<T>.get_GlobalRandom` 补丁失败** | `[ThreadStatic]` 静态泛型属性 getter 无法被 HarmonyX 补丁，抛 `HarmonyException` | 改用 `EntityUpdateBehaviourRandomPatch` 在逻辑线程内按“线程 + 载入代次”动态重置 `_GlobalRandom`。 |
| **直接 Hook `DXGame.set_Paused`** | 读档期间引擎内部频繁调用 `set_Paused`，拦截 `return false` 导致关卡载入硬退出 | **禁止 Hook `DXGame.set_Paused`**；暂停/倍速只允许从输入层（`ZXSystemGameLevel.OnKeyDown` / `OnKeyUp` + 按钮回调）拦截，且暂停快捷键必须通过 `GameBridge.IsKeyBoundToCommand` 查询玩家当前 `ZXKeyboardCommand.Pause` 绑定（禁止硬编码空格）。 |
| **局内快照重载丢失本地编队** | 局内 `Auto-resync` 替换 `ZXLevelState` 后本地 `1~8` 编队清空 | 仅在 `_hasEnteredSyncedLevelInMatch` 门控下的局内重同步前按确定性实体 ID 备份并在 `OnLevelLoadComplete` 还原且刷新左下角编队 HUD。 |
| **RyuJIT 内联短静态方法** | `ZXGame.#=zB3Vhdin5PerfrubjO4AblAA=()`（`IsMultiplayer`）仅 6~8 字节 IL，会被 x64 JIT 内联而绕过 Harmony Prefix | 直接在底层引擎接口（如 `DXWorldGrid.FindPathConcurrent`）实施拦截（`DeterministicPathfindingPatch`）。 |
| **混淆名标识符 `CS1040`** | 直接在 C# 中写 `#=z...` 会被当作预处理指令编译失败 | 全部混淆常量统一收敛至 `ObfuscatedSymbols.cs`，业务层通过 `GameBridge` 桥接调用。 |
| **直接运行 EXE 秒退** | `SteamAPI.RestartAppIfNecessary` 拉起 Steam 并杀掉当前进程 | `install_mod.ps1` 自动在游戏目录写 `steam_appid.txt`（内容 `644930`）。 |
| **生存存档测试后整套消失** | 用生存原档跑长时联机/战斗脚本一旦战败，主档、`_Backup`、`_Crash` 及 `.zxcheck` 全部被清空且云端同步抹除；改名拷贝进 `Saves\` 则无法加载 | 引擎铁人删档逻辑（`ZXGame.#=zvpunKuk5xh_4xlTRbw==`）会调用 `File.Delete`（绕过回收站）连删 6 个相关文件，Steam Cloud 随即 `Forget OK`，且 SSD TRIM 会快速清零释放簇；同时 `.zxcheck` 绑定原文件名，改名即报错。**测试前必须以原文件名将完整存档组备份至 `Saves\` 目录外（如 `D:\TAB_Saves_Backup\`）**，测试战败或结束后按原名还原，禁止裸跑用户生存原档。 |

---

## 5. 标准开发闭环 (SOP)

1. **读状态与设计**：动手前先确认 [`06_PROJECT_STATUS.md`](06_PROJECT_STATUS.md)、[`02_ARCHITECTURE_DESIGN.md`](02_ARCHITECTURE_DESIGN.md) 与 [`03_REVERSE_ENGINEERING_MAP.md`](03_REVERSE_ENGINEERING_MAP.md)。
2. **改前先读代码**：源码与反编译结论高度耦合，修改前务必使用 `read_file` 核对目标方法签名与现有守卫。
3. **编译部署查日志**：编译后杀进程部署，启动后第一时间核对 `0 missing` 与 `failed-tolerant=0`。
4. **门禁验证再收工**：涉及同步或确定性修改必须运行 [`07_TESTING_AND_MCP.md`](07_TESTING_AND_MCP.md) 中的自动化回归脚本（双进程回放 / Loopback 联机 / MCP 截图）取证；完成后同步更新 `03` / `05` / `06`。
