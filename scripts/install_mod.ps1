# install_mod.ps1
# Builds TABCoopMod in Release mode and deploys TABCoopMod.dll + 0Harmony.dll + AppDomainManager config
# to the They Are Billions game directory WITHOUT modifying TheyAreBillions.exe.

param(
    [string]$WorkspaceDir = "",
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
if ([string]::IsNullOrWhiteSpace($WorkspaceDir)) { $WorkspaceDir = Resolve-TabRepoRoot }
$GameDir = Resolve-TabGameDir $GameDir

Write-Host "[1/3] Building TABCoopMod (Release)..."
dotnet build "$WorkspaceDir\src\TABCoopMod\TABCoopMod.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed!"
}

$binDir = "$WorkspaceDir\src\TABCoopMod\bin\Release"
Write-Host "[2/3] Copying TABCoopMod.dll and 0Harmony.dll to $GameDir..."
Get-Process *TheyAreBillions* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Test-Path "$GameDir\TABCoopMod.dll") {
    try {
        Copy-Item "$binDir\TABCoopMod.dll" "$GameDir\TABCoopMod.dll" -Force
    } catch {
        Move-Item "$GameDir\TABCoopMod.dll" "$GameDir\TABCoopMod.dll.old" -Force -ErrorAction SilentlyContinue
        Copy-Item "$binDir\TABCoopMod.dll" "$GameDir\TABCoopMod.dll" -Force
    }
} else {
    Copy-Item "$binDir\TABCoopMod.dll" "$GameDir\TABCoopMod.dll" -Force
}
Copy-Item "$binDir\TABCoopMod.pdb" "$GameDir\TABCoopMod.pdb" -Force -ErrorAction SilentlyContinue
Copy-Item "$binDir\0Harmony.dll" "$GameDir\0Harmony.dll" -Force -ErrorAction SilentlyContinue
[System.IO.File]::WriteAllText("$GameDir\steam_appid.txt", "644930")

$configPath = "$GameDir\TheyAreBillions.exe.config"
$backupPath = "$GameDir\TheyAreBillions.exe.config.bak"

if (-not (Test-Path $backupPath)) {
    Write-Host "[3/3] Backing up original TheyAreBillions.exe.config to TheyAreBillions.exe.config.bak..."
    Copy-Item $configPath $backupPath -Force
}

Write-Host "[3/3] Updating TheyAreBillions.exe.config with ModAppDomainManager bootstrap..."
$configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.6"/>
  </startup>
  <runtime>
    <appDomainManagerAssembly value="TABCoopMod, Version=0.2.14.0, Culture=neutral, PublicKeyToken=null" />
    <appDomainManagerType value="TABCoopMod.Bootstrap.ModAppDomainManager" />
  </runtime>
</configuration>
"@
[System.IO.File]::WriteAllText($configPath, $configXml, [System.Text.Encoding]::UTF8)
Write-Host "TABCoopMod installed successfully!"
