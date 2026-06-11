$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$mods = @("BirthdayGiftReminder", "FishingAssist")

foreach ($mod in $mods) {
    $path = Join-Path $root $mod
    Write-Host "Building $mod..."
    Push-Location $path
    dotnet restore
    dotnet build -c Release
    Pop-Location
}

Write-Host ""
Write-Host "Done. Build outputs are usually here:"
foreach ($mod in $mods) {
    Write-Host "  $root\$mod\bin\Release\net6.0\$mod"
}
