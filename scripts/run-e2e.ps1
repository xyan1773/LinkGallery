[CmdletBinding()]
param(
    [ValidateSet('Smoke', 'Experience', 'Scale', 'Soak', 'Physical')]
    [string]$Profile = 'Smoke',
    [string]$SourceMediaRoot = (
        'D:\' +
        [string][char]0x7CFB +
        [string][char]0x7EDF +
        [string][char]0x5BFC +
        [string][char]0x822A +
        '\Pictures\DJI_001'
    ),
    [string]$AvdName = 'Pixel_10_Pro_XL',
    [string]$AvdDevice = 'pixel_9_pro_xl',
    [string]$AvdHome = 'D:\AndroidAVD',
    [string]$DeviceSerial = 'emulator-5554',
    [string]$PhysicalAddress,
    [string]$OutputRoot,
    [switch]$RecreateAvd,
    [switch]$SkipBuild,
    [switch]$SkipMedia,
    [int]$SoakMinutes = 30
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1') | Out-Null

$adb = Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe'
$emulator = Join-Path $env:ANDROID_HOME 'SDK\emulator\emulator.exe'
$avdManager = Join-Path $env:ANDROID_HOME 'cmdline-tools\latest\bin\avdmanager.bat'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$packageName = 'com.linkgallery.companion'
$activityName = "$packageName/.MainActivity"
$port = 39570
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\e2e\$timestamp-$($Profile.ToLowerInvariant())"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$dataRoot = Join-Path $OutputRoot 'desktop-data'
$importRoot = Join-Path $OutputRoot 'imports'
$logcatPath = Join-Path $OutputRoot 'android-logcat.txt'
$manifestPath = Join-Path $OutputRoot 'media-manifest.json'

New-Item -ItemType Directory -Path $OutputRoot,$dataRoot,$importRoot -Force | Out-Null

function Invoke-Adb {
    param([string[]]$Arguments)
    & $adb -s $DeviceSerial @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "adb failed: $($Arguments -join ' ')"
    }
}

function Wait-AndroidBoot {
    $deadline = [DateTimeOffset]::Now.AddMinutes(5)
    do {
        Start-Sleep -Seconds 2
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        $state = & $adb -s $DeviceSerial get-state 2>$null
        $ErrorActionPreference = $previousPreference
        $booted = if ($state -eq 'device') {
            & $adb -s $DeviceSerial shell getprop sys.boot_completed 2>$null
        }
    } until (($state -eq 'device' -and "$booted".Trim() -eq '1') -or
        [DateTimeOffset]::Now -ge $deadline)
    if ([DateTimeOffset]::Now -ge $deadline) {
        throw "Android device $DeviceSerial did not boot in five minutes."
    }
}

