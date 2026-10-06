param(
    [switch]$Live,
    [ValidateRange(1, 5000)][int]$MaxAuctions = 10
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$previousMode = $env:Source__Mode
$previousLimit = $env:Source__MaxAuctions
try {
    $env:Source__Mode = if ($Live) { 'Live' } else { 'Fixture' }
    $env:Source__MaxAuctions = $MaxAuctions.ToString()
    dotnet run --project (Join-Path $repo 'src/Fluxus.Api') --no-launch-profile -- --migrate --import-once
    if ($LASTEXITCODE -ne 0) { throw 'Import failed or completed with failed auctions. Inspect the import summary.' }
}
finally {
    $env:Source__Mode = $previousMode
    $env:Source__MaxAuctions = $previousLimit
}
