[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseAssetDirectory,

    [string]$EvidenceDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'BakuretsuOsakanaKobo-Acceptance'),

    [switch]$AutomatedOnly
)

$ErrorActionPreference = 'Stop'

function Confirm-ManualStep {
    param([Parameter(Mandatory = $true)][string]$Prompt)

    while ($true) {
        $answer = Read-Host "$Prompt [y/n]"
        switch ($answer.Trim().ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
        }
    }
}

function Get-RequiredAsset {
    param(
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)]$ManifestEntry
    )

    if (-not $ManifestEntry -or [string]::IsNullOrWhiteSpace($ManifestEntry.file)) {
        throw 'The release asset manifest is missing a required asset entry.'
    }

    $path = Join-Path $AssetRoot $ManifestEntry.file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release asset is missing: $path"
    }

    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
    if ($item.Length -ne [long]$ManifestEntry.bytes) {
        throw "Release asset size mismatch: $($ManifestEntry.file)"
    }
    if ($hash -ne $ManifestEntry.sha256) {
        throw "Release asset SHA-256 mismatch: $($ManifestEntry.file)"
    }

    return [ordered]@{
        file = $ManifestEntry.file
        path = $item.FullName
        bytes = $item.Length
        sha256 = $hash
    }
}

$assetRoot = (Resolve-Path -LiteralPath $ReleaseAssetDirectory).Path
$manifestPath = Join-Path $assetRoot 'release-assets.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Release asset manifest is missing: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.validationOnly) {
    throw 'Clean-environment acceptance requires a public-release candidate generated without -ValidationOnly.'
}

$binaryAsset = Get-RequiredAsset -AssetRoot $assetRoot -ManifestEntry $manifest.binaryArchive
$sourceAsset = Get-RequiredAsset -AssetRoot $assetRoot -ManifestEntry $manifest.correspondingSourceArchive

$os = Get-CimInstance Win32_OperatingSystem
$isWindows11 = [Environment]::OSVersion.Version.Build -ge 22000
$isX64 = [Environment]::Is64BitOperatingSystem -and $env:PROCESSOR_ARCHITECTURE -eq 'AMD64'
if (-not $isWindows11) {
    throw "Windows 11 is required. Detected build: $([Environment]::OSVersion.Version.Build)"
}
if (-not $isX64) {
    throw "Windows x64 is required. Detected architecture: $env:PROCESSOR_ARCHITECTURE"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '.NET 10 Desktop Runtime x64 is not installed or dotnet.exe is not on PATH.'
}
$desktopRuntime = @(& $dotnet.Source --list-runtimes) | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }
if ($desktopRuntime.Count -eq 0) {
    throw '.NET 10 Desktop Runtime x64 is required.'
}

$evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$runDirectory = Join-Path $evidenceRoot ([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$extractDirectory = Join-Path $runDirectory 'app'
[IO.Directory]::CreateDirectory($extractDirectory) | Out-Null

Expand-Archive -LiteralPath $binaryAsset.path -DestinationPath $extractDirectory

$requiredFiles = @(
    'BakuretsuOsakanaKobo.exe',
    'BakuretsuOsakanaKobo.dll',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'CORRESPONDING-SOURCE.md',
    'docs\supported-media-formats.md',
    'dotnet-packages.json',
    'licenses\LGPL-2.1.txt'
)
foreach ($relativePath in $requiredFiles) {
    $requiredPath = Join-Path $extractDirectory $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}

$pluginDirectory = Join-Path $extractDirectory 'libvlc\win-x64\plugins'
$pluginCount = @(Get-ChildItem -LiteralPath $pluginDirectory -Filter '*.dll' -File -Recurse).Count
if ($pluginCount -ne 319) {
    throw "Expected 319 LibVLC plugins, found $pluginCount."
}

$forbiddenPlugins = @(
    'libdolby_surround_decoder_plugin.dll',
    'libheadphone_channel_mixer_plugin.dll',
    'libx26410b_plugin.dll',
    'liblua_plugin.dll'
)
$forbiddenFound = @(Get-ChildItem -LiteralPath $pluginDirectory -File -Recurse | Where-Object Name -In $forbiddenPlugins)
if ($forbiddenFound.Count -ne 0) {
    throw "Forbidden GPL-only plugin found: $($forbiddenFound.Name -join ', ')"
}

$invalidMediaPath = Join-Path $runDirectory 'invalid-media.mp4'
Set-Content -LiteralPath $invalidMediaPath -Value 'This is intentionally not a media file.' -Encoding utf8

$executable = Join-Path $extractDirectory 'BakuretsuOsakanaKobo.exe'
$process = Start-Process -FilePath $executable -WorkingDirectory $extractDirectory -PassThru
$windowReady = $false
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.MainWindowHandle -ne 0 -and $process.Responding) {
        $windowReady = $true
        break
    }
}
if (-not $windowReady) {
    if (-not $process.HasExited) { $null = $process.CloseMainWindow() }
    throw 'The product window did not become ready within 15 seconds.'
}
$windowTitle = $process.MainWindowTitle

