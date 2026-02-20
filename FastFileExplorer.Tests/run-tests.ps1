param(
    [string]$Configuration = "Debug"
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

Write-Host "Coverage and test results are in: $resultsDir"
