param([int]$Port = 5187)
$ErrorActionPreference = 'Stop'
$serverRoot = Split-Path -Parent $PSScriptRoot
$dataFolder = Join-Path $serverRoot 'data'
New-Item -ItemType Directory -Path $dataFolder -Force | Out-Null
$keyFile = Join-Path $dataFolder 'dev-owner-key.txt'
if (-not (Test-Path -LiteralPath $keyFile)) {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [IO.File]::WriteAllText($keyFile, [Convert]::ToHexString($bytes))
}
$env:ENACTIVE_OWNER_KEY = [IO.File]::ReadAllText($keyFile).Trim()
$env:ENACTIVE_DATA = $dataFolder
$env:ASPNETCORE_ENVIRONMENT = 'Development'
Write-Host "Enactive Remote: http://127.0.0.1:$Port"
Write-Host "Local owner access key: $keyFile"
dotnet run --no-launch-profile --project (Join-Path $serverRoot 'Enactive.Server.csproj') -- --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE
