param(
    [string]$OutputDirectory = "",
    [string]$Runtime = "win-x64",
    [string]$DotNetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "release"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$root = [System.IO.Path]::GetFullPath($root)
if ($OutputDirectory -eq $root -or $OutputDirectory.Length -lt 4) {
    throw "Refusing to publish over the project root or filesystem root."
}
$wrapperStage = Join-Path $OutputDirectory ".wrapper-stage"
$gatewayStage = Join-Path $OutputDirectory ".gateway-stage"

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Path $wrapperStage | Out-Null
New-Item -ItemType Directory -Path $gatewayStage | Out-Null

$common = @(
    "--configuration", "Release",
    "--runtime", $Runtime,
    "--no-restore",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:NuGetAudit=false",
    "-p:TreatWarningsAsErrors=false",
    "-p:WarningsNotAsErrors=NU1900"
)

& $DotNetPath publish (Join-Path $root "src\AerolinkManager.Wrapper\AerolinkManager.Wrapper.csproj") @common --output $wrapperStage
if ($LASTEXITCODE -ne 0) { throw "Wrapper publish failed with exit code $LASTEXITCODE." }

& $DotNetPath publish (Join-Path $root "src\ClaudeManager.Gateway\ClaudeManager.Gateway.csproj") @common --output $gatewayStage
if ($LASTEXITCODE -ne 0) { throw "Gateway publish failed with exit code $LASTEXITCODE." }

& $DotNetPath publish (Join-Path $root "src\AerolinkManager.App\AerolinkManager.App.csproj") @common --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $wrapperStage "ClaudeManager.Wrapper.exe") -Destination (Join-Path $OutputDirectory "ClaudeManager.Wrapper.exe") -Force
Remove-Item -LiteralPath $wrapperStage -Recurse -Force

Copy-Item -LiteralPath (Join-Path $gatewayStage "ClaudeManager.Gateway.exe") -Destination (Join-Path $OutputDirectory "ClaudeManager.Gateway.exe") -Force
Copy-Item -LiteralPath (Join-Path $gatewayStage "Microsoft.Data.Sqlite.dll") -Destination (Join-Path $OutputDirectory "Microsoft.Data.Sqlite.dll") -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $gatewayStage "SQLitePCLRaw.*") -Destination $OutputDirectory -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $gatewayStage -Recurse -Force

Get-ChildItem -LiteralPath $OutputDirectory -File -Filter "ClaudeManager.Wrapper.*" | Where-Object { $_.Name -ne "ClaudeManager.Wrapper.exe" } | Remove-Item -Force
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Extension -in @(".pdb", ".xml") } | Remove-Item -Force

$smokeHome = Join-Path $OutputDirectory ".smoke-data"
$previousHome = $env:CLAUDE_MANAGER_HOME
try {
    $env:CLAUDE_MANAGER_HOME = $smokeHome
    $smoke = Start-Process -FilePath (Join-Path $OutputDirectory "ClaudeManager.exe") -ArgumentList "--smoke-test" -WindowStyle Hidden -Wait -PassThru
    if ($smoke.ExitCode -ne 0) { throw "Published application smoke test failed with exit code $($smoke.ExitCode)." }
}
finally {
    if ($null -eq $previousHome) { Remove-Item Env:CLAUDE_MANAGER_HOME -ErrorAction SilentlyContinue } else { $env:CLAUDE_MANAGER_HOME = $previousHome }
    if (Test-Path -LiteralPath $smokeHome) { Remove-Item -LiteralPath $smokeHome -Recurse -Force }
}

Write-Host "Claude Manager release created:"
Write-Host "  $OutputDirectory\ClaudeManager.exe"
Write-Host "  $OutputDirectory\ClaudeManager.Wrapper.exe"
Write-Host "  $OutputDirectory\ClaudeManager.Gateway.exe"
