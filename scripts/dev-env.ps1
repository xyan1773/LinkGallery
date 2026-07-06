[CmdletBinding()]
param()

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotenvPath = Join-Path $repositoryRoot '.env'
if (Test-Path -LiteralPath $dotenvPath) {
    foreach ($line in Get-Content -LiteralPath $dotenvPath) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) {
            continue
        }

        $parts = $trimmed.Split('=', 2)
        if ($parts.Count -ne 2) {
            throw "Invalid .env entry: $trimmed"
        }

        $name = $parts[0].Trim()
        $value = $parts[1].Trim().Trim('"').Trim("'")
        if (-not [Environment]::GetEnvironmentVariable($name, 'Process')) {
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}

$dotnetRoot = if ($env:DOTNET_ROOT) {
    $env:DOTNET_ROOT
} elseif (Test-Path -LiteralPath 'E:\tools\dotnet8\dotnet.exe') {
    'E:\tools\dotnet8'
} else {
    Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
}

$javaHome = if ($env:JAVA_HOME) {
    $env:JAVA_HOME
} elseif (Test-Path -LiteralPath 'E:\coding_ide\android\jbr\bin\java.exe') {
    'E:\coding_ide\android\jbr'
} elseif (Test-Path -LiteralPath 'E:\tools\SDK26\bin\java.exe') {
    'E:\tools\SDK26'
} else {
    throw 'JDK not found. Set JAVA_HOME.'
}

$androidHome = if ($env:ANDROID_HOME) {
    $env:ANDROID_HOME
} elseif (Test-Path -LiteralPath 'E:\tools\android-sdk') {
    'E:\tools\android-sdk'
} else {
    throw 'Android SDK not found. Set ANDROID_HOME.'
}

$env:DOTNET_ROOT = $dotnetRoot
$env:JAVA_HOME = $javaHome
$env:ANDROID_HOME = $androidHome
$env:ANDROID_SDK_ROOT = $androidHome
$env:PATH = "$dotnetRoot;$javaHome\bin;$androidHome\platform-tools;$env:PATH"

[pscustomobject]@{
    DotnetRoot = $env:DOTNET_ROOT
    JavaHome = $env:JAVA_HOME
    AndroidHome = $env:ANDROID_HOME
}
