$ErrorActionPreference = "Stop"

$sourceDir = "D:\gamescript\FishingAssist\bin\Release\net6.0"
$targetDir = "D:\app\steam\steamapps\common\Stardew Valley\Mods\FishingAssist"
$logPath = "D:\gamescript\FishingAssist-update.log"

"[$(Get-Date -Format o)] Waiting for Stardew/SMAPI to exit..." | Out-File -FilePath $logPath -Encoding UTF8

while (Get-Process | Where-Object { $_.ProcessName -like "*Stardew*" -or $_.ProcessName -like "*SMAPI*" }) {
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir "FishingAssist.dll") -Destination $targetDir -Force
Copy-Item -Path "D:\gamescript\FishingAssist\manifest.json" -Destination $targetDir -Force
Copy-Item -Path "D:\gamescript\FishingAssist\config.json" -Destination $targetDir -Force
Copy-Item -Path "D:\gamescript\FishingAssist\README.md" -Destination $targetDir -Force

"[$(Get-Date -Format o)] FishingAssist updated successfully." | Add-Content -Path $logPath -Encoding UTF8
