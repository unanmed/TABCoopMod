# package_release.ps1
# Builds TABCoopMod (Release) and packages a drag-and-drop archive into dist/
# containing ONLY runtime files + a plain-text user guide (no install scripts).

param(
    [string]$WorkspaceDir = "",
    [string]$Version = "v0.2.14"
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ([string]::IsNullOrWhiteSpace($WorkspaceDir)) {
    $WorkspaceDir = Split-Path -Parent $PSScriptRoot
}

Write-Host "[1/3] Building TABCoopMod (Release)..."
dotnet build "$WorkspaceDir\src\TABCoopMod\TABCoopMod.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed!"
}

$binDir = Join-Path $WorkspaceDir "src\TABCoopMod\bin\Release"
$distRoot = Join-Path $WorkspaceDir "dist"
$pkgName = "TABCoopMod-$Version"
$stageDir = Join-Path $distRoot $pkgName
$zipPath = Join-Path $distRoot "$pkgName.zip"

Write-Host "[2/3] Staging release files in $stageDir..."
if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item (Join-Path $binDir "TABCoopMod.dll") (Join-Path $stageDir "TABCoopMod.dll") -Force
Copy-Item (Join-Path $binDir "TABCoopMod.pdb") (Join-Path $stageDir "TABCoopMod.pdb") -Force
Copy-Item (Join-Path $binDir "0Harmony.dll") (Join-Path $stageDir "0Harmony.dll") -Force

[System.IO.File]::WriteAllText((Join-Path $stageDir "steam_appid.txt"), "644930")

$asmVersion = "0.2.14.0"
if ($Version -match '(\d+)\.(\d+)\.(\d+)') {
    $asmVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
}

$configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"/>
  </startup>
  <runtime>
    <appDomainManagerAssembly value="TABCoopMod, Version=$asmVersion, Culture=neutral, PublicKeyToken=null" />
    <appDomainManagerType value="TABCoopMod.Bootstrap.ModAppDomainManager" />
  </runtime>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $stageDir "TheyAreBillions.exe.config"), $configXml, [System.Text.Encoding]::UTF8)

$guideText = @"
====================================================================
  TABCoopMod ($Version) — 《亿万僵尸》2~3人合作联机 Mod 使用说明
====================================================================

【适用版本】
  仅支持 Steam 正版《They Are Billions》64位版 (v1.0.14.29)。
  所有联机玩家必须安装完全相同的 Mod 版本。


【一、安装方法（解压即用）】
  1. 完全关闭正在运行的《They Are Billions》游戏。
  2. 打开 Steam 客户端 -> 右键《They Are Billions》-> 管理 -> 浏览本地文件，
     打开游戏根目录（即包含 TheyAreBillions.exe 的文件夹）。
  3. 【强烈建议先备份】将游戏根目录中原有的 TheyAreBillions.exe.config
     复制一份命名为 TheyAreBillions.exe.config.bak（方便日后卸载还原）。
  4. 将本压缩包内的所有文件直接解压复制到游戏根目录，提示替换
     TheyAreBillions.exe.config 时选择“替换/覆盖”。

  解压后游戏根目录应包含以下新增/更新文件：
    - TABCoopMod.dll
    - TABCoopMod.pdb
    - 0Harmony.dll
    - steam_appid.txt
    - TheyAreBillions.exe.config   (已替换)
    - TABCoopMod_使用说明.txt


【二、如何开始联机（执政官共控模式）】
  1. 像平时一样从 Steam 启动《They Are Billions》。
  2. 在主菜单可以看到新增按钮【多人合作模式】（快捷键 K），点击进入等候大厅。
  3. Steam 好友联机：
     - 房主：点击【创建 Steam 房间】，进入等候房间后可点击邀请按钮或通过
       Steam 界面（Shift+Tab）邀请好友。
     - 客机：直接接受房主的 Steam 邀请即可自动加入（如果当前未启动游戏，
       可通过 Steam 邀请启动并自动接入）。
     - 自动直连加速：通过 Steam 大厅接头后，Mod 会自动尝试局域网/公网 IPv6/STUN UDP
       直连升级并在大厅顶部实时显示链路与 RTT；若双方网络受限无法直连，
       会自动通过隔离后的 Steam 多通道保底。
     - 同机双开本地测试：房主点击【创建本地测试房间】（127.0.0.1:27099），
       另一窗口点击【加入本地房间】即可。
  4. 房间准备与开局：
     - 客机点击【进入准备状态】后，房主即可点击【读取存档...】、【新建生存游戏...】
       或【自定义地图...】开启对局。
     - 房主可在房间右侧切换新局预设：【联机均衡】（2P/3P 扩图与资源/尸潮配平）
       或【原版】。
  5. 局内协同功能：
     - 2~3 名玩家共控同一基地与共享资源，但各自拥有独立视角、框选、编队与 UI。
     - 小地图实时显示队友彩色视野框（P1 蓝 / P2 绿 / P3 橙）。
     - 战术信号打点：按住 Alt + 鼠标左键 点击地面，可向队友同步发送地图信号。
     - 同步暂停：按空格或点击右上角暂停按钮可同步冻结对局（支持暂停期间微操），
       恢复时会有 3 秒顶居中倒计时提示。


【三、卸载方法】
  1. 关闭游戏。
  2. 删除游戏根目录下的以下文件：
       TABCoopMod.dll
       TABCoopMod.pdb
       0Harmony.dll
       steam_appid.txt
       TABCoopMod.log（如存在）
       TABCoopMod_Logs\ 目录（如存在）
       TABCoopMod_使用说明.txt
  3. 还原 TheyAreBillions.exe.config：
     - 若此前备份了 TheyAreBillions.exe.config.bak，删掉当前的
       TheyAreBillions.exe.config 并把 .bak 改回原名即可；
     - 若未备份，可在 Steam 中右键游戏 -> 属性 -> 已安装文件 ->
       “验证游戏文件的完整性”自动恢复原版文件，或将 TheyAreBillions.exe.config
       内容改回以下 3 行原版内容：

<?xml version="1.0" encoding="utf-8"?>
<configuration>
<startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"/></startup></configuration>


【四、常见问题与排查】
  1. 启动游戏后主菜单没有【多人合作模式】按钮：
     - 请确认解压目标目录正确（必须与 TheyAreBillions.exe 在同一层，
       不要多套一层子文件夹）。
     - 请确认 TheyAreBillions.exe.config 已被成功覆盖，且与 TABCoopMod.dll、
       0Harmony.dll 位于同一目录。
  2. 启动无反应或闪退：
     - 本 Mod 仅适配 64 位版本 TheyAreBillions.exe（不要运行 _x86.exe）。
     - 若发生异常，请查看游戏根目录生成的 TABCoopMod.log（以及 TABCoopMod_Logs\ 下按版本归档的历史日志）定位原因。
  3. 杀毒软件误报：
     - Mod 通过 .NET AppDomainManager 在内存中加载 Harmony 补丁（不会修改
       TheyAreBillions.exe 本体），如被拦截请将游戏目录加入白名单。
"@
[System.IO.File]::WriteAllText((Join-Path $stageDir "TABCoopMod_使用说明.txt"), $guideText, [System.Text.Encoding]::UTF8)

Write-Host "[3/3] Creating zip archive at $zipPath..."
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Release package created successfully!"
Get-ChildItem $stageDir | Select-Object Name, Length
Get-Item $zipPath | Select-Object FullName, Length