$manual = [ordered]@{}
$closeRequested = $false
$closedWithinTimeout = $true
try {
    if (-not $AutomatedOnly) {
        Write-Host ''
        Write-Host '製品ウィンドウで次の確認を行ってください。検証素材は配布ZIPへ含まれません。'
        $manual.mp4AutoPlayback = Confirm-ManualStep 'ファイル > 開く から保証対象MP4を選び、自動再生しましたか'
        $manual.pauseResume = Confirm-ManualStep '再生・一時停止・再開が正しく動作しましたか'
        $manual.seekAndTime = Confirm-ManualStep 'シークバーを操作でき、現在時刻 / 全体時間が正しく表示されましたか'
        $manual.wmvAutoPlayback = Confirm-ManualStep 'ファイル > 開く から保証対象WMVを選び、自動再生しましたか'
        Write-Host "破損入力の確認には次のファイルを選択してください: $invalidMediaPath"
        $manual.invalidMediaRecovery = Confirm-ManualStep '破損MP4でエラーが表示され、アプリが異常終了せず操作を継続できましたか'
        $manual.noticesReadable = Confirm-ManualStep '展開先のREADME、第三者通知、LGPL本文、対応ソース案内を閲覧できましたか'
    }
} finally {
    $process.Refresh()
    if (-not $process.HasExited) {
        $closeRequested = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $closedWithinTimeout = $false
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
}

$automatedChecksPassed = $windowReady -and $closedWithinTimeout -and $process.ExitCode -eq 0
$manualPassed = -not $AutomatedOnly -and (@($manual.Values | Where-Object { -not $_ }).Count -eq 0)
$result = [ordered]@{
    completedAt = [DateTimeOffset]::UtcNow.ToString('o')
    automatedOnly = [bool]$AutomatedOnly
    automatedChecksPassed = $automatedChecksPassed
    passed = $automatedChecksPassed -and $manualPassed
    environment = [ordered]@{
        osCaption = $os.Caption
        osVersion = $os.Version
        osBuild = $os.BuildNumber
        architecture = $env:PROCESSOR_ARCHITECTURE
        windowsDesktopRuntime = @($desktopRuntime)
    }
    assets = [ordered]@{
        binary = $binaryAsset
        correspondingSource = $sourceAsset
    }
    package = [ordered]@{
        extractionDirectory = $extractDirectory
        requiredFileCount = $requiredFiles.Count
        libVlcPluginCount = $pluginCount
        forbiddenPluginCount = $forbiddenFound.Count
    }
    window = [ordered]@{
        ready = $windowReady
        title = $windowTitle
        closeRequested = $closeRequested
        closedWithinTimeout = $closedWithinTimeout
        exitCode = $process.ExitCode
    }
    manual = $manual
}

$resultPath = Join-Path $runDirectory 'clean-environment-acceptance.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output "Clean-environment acceptance evidence: $resultPath"

if (-not $automatedChecksPassed -or (-not $AutomatedOnly -and -not $result.passed)) {
    throw 'One or more clean-environment acceptance checks failed.'
}
