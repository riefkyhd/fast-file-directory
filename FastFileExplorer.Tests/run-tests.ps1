param(
    [string]$Configuration = "Debug",
    [switch]$RunFlaUiE2E
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $scriptDir "FastFileExplorer.Tests.csproj"
$resultsDir = Join-Path $scriptDir "TestResults"

if (Test-Path $resultsDir) {
    Remove-Item $resultsDir -Recurse -Force
}

dotnet test $projectPath `
    -c $Configuration `
    --collect:"XPlat Code Coverage" `
    --results-directory $resultsDir

if ($RunFlaUiE2E) {
    $env:RUN_FLAUI_E2E = "1"
    dotnet test $projectPath `
        -c $Configuration `
        --filter "TestCategory=UI"
}

Write-Host "Coverage and test results are in: $resultsDir"