function Recreate-TestAvd {
    if ($Profile -eq 'Physical') {
        throw 'Physical profile cannot recreate an AVD.'
    }
    if (-not (Test-Path -LiteralPath $avdManager)) {
        throw "avdmanager not found at $avdManager"
    }
    $connected = & $adb devices | Select-String -SimpleMatch "$DeviceSerial`tdevice"
    if ($connected) {
        & $adb -s $DeviceSerial emu kill | Out-Null
        Start-Sleep -Seconds 3
    }
    $legacyAvdHome = Join-Path $env:USERPROFILE '.android\avd'
    $legacyAvdIni = Join-Path $legacyAvdHome "$AvdName.ini"
    if (Test-Path -LiteralPath $legacyAvdIni) {
        $env:ANDROID_AVD_HOME = $null
        & $avdManager delete avd -n $AvdName | Out-Null
    }
    $AvdHome = [System.IO.Path]::GetFullPath($AvdHome)
    New-Item -ItemType Directory -Path $AvdHome -Force | Out-Null
    $env:ANDROID_AVD_HOME = $AvdHome
    $avdIni = Join-Path $AvdHome "$AvdName.ini"
    if (Test-Path -LiteralPath $avdIni) {
        & $avdManager delete avd -n $AvdName | Out-Null
    }
    'no' | & $avdManager create avd `
        -n $AvdName `
        -k 'system-images;android-36.1;google_apis;x86_64' `
        -d $AvdDevice `
        --force | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to recreate AVD $AvdName."
    }

    $avdDirectory = Join-Path $AvdHome "$AvdName.avd"
    $expectedRoot = [System.IO.Path]::GetFullPath($AvdHome)
    $resolvedAvd = [System.IO.Path]::GetFullPath($avdDirectory)
    if (-not $resolvedAvd.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to edit AVD outside $expectedRoot"
    }
    $configPath = Join-Path $resolvedAvd 'config.ini'
    $config = Get-Content -LiteralPath $configPath
    $config = $config | Where-Object { $_ -notlike 'disk.dataPartition.size=*' }
    $config = $config | ForEach-Object {
        if ($_ -like 'image.sysdir.1=SDK\*') {
            $_ -replace '^image\.sysdir\.1=SDK\\', 'image.sysdir.1='
        } else {
            $_
        }
    }
    $config += 'disk.dataPartition.size=100G'
    Set-Content -LiteralPath $configPath -Value $config -Encoding ASCII
}

