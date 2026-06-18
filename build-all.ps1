$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$mods = @(
    "AutoFish",
    "AutoSort",
    "AutoWalk",
    "BiggerBackpack",
    "BirthdayGiftReminder",
    "ChestHub",
    "FarmServant",
    "MineHelper",
    "NoTerminal"
)

foreach ($mod in $mods) {
    $path = Join-Path $root $mod
    Write-Host "Building $mod..."
    Push-Location $path
    try {
        dotnet restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for $mod with exit code $LASTEXITCODE."
        }

        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for $mod with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "Done. Build outputs are usually here:"
foreach ($mod in $mods) {
    Write-Host "  $root\$mod\bin\Release\net6.0\$mod"
}
