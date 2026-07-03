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

function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Command,
        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

if (-not $SkipDesktop) {
    $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    $solution = Join-Path $repositoryRoot 'LinkGallery.sln'
    Invoke-Native { & $dotnet restore $solution } 'Desktop restore failed'
    Invoke-Native {
        & $dotnet build $solution --configuration $Configuration --no-restore
    } 'Desktop build failed'
    Invoke-Native {
        & $dotnet test $solution --configuration $Configuration --no-build
    } 'Desktop tests failed'
}

if (-not $SkipAndroid) {
    $gradle = Join-Path $repositoryRoot 'android\gradlew.bat'
    if (-not (Test-Path -LiteralPath $gradle)) {
        throw 'Android Gradle Wrapper is missing.'
    }

    Invoke-Native {
        & $gradle --project-dir (Join-Path $repositoryRoot 'android') assembleDebug testDebugUnitTest
    } 'Android build or unit tests failed'
}
