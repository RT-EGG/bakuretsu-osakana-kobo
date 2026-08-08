[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FfmpegDirectory,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.tmp\edge-case-videos')
)

$ErrorActionPreference = 'Stop'
$ffmpeg = (Resolve-Path -LiteralPath (Join-Path $FfmpegDirectory 'ffmpeg.exe')).Path
$ffprobe = (Resolve-Path -LiteralPath (Join-Path $FfmpegDirectory 'ffprobe.exe')).Path
$repoRoot = Split-Path -Parent $PSScriptRoot
$source30 = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'test-assets\generated\phase2-1920x1080-5s-30fps-h264-aac.mp4')).Path
$source60 = (Resolve-Path -LiteralPath (Join-Path $repoRoot 'test-assets\generated\phase2-1920x1080-5s-60fps-h264-aac.mp4')).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

$configuration = (& $ffmpeg -version | Where-Object { $_ -like 'configuration:*' }) -join ' '
foreach ($option in @('--enable-gpl', '--enable-nonfree', '--enable-libx264', '--enable-libx265')) {
    if ($configuration -match [regex]::Escape($option)) { throw "Prohibited FFmpeg option: $option" }
}

$hevc = Join-Path $outputRoot 'edge-hevc-aac.mp4'
& $ffmpeg -y -hide_banner -loglevel warning -i $source30 -c:v hevc_mf -b:v 6M -tag:v hvc1 -c:a copy -movflags +faststart $hevc
$hevcEncoder = 'hevc_mf'
$hevcGenerationError = $null
if ($LASTEXITCODE -ne 0) {
    & $ffmpeg -y -hide_banner -loglevel warning -i $source30 -c:v hevc_nvenc -preset p4 -b:v 6M -tag:v hvc1 -c:a copy -movflags +faststart $hevc
    $hevcEncoder = 'hevc_nvenc'
}
if ($LASTEXITCODE -ne 0) {
    & $ffmpeg -y -hide_banner -loglevel warning -i $source30 -c:v libkvazaar -kvazaar-params preset=ultrafast -b:v 6M -tag:v hvc1 -c:a copy -movflags +faststart $hevc
    $hevcEncoder = 'libkvazaar'
}
if ($LASTEXITCODE -ne 0) {
    $hevcEncoder = $null
    $hevcGenerationError = 'HEVC generation failed with Media Foundation, NVENC, and libkvazaar.'
    [IO.File]::Delete($hevc)
}

$vfr = Join-Path $outputRoot 'edge-h264-vfr-aac.mp4'
& $ffmpeg -y -hide_banner -loglevel warning -i $source60 -vf "select='if(lt(t,2.5),not(mod(n,2)),not(mod(n,3)))'" -fps_mode vfr -c:v h264_mf -b:v 6M -c:a copy -movflags +faststart $vfr
if ($LASTEXITCODE -ne 0) { throw 'VFR generation failed.' }

$vp9 = Join-Path $outputRoot 'edge-vp9-aac.mp4'
& $ffmpeg -y -hide_banner -loglevel warning -i $source30 -c:v libvpx-vp9 -deadline realtime -cpu-used 8 -b:v 2M -c:a copy -movflags +faststart $vp9
if ($LASTEXITCODE -ne 0) { throw 'VP9 generation failed.' }

$sourceBytes = [IO.File]::ReadAllBytes($source30)
$truncated = Join-Path $outputRoot 'edge-truncated.mp4'
[IO.File]::WriteAllBytes($truncated, $sourceBytes[0..([Math]::Floor($sourceBytes.Length * 0.45) - 1)])
$fake = Join-Path $outputRoot 'edge-not-media.mp4'
[IO.File]::WriteAllText($fake, 'This is deliberately not a media container.', [Text.Encoding]::ASCII)

$generatedPaths = @($vfr, $vp9, $truncated, $fake)
if ($hevcEncoder) { $generatedPaths = @($hevc) + $generatedPaths }

$items = foreach ($path in $generatedPaths) {
    $probeText = (& $ffprobe -v error -show_entries 'format=format_name,duration,size:stream=index,codec_name,codec_type,profile,width,height,avg_frame_rate,r_frame_rate' -of json $path 2>$null) -join "`n"
    [pscustomobject]@{
        file = [IO.Path]::GetFileName($path)
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
        ffprobeExitCode = $LASTEXITCODE
        probe = if ($probeText) { $probeText | ConvertFrom-Json } else { $null }
    }
}

[ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    ffmpeg = (& $ffmpeg -version | Select-Object -First 1)
    configuration = $configuration
    distribution = 'Test generation only; assets are not application dependencies.'
    hevcEncoder = $hevcEncoder
    hevcGenerationError = $hevcGenerationError
    files = $items
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot 'manifest.json') -Encoding utf8

Write-Output "Generated edge-case assets: $outputRoot"
