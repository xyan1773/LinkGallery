[CmdletBinding()]
param(
    [switch]$SkipDesktop,
    [switch]$SkipAndroid,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'dev-env.ps1') | Out-Host

if (-not $SkipDesktop) {
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') restore (Join-Path $repositoryRoot 'LinkGallery.sln')
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') build `
        (Join-Path $repositoryRoot 'LinkGallery.sln') `
        --configuration $Configuration `
        --no-restore
    & (Join-Path $env:DOTNET_ROOT 'dotnet.exe') test `
        (Join-Path $repositoryRoot 'LinkGallery.sln') `
        --configuration $Configuration `
        --no-build
}

if (-not $SkipAndroid) {
    $gradle = Join-Path $repositoryRoot 'android\gradlew.bat'
    if (-not (Test-Path -LiteralPath $gradle)) {
        throw 'Android Gradle Wrapper is missing.'
    }

    & $gradle --project-dir (Join-Path $repositoryRoot 'android') assembleDebug testDebugUnitTest
}
