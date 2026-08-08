[CmdletBinding()]
param(
    [string] $InputDirectory = (Join-Path $PSScriptRoot '..\test-assets\generated'),

    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\test-assets\generated'),

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'This script requires Windows PowerShell 5.1 (powershell.exe) for its WinRT projection.'
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.StorageFolder, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.CreationCollisionOption, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Transcoding.MediaTranscoder, Windows.Media.Transcoding, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Transcoding.PrepareTranscodeResult, Windows.Media.Transcoding, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.MediaProperties.MediaEncodingProfile, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.MediaProperties.VideoEncodingQuality, Windows.Media.MediaProperties, ContentType = WindowsRuntime] | Out-Null

function Wait-WinRtOperation {
    param(
        [Parameter(Mandatory = $true)] $Operation,
        [Parameter(Mandatory = $true)] [type] $ResultType
    )

    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.IsGenericMethod -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } |
        Select-Object -First 1

    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

function Wait-WinRtActionWithProgress {
    param([Parameter(Mandatory = $true)] $Operation)

    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.IsGenericMethod -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncActionWithProgress`1'
        } |
        Select-Object -First 1

    $task = $method.MakeGenericMethod([double]).Invoke($null, @($Operation))
    $task.Wait()
}

$resolvedInput = [System.IO.Path]::GetFullPath($InputDirectory)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

$outputFolder = Wait-WinRtOperation `
    ([Windows.Storage.StorageFolder]::GetFolderFromPathAsync($resolvedOutput)) `
    ([Windows.Storage.StorageFolder])

$inputFiles = @(
    Get-ChildItem -LiteralPath $resolvedInput -File -Filter 'phase2-*-h264-aac.mp4' |
        Sort-Object Name
)

if ($inputFiles.Count -eq 0) {
    throw "No Phase 2 MP4 files were found in $resolvedInput."
}

foreach ($inputFile in $inputFiles) {
    $standardPattern = '^phase2-(?<width>\d+)x(?<height>\d+)-(?<duration>\d+)s-(?<fps>\d+)fps-h264-aac$'
    $syncPattern = '^phase2-av-sync-(?<width>\d+)x(?<height>\d+)-(?<duration>\d+)s-(?<fps>\d+)fps-h264-aac$'
    if ($inputFile.BaseName -notmatch $standardPattern -and
        $inputFile.BaseName -notmatch $syncPattern) {
        throw "Unexpected Phase 2 file name: $($inputFile.Name)"
    }

    $width = [uint32]$Matches.width
    $height = [uint32]$Matches.height
    $fps = [uint32]$Matches.fps
    $bitrate = if ($width -ge 3840) {
        if ($fps -ge 60) { 12000000 } else { 10000000 }
    }
    else {
        if ($fps -ge 60) { 8000000 } else { 6000000 }
    }

    $outputName = $inputFile.Name -replace 'h264-aac\.mp4$', 'wmv9-wma.wmv'
    $outputPath = Join-Path $resolvedOutput $outputName
    if ((Test-Path -LiteralPath $outputPath) -and -not $Force) {
        throw "Output already exists. Use -Force to replace it: $outputPath"
    }

    $source = Wait-WinRtOperation `
        ([Windows.Storage.StorageFile]::GetFileFromPathAsync($inputFile.FullName)) `
        ([Windows.Storage.StorageFile])
    $collision = if ($Force) {
        [Windows.Storage.CreationCollisionOption]::ReplaceExisting
    }
    else {
        [Windows.Storage.CreationCollisionOption]::FailIfExists
    }
    $destination = Wait-WinRtOperation `
        ($outputFolder.CreateFileAsync($outputName, $collision)) `
        ([Windows.Storage.StorageFile])

    $profile = [Windows.Media.MediaProperties.MediaEncodingProfile]::CreateWmv(
        [Windows.Media.MediaProperties.VideoEncodingQuality]::HD1080p
    )
    $profile.Video.Width = $width
    $profile.Video.Height = $height
    $profile.Video.Bitrate = [uint32]$bitrate
    $profile.Video.FrameRate.Numerator = $fps
    $profile.Video.FrameRate.Denominator = 1
    $profile.Audio.SampleRate = 48000
    $profile.Audio.ChannelCount = 2
    $profile.Audio.Bitrate = 192000

    Write-Host "Transcoding $($inputFile.Name) -> $outputName"
    $transcoder = [Windows.Media.Transcoding.MediaTranscoder]::new()
    $preparation = Wait-WinRtOperation `
        ($transcoder.PrepareFileTranscodeAsync($source, $destination, $profile)) `
        ([Windows.Media.Transcoding.PrepareTranscodeResult])

    if (-not $preparation.CanTranscode) {
        Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
        throw "Windows Media Transcoder rejected $($inputFile.Name): $($preparation.FailureReason)"
    }

    Wait-WinRtActionWithProgress ($preparation.TranscodeAsync())
}

Write-Host "Generated $($inputFiles.Count) WMV files."
