[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FfmpegDirectory,

    [string] $AssetDirectory = (Join-Path $PSScriptRoot '..\test-assets\generated')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ffmpeg = Join-Path $FfmpegDirectory 'ffmpeg.exe'
$ffprobe = Join-Path $FfmpegDirectory 'ffprobe.exe'
foreach ($tool in @($ffmpeg, $ffprobe)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required tool was not found: $tool"
    }
}

$resolvedAssets = [System.IO.Path]::GetFullPath($AssetDirectory)
$mediaFiles = @(
    Get-ChildItem -LiteralPath $resolvedAssets -File |
        Where-Object Extension -in @('.mp4', '.wmv') |
        Sort-Object Name
)
$expectedCount = 16
$errors = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[object]]::new()

if ($mediaFiles.Count -ne $expectedCount) {
    $errors.Add("Expected $expectedCount media files, but found $($mediaFiles.Count).")
}

foreach ($file in $mediaFiles) {
    if ($file.BaseName -notmatch '^phase2-(?<width>1920|3840)x(?<height>1080|2160)-(?<duration>5|60)s-(?<fps>30|60)fps-(?<format>h264-aac|wmv9-wma)$') {
        $errors.Add("Unexpected file name: $($file.Name)")
        continue
    }

    $expectedWidth = [int]$Matches.width
    $expectedHeight = [int]$Matches.height
    $expectedDuration = [int]$Matches.duration
    $expectedFps = [int]$Matches.fps
    $expectedFrames = $expectedDuration * $expectedFps
    $expectedVideoCodec = if ($Matches.format -eq 'h264-aac') { 'h264' } else { 'vc1' }
    $expectedAudioCodec = if ($Matches.format -eq 'h264-aac') { 'aac' } else { 'wmapro' }

    $probeJson = & $ffprobe -v error -count_packets -show_entries `
        'format=format_name,duration,size:stream=index,codec_name,codec_long_name,codec_type,width,height,avg_frame_rate,r_frame_rate,sample_rate,channels,duration,nb_read_packets,pix_fmt' `
        -of json $file.FullName
    if ($LASTEXITCODE -ne 0) {
        $errors.Add("ffprobe failed: $($file.Name)")
        continue
    }

    $probe = ($probeJson -join "`n") | ConvertFrom-Json
    $video = $probe.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $audio = $probe.streams | Where-Object codec_type -eq 'audio' | Select-Object -First 1
    if ($null -eq $video -or $null -eq $audio) {
        $errors.Add("Missing video or audio stream: $($file.Name)")
        continue
    }

    $duration = [double]$probe.format.duration
    $frameCount = [int]$video.nb_read_packets
    $fileErrors = [System.Collections.Generic.List[string]]::new()

    if ($video.codec_name -ne $expectedVideoCodec) {
        $fileErrors.Add("video codec $($video.codec_name), expected $expectedVideoCodec")
    }
    if ($audio.codec_name -ne $expectedAudioCodec) {
        $fileErrors.Add("audio codec $($audio.codec_name), expected $expectedAudioCodec")
    }
    if ([int]$video.width -ne $expectedWidth -or [int]$video.height -ne $expectedHeight) {
        $fileErrors.Add("resolution $($video.width)x$($video.height), expected ${expectedWidth}x${expectedHeight}")
    }
    if ([math]::Abs($duration - $expectedDuration) -gt 0.1) {
        $fileErrors.Add("duration $duration, expected $expectedDuration +/- 0.1 seconds")
    }
    if ($frameCount -ne $expectedFrames) {
        $fileErrors.Add("video packets $frameCount, expected $expectedFrames")
    }
    if ([int]$audio.sample_rate -ne 48000 -or [int]$audio.channels -ne 2) {
        $fileErrors.Add("audio $($audio.sample_rate) Hz/$($audio.channels) channels, expected 48000 Hz/2 channels")
    }

    $volumeOutput = & $ffmpeg -hide_banner -loglevel info -i $file.FullName `
        -map '0:a:0' -af volumedetect -f null NUL 2>&1
    if ($LASTEXITCODE -ne 0) {
        $fileErrors.Add('audio peak measurement failed')
        $maxVolume = $null
    }
    else {
        $volumeText = $volumeOutput -join "`n"
        if ($volumeText -match 'max_volume:\s+(?<volume>-?\d+(?:\.\d+)?) dB') {
            $maxVolume = [double]$Matches.volume
            if ($maxVolume -lt -1.5 -or $maxVolume -gt 0.1) {
                $fileErrors.Add("audio peak $maxVolume dBFS, expected -1.5 to 0.1 dBFS")
            }
        }
        else {
            $maxVolume = $null
            $fileErrors.Add('audio peak was not reported')
        }
    }

    foreach ($fileError in $fileErrors) {
        $errors.Add("$($file.Name): $fileError")
    }

    $results.Add([ordered]@{
        file = $file.Name
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
        sizeBytes = [long]$probe.format.size
        durationSeconds = $duration
        video = [ordered]@{
            codec = $video.codec_name
            codecDescription = $video.codec_long_name
            width = [int]$video.width
            height = [int]$video.height
            expectedFps = $expectedFps
            packetCount = $frameCount
            pixelFormat = $video.pix_fmt
        }
        audio = [ordered]@{
            codec = $audio.codec_name
            codecDescription = $audio.codec_long_name
            sampleRate = [int]$audio.sample_rate
            channels = [int]$audio.channels
            maxVolumeDbfs = $maxVolume
        }
        passed = $fileErrors.Count -eq 0
    })
}

$report = [ordered]@{
    verifiedAt = (Get-Date).ToUniversalTime().ToString('o')
    expectedFiles = $expectedCount
    actualFiles = $mediaFiles.Count
    passed = $errors.Count -eq 0
    errors = $errors
    files = $results
}
$reportPath = Join-Path $resolvedAssets 'verification.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    throw "Phase 2 test-video verification failed with $($errors.Count) error(s)."
}

Write-Host "Verified $($results.Count) files."
Write-Host "Report: $reportPath"