function Start-TestAvd {
    if ($Profile -eq 'Physical') {
        return
    }
    $env:ANDROID_AVD_HOME = [System.IO.Path]::GetFullPath($AvdHome)
    $existing = & $adb devices | Select-String -SimpleMatch "$DeviceSerial`tdevice"
    if (-not $existing) {
        if (-not (Test-Path -LiteralPath $emulator)) {
            throw "Android emulator not found at $emulator"
        }
        $emulatorSdkRoot = Join-Path $env:ANDROID_HOME 'SDK'
        $env:ANDROID_HOME = $emulatorSdkRoot
        $env:ANDROID_SDK_ROOT = $emulatorSdkRoot
        $emulatorStdout = Join-Path $OutputRoot 'emulator-stdout.txt'
        $emulatorStderr = Join-Path $OutputRoot 'emulator-stderr.txt'
        Start-Process `
            -FilePath $emulator `
            -ArgumentList @(
                '-avd', $AvdName,
                '-no-window',
                '-no-audio',
                '-no-boot-anim',
                '-no-snapshot-load',
                '-gpu', 'swiftshader_indirect'
            ) `
            -RedirectStandardOutput $emulatorStdout `
            -RedirectStandardError $emulatorStderr `
            -WindowStyle Hidden | Out-Null
    }
    Wait-AndroidBoot
}

function Assert-EmulatorCapacity {
    if ($Profile -notin 'Experience') {
        return
    }
    $deadline = [DateTimeOffset]::Now.AddMinutes(2)
    do {
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        $lines = & $adb -s $DeviceSerial shell df -Pk /storage/emulated/0 2>$null
        $dfExitCode = $LASTEXITCODE
        $ErrorActionPreference = $previousPreference
        if ($dfExitCode -eq 0) {
            $line = $lines | Select-Object -Last 1
            break
        }
        Start-Sleep -Seconds 2
    } until ([DateTimeOffset]::Now -ge $deadline)
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Emulator external storage did not become available in two minutes.'
    }
    $columns = "$line" -split '\s+'
    if ($columns.Count -lt 4) {
        throw "Unable to parse emulator capacity: $line"
    }
    $availableGb = [math]::Round(([double]$columns[3] * 1024) / 1GB, 1)
    if ($availableGb -lt 80) {
        throw "Emulator has only $availableGb GB free; at least 80 GB is required."
    }
}

function Get-ExperienceFiles {
    $all = Get-ChildItem -LiteralPath $SourceMediaRoot -File
    $photos = @($all | Where-Object Extension -IEq '.JPG')
    $videos = @($all | Where-Object {
        $_.Extension -ieq '.MP4' -and $_.Length -le 2.5GB
    })
    foreach ($targetGb in 5, 7, 10, 13, 16) {
        $candidate = $all |
            Where-Object Extension -IEq '.MP4' |
            Sort-Object { [math]::Abs(($_.Length / 1GB) - $targetGb) } |
            Select-Object -First 1
        if ($null -ne $candidate -and $candidate.FullName -notin $videos.FullName) {
            $videos += $candidate
        }
    }
    $proxies = @($all | Where-Object Extension -IEq '.LRF' | Sort-Object Length)
    $selectedProxies = if ($proxies.Count -gt 2) {
        @($proxies[0], $proxies[[math]::Floor($proxies.Count / 2)], $proxies[-1])
    } else {
        $proxies
    }
    @($photos + $videos + $selectedProxies)
}

function Push-MediaFile {
    param([System.IO.FileInfo]$File, [string]$RemoteDirectory)
    Invoke-Adb -Arguments @('shell','mkdir','-p',$RemoteDirectory) | Out-Null
    Invoke-Adb -Arguments @('push',$File.FullName,"$RemoteDirectory/$($File.Name)") |
        Out-Host
}

function Scan-Media {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    & $adb -s $DeviceSerial shell cmd media_provider scan_volume external_primary 2>$null |
        Out-Host
    $scanExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    if ($scanExitCode -ne 0) {
        Invoke-Adb -Arguments @(
            'shell','content','call',
            '--uri','content://media',
            '--method','scan_volume',
            '--arg','external_primary'
        ) | Out-Host
    }
}

function Prepare-Media {
    if ($Profile -eq 'Physical' -or $SkipMedia) {
        return
    }
    if (-not (Test-Path -LiteralPath $SourceMediaRoot -PathType Container)) {
        throw "Source media directory not found: $SourceMediaRoot"
    }

    Invoke-Adb -Arguments @(
        'shell','rm','-rf','/sdcard/DCIM/LinkGalleryE2E'
    ) | Out-Null
    Invoke-Adb -Arguments @(
        'shell','mkdir','-p','/sdcard/DCIM/LinkGalleryE2E'
    ) | Out-Null
    $files = switch ($Profile) {
        'Smoke' {
            @(
                Get-ChildItem -LiteralPath $SourceMediaRoot -File -Filter '*.JPG' |
                    Sort-Object Length |
                    Select-Object -First 1
                Get-ChildItem -LiteralPath $SourceMediaRoot -File -Filter '*.MP4' |
                    Sort-Object Length |
                    Select-Object -First 1
            )
        }
        'Scale' {
            @(
                Get-ChildItem -LiteralPath $SourceMediaRoot -File -Filter '*.JPG' |
                    Sort-Object Length |
                    Select-Object -First 1
                Get-ChildItem -LiteralPath $SourceMediaRoot -File -Filter '*.MP4' |
                    Sort-Object Length |
                    Select-Object -First 1
            )
        }
        default { Get-ExperienceFiles }
    }
    $files = @($files | Where-Object { $null -ne $_ })
    $files | Select-Object Name,Length,LastWriteTimeUtc,FullName |
        ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $manifestPath -Encoding UTF8
    foreach ($file in $files) {
        Push-MediaFile $file '/sdcard/DCIM/LinkGalleryE2E'
    }

    if ($Profile -eq 'Scale') {
        $seed = ($files | Where-Object Extension -IEq '.JPG' | Select-Object -First 1).Name
        $seedScript = Join-Path $PSScriptRoot 'e2e-scale-seed.sh'
        Invoke-Adb -Arguments @(
            'push',$seedScript,'/data/local/tmp/linkgallery-scale-seed.sh'
        ) | Out-Host
        Invoke-Adb -Arguments @(
            'shell','sh','/data/local/tmp/linkgallery-scale-seed.sh',$seed
        ) | Out-Host
    }
    Scan-Media
}

function Build-TestTargets {
    if ($SkipBuild) {
        return
    }
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw 'Project build or unit tests failed.'
    }
    & $dotnet build `
        (Join-Path $repositoryRoot 'e2e\LinkGallery.E2E\LinkGallery.E2E.csproj') `
        --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw 'E2E harness build failed.'
    }
}

function Start-AndroidApp {
    $apk = Join-Path $repositoryRoot 'android\app\build\outputs\apk\debug\app-debug.apk'
    Invoke-Adb -Arguments @('install','-r',$apk) | Out-Host
    Invoke-Adb -Arguments @('shell','pm','clear',$packageName) | Out-Host
    Invoke-Adb -Arguments @(
        'shell','pm','grant',$packageName,'android.permission.READ_MEDIA_IMAGES'
    ) | Out-Null
    Invoke-Adb -Arguments @(
        'shell','pm','grant',$packageName,'android.permission.READ_MEDIA_VIDEO'
    ) | Out-Null
    Invoke-Adb -Arguments @(
        'shell','pm','grant',$packageName,'android.permission.ACCESS_MEDIA_LOCATION'
    ) | Out-Null
    Invoke-Adb -Arguments @('shell','am','start','-W','-n',$activityName) | Out-Host
}

function Wait-Api {
    param([string]$Address)
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    do {
        try {
            return Invoke-RestMethod "http://$Address/api/v1/device" -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 250
        }
    } until ([DateTimeOffset]::Now -ge $deadline)
    throw "Device API at $Address did not become ready."
}

function Wait-MediaCount {
    param([string]$Address, [int]$Minimum)
    $deadline = [DateTimeOffset]::Now.AddMinutes(3)
    do {
        $device = Wait-Api $Address
        if ($device.mediaCount -ge $Minimum) {
            return $device
        }
        Start-Sleep -Seconds 2
    } until ([DateTimeOffset]::Now -ge $deadline)
    throw "MediaStore exposed $($device.mediaCount) items; expected at least $Minimum."
}

function Test-ReadOnlyBoundary {
    param([string]$Address)
    $checks = foreach ($method in 'Delete','Patch','Put') {
        $status = try {
            (Invoke-WebRequest `
                -Uri "http://$Address/api/v1/media/e2e-probe" `
                -Method $method `
                -TimeoutSec 5).StatusCode
        } catch {
            [int]$_.Exception.Response.StatusCode
        }
        [pscustomobject]@{
            Method = $method
            StatusCode = $status
            Passed = $status -in 404,405
        }
    }
    $checks | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutputRoot 'readonly-api.json') -Encoding UTF8
    if ($checks.Passed -contains $false) {
        throw 'A media mutation endpoint was unexpectedly available.'
    }
}

