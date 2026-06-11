$ErrorActionPreference = "Stop"

$steamConfigPath = "D:\app\steam\userdata\411671882\config\localconfig.vdf"
$launcherPath = "D:\gamescript\StardewModAssistant\publish\StardewModAssistant.exe"
$backupPath = "D:\app\steam\userdata\411671882\config\localconfig.vdf.bak-codex-before-ui-launcher"
$logPath = "D:\gamescript\Set-Steam-Launch-To-ModAssistant.log"

"[$(Get-Date -Format o)] Waiting for Steam and Stardew Valley to exit..." | Out-File -FilePath $logPath -Encoding UTF8

while (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -ieq "steam" -or $_.ProcessName -like "*Stardew*" }) {
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $launcherPath)) {
    throw "Launcher not found: $launcherPath"
}

Copy-Item -Path $steamConfigPath -Destination $backupPath -Force

$text = [System.IO.File]::ReadAllText($steamConfigPath)
$lines = $text -split "`r?`n", -1
$targetPath = @("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "413150")
$stack = New-Object System.Collections.Generic.List[string]
$pendingKey = $null
$output = New-Object System.Collections.Generic.List[string]
$launchLineWritten = $false
$escapedLauncherPath = $launcherPath.Replace("\", "\\")
$launchOptions = '\"' + $escapedLauncherPath + '\" %command%'

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

    if (Test-PathEqual $stack $targetPath) {
        if ($line -match '^\s*"LaunchOptions"\s+') {
            $indent = ([regex]::Match($line, '^\s*')).Value
            $output.Add($indent + '"LaunchOptions"' + "`t`t" + '"' + $launchOptions + '"')
            $launchLineWritten = $true
            continue
        }

        if ($line -match '^\s*\}\s*$' -and -not $launchLineWritten) {
            $indent = ([regex]::Match($line, '^\s*')).Value + "`t"
            $output.Add($indent + '"LaunchOptions"' + "`t`t" + '"' + $launchOptions + '"')
            $launchLineWritten = $true
        }
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

if (-not $launchLineWritten) {
    throw "Could not find Steam app 413150 block to add LaunchOptions."
}

[System.IO.File]::WriteAllText($steamConfigPath, [string]::Join("`r`n", $output), [System.Text.UTF8Encoding]::new($false))

"[$(Get-Date -Format o)] LaunchOptions set to $launchOptions" | Add-Content -Path $logPath -Encoding UTF8
