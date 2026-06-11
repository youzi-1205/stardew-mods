$ErrorActionPreference = 'Stop'

$source = 'D:\gamescript\FarmSuite\bin\Release\net6.0'
$target = 'D:\app\steam\steamapps\common\Stardew Valley\Mods\FarmSuite'
$log = 'D:\gamescript\FarmSuite-deploy-after-exit.log'

function Write-DeployLog([string]$message) {
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $log -Value "[$timestamp] $message" -Encoding UTF8
}

Write-DeployLog 'Waiting for Stardew Valley / SMAPI to exit before deploying FarmSuite.'
while (Get-Process StardewModdingAPI, StardewValley -ErrorAction SilentlyContinue) {
    Start-Sleep -Seconds 2
}

Start-Sleep -Seconds 1
New-Item -ItemType Directory -Force -Path $target | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $target 'assets') | Out-Null

Copy-Item -LiteralPath (Join-Path $source 'FarmSuite.dll') -Destination (Join-Path $target 'FarmSuite.dll') -Force
Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination (Join-Path $target 'manifest.json') -Force
Copy-Item -LiteralPath (Join-Path $source 'assets\helper.png') -Destination (Join-Path $target 'assets\helper.png') -Force
Copy-Item -LiteralPath (Join-Path $source 'assets\helper_portrait.png') -Destination (Join-Path $target 'assets\helper_portrait.png') -Force
Copy-Item -LiteralPath (Join-Path $source 'assets\truck.png') -Destination (Join-Path $target 'assets\truck.png') -Force

Write-DeployLog 'FarmSuite deployed successfully.'