if ($RecreateAvd) {
    Recreate-TestAvd
}
Start-TestAvd
Assert-EmulatorCapacity
Build-TestTargets
Prepare-Media
Start-AndroidApp

$address = if ($Profile -eq 'Physical') {
    if ([string]::IsNullOrWhiteSpace($PhysicalAddress)) {
        throw 'Physical profile requires -PhysicalAddress host:port.'
    }
    $PhysicalAddress
} else {
    & $adb -s $DeviceSerial forward "tcp:$port" "tcp:$port" | Out-Null
    "127.0.0.1:$port"
}

& $adb -s $DeviceSerial logcat -c
$logcat = Start-Process `
    -FilePath $adb `
    -ArgumentList @('-s', $DeviceSerial, 'logcat', '-v', 'threadtime') `
    -RedirectStandardOutput $logcatPath `
    -WindowStyle Hidden `
    -PassThru

try {
    $minimumMediaCount = if ($Profile -eq 'Scale') { 5000 } else { 1 }
    $device = Wait-MediaCount $address $minimumMediaCount
    $device | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $OutputRoot 'device.json') -Encoding UTF8
    Test-ReadOnlyBoundary $address

    $desktop = Join-Path $repositoryRoot `
        'desktop\LinkGallery.Desktop\bin\Debug\net8.0-windows\LinkGallery.Desktop.exe'
    $runner = Join-Path $repositoryRoot `
        'e2e\LinkGallery.E2E\bin\Debug\net8.0-windows\LinkGallery.E2E.exe'
    $iterations = if ($Profile -eq 'Soak') { 10 } else { 1 }
    $minutes = if ($Profile -eq 'Soak') { $SoakMinutes } else { 0 }
    $requireVideo = $Profile -ne 'Scale'
    & $runner `
        --desktop $desktop `
        --address $address `
        --artifacts $OutputRoot `
        --data $dataRoot `
        --imports $importRoot `
        --iterations $iterations `
        --soak-minutes $minutes `
        --require-video $requireVideo
    $runnerExit = $LASTEXITCODE

    $integrityRequired = $Profile -ne 'Physical' -and -not $SkipMedia
    $integrityPassed = -not $integrityRequired
    $integrityDetail = if ($integrityRequired) {
        'No completed import was available.'
    } else {
        'Source hash comparison is not required for this profile.'
    }
    $imported = Get-ChildItem -LiteralPath $importRoot -File |
        Where-Object Extension -NE '.partial' |
        Select-Object -First 1
    if ($null -ne $imported -and (Test-Path -LiteralPath $manifestPath)) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $sourceEntry = $manifest | Where-Object Name -EQ $imported.Name | Select-Object -First 1
        if ($null -eq $sourceEntry -and
            $Profile -eq 'Scale' -and
            $imported.Extension -ieq '.JPG') {
            $sourceEntry = $manifest | Where-Object Name -Like '*.JPG' | Select-Object -First 1
        }
        if ($null -ne $sourceEntry) {
            $sourceHash = (Get-FileHash -LiteralPath $sourceEntry.FullName -Algorithm SHA256).Hash
            $importHash = (Get-FileHash -LiteralPath $imported.FullName -Algorithm SHA256).Hash
            $integrityPassed = $sourceHash -eq $importHash
            $integrityDetail = "Source=$sourceHash Import=$importHash"
        }
    }
    [pscustomobject]@{
        Passed = $integrityPassed
        Detail = $integrityDetail
        ImportedFile = $imported.FullName
    } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutputRoot 'import-integrity.json') -Encoding UTF8
    if (-not $integrityPassed) {
        $runnerExit = 1
    }

    $screenshotPath = Join-Path $OutputRoot 'android-screen.png'
    $screenshot = Start-Process `
        -FilePath $adb `
        -ArgumentList @('-s',$DeviceSerial,'exec-out','screencap','-p') `
        -RedirectStandardOutput $screenshotPath `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($screenshot.ExitCode -ne 0) {
        throw 'Unable to capture the Android screen.'
    }
} finally {
    if ($null -ne $logcat -and -not $logcat.HasExited) {
        Stop-Process -Id $logcat.Id
    }
}

$logText = Get-Content -LiteralPath $logcatPath -Raw -ErrorAction SilentlyContinue
$fatalCount = if ([string]::IsNullOrEmpty($logText)) {
    0
} else {
    [regex]::Matches(
        $logText,
        'FATAL EXCEPTION[\s\S]{0,1200}Process: com\.linkgallery\.companion'
    ).Count
}
$anrCount = @(
    Select-String `
        -LiteralPath $logcatPath `
        -Pattern 'ANR in com.linkgallery.companion' `
        -ErrorAction SilentlyContinue
).Count
$summary = [pscustomobject]@{
    Profile = $Profile
    GeneratedAt = [DateTimeOffset]::UtcNow
    Address = $address
    DeviceMediaCount = $device.mediaCount
    RunnerExitCode = $runnerExit
    AndroidFatalOrAnrCount = $fatalCount + $anrCount
    Passed = $runnerExit -eq 0 -and ($fatalCount + $anrCount) -eq 0
}
$summary | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding UTF8
$summary | Format-List
if (-not $summary.Passed) {
    exit 1
}
