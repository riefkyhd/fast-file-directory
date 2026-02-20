param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [string]$RuntimeInstallerPath,
    [string]$DotNetChannel = "9.0",
    [string]$IsccPath
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Resolve-Path (Join-Path $scriptDir "..\FastFileExplorer.csproj")
$publishDir = Join-Path $scriptDir "out\publish"
$issPath = Join-Path $scriptDir "FastFileExplorer.iss"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

$runtimeDir = Join-Path $scriptDir "out\runtime"
if (-not (Test-Path $runtimeDir)) {
    New-Item -ItemType Directory -Path $runtimeDir | Out-Null
}

if ($SelfContained.IsPresent) {
    Write-Warning "Ignoring -SelfContained. Installer is now framework-dependent by design."
}

Write-Host "Publishing application..."
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    -o $publishDir

$runtimeInstallerName = "windowsdesktop-runtime-$DotNetChannel-win-x64.exe"
$runtimeInstallerTarget = Join-Path $runtimeDir $runtimeInstallerName

if ($RuntimeInstallerPath) {
    if (-not (Test-Path $RuntimeInstallerPath)) {
        throw "Provided -RuntimeInstallerPath does not exist: $RuntimeInstallerPath"
    }
    Copy-Item -Path $RuntimeInstallerPath -Destination $runtimeInstallerTarget -Force
} elseif (-not (Test-Path $runtimeInstallerTarget)) {
    $runtimeUrl = "https://aka.ms/dotnet/$DotNetChannel/windowsdesktop-runtime-win-x64.exe"
    Write-Host "Downloading .NET Windows Desktop Runtime installer from $runtimeUrl ..."
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeInstallerTarget
}

$preferredExternalIcon = $null
$idemiaIcon = Join-Path $scriptDir "..\icon\idemia_new.ico"
$fallbackIcon = Join-Path $scriptDir "..\..\src\app\favicon.ico"

if ($preferredExternalIcon -and (Test-Path $preferredExternalIcon)) {
    $iconPath = (Resolve-Path $preferredExternalIcon).Path
} elseif (Test-Path $idemiaIcon) {
    $iconPath = (Resolve-Path $idemiaIcon).Path
} else {
    $iconPath = (Resolve-Path $fallbackIcon).Path
    Write-Warning "idemia_new.ico not found in icon path. Using fallback icon: $iconPath"
}

$resolvedIscc = $null
if ($IsccPath) {
    if (Test-Path $IsccPath) {
        $resolvedIscc = (Resolve-Path $IsccPath).Path
    } else {
        throw "Provided -IsccPath does not exist: $IsccPath"
    }
}

if (-not $resolvedIscc) {
    $candidatePaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidatePaths) {
        if (Test-Path $candidate) {
            $resolvedIscc = $candidate
            break
        }
    }
}

if (-not $resolvedIscc) {
    $appPathKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ISCC.exe"
    $appPath = (Get-ItemProperty -Path $appPathKey -ErrorAction SilentlyContinue)."(default)"
    if ($appPath -and (Test-Path $appPath)) {
        $resolvedIscc = $appPath
    }
}

if (-not $resolvedIscc) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($iscc) {
        $resolvedIscc = $iscc.Source
    }
}

if (-not $resolvedIscc) {
    throw "Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -IsccPath `"C:\Program Files (x86)\Inno Setup 6\ISCC.exe`"."
}

Write-Host "Building installer..."
Write-Host "Using ISCC: $resolvedIscc"
& $resolvedIscc "/DPublishDir=$publishDir" "/DAppIconFile=$iconPath" "/DDotNetRuntimeInstaller=$runtimeInstallerTarget" $issPath

Write-Host "Installer created in: $scriptDir\out\installer"
