$ErrorActionPreference = "Stop"

$steamConfigPath = "D:\app\steam\userdata\411671882\config\localconfig.vdf"
$backupPath = "D:\app\steam\userdata\411671882\config\localconfig.vdf.bak-codex-before-clear-launchoptions"
$logPath = "D:\gamescript\Clear-Stardew-Steam-LaunchOptions.log"

"[$(Get-Date -Format o)] Waiting for Steam and Stardew Valley to exit..." | Out-File -FilePath $logPath -Encoding UTF8

while (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -ieq "steam" -or $_.ProcessName -like "*Stardew*" }) {
    Start-Sleep -Seconds 2
}

Copy-Item -Path $steamConfigPath -Destination $backupPath -Force

$text = [System.IO.File]::ReadAllText($steamConfigPath)
$lines = $text -split "`r?`n", -1
$targetPath = @("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "413150")
$stack = New-Object System.Collections.Generic.List[string]
$pendingKey = $null
$output = New-Object System.Collections.Generic.List[string]
$removed = $false

function Test-PathEqual($stack, [string[]] $target) {
    if ($stack.Count -ne $target.Length) { return $false }
    for ($i = 0; $i -lt $target.Length; $i++) {
        if ($stack[$i] -ne $target[$i]) { return $false }
    }
    return $true
}

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]

    if ($line -match '^\s*"([^"]+)"\s*$') {
        $pendingKey = $Matches[1]
        $output.Add($line)
        continue
    }

    if ($line -match '^\s*\{\s*$') {
        if ($pendingKey -ne $null) {
            $stack.Add($pendingKey)
            $pendingKey = $null
        }
        $output.Add($line)
        continue
    }

    if ((Test-PathEqual $stack $targetPath) -and $line -match '^\s*"LaunchOptions"\s+') {
        $removed = $true
        continue
    }

    $output.Add($line)

    if ($line -match '^\s*\}\s*$') {
        if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
        $pendingKey = $null
        continue
    }

    if ($line -match '^\s*"([^"]+)"\s+".*"\s*$') {
        $pendingKey = $null
    }
}

[System.IO.File]::WriteAllText($steamConfigPath, [string]::Join("`r`n", $output), [System.Text.UTF8Encoding]::new($false))

"[$(Get-Date -Format o)] Removed LaunchOptions: $removed" | Add-Content -Path $logPath -Encoding UTF8
