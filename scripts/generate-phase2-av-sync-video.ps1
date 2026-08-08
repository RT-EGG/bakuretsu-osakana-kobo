[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FfmpegDirectory,

    [Parameter(Mandatory = $true)]
    [string] $GeneratorSource,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $GeneratorArchiveSha256,

    [string] $OutputDirectory = (
        Join-Path $PSScriptRoot '..\test-assets\generated\av-sync'),

    [switch] $Force
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

$versionLines = & $ffmpeg -version
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect the FFmpeg build.'
}

$configuration = ($versionLines | Where-Object { $_ -like 'configuration:*' }) -join ' '
foreach ($option in @('--enable-gpl', '--enable-nonfree', '--enable-libx264', '--enable-libx265')) {
    if ($configuration -match [regex]::Escape($option)) {
        throw "Refusing to use this FFmpeg build because it contains $option."
    }
}

$encoderList = (& $ffmpeg -hide_banner -encoders 2>&1) -join "`n"
foreach ($encoder in @('h264_mf', 'aac')) {
    if ($encoderList -notmatch "(?m)^\s*[A-Z\.]+\s+$([regex]::Escape($encoder))\s") {
        throw "Required encoder is unavailable: $encoder"
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

$width = 1920
$height = 1080
$duration = 10
$fps = 60
$fileName = "phase2-av-sync-${width}x${height}-${duration}s-${fps}fps-h264-aac.mp4"
$outputPath = Join-Path $resolvedOutput $fileName
if ((Test-Path -LiteralPath $outputPath) -and -not $Force) {
    throw "Output already exists. Use -Force to replace it: $outputPath"
}

$font = 'C\:/Windows/Fonts/consola.ttf'
$leftPulse = 'gte(t\,0.5)*lt(mod(t-0.5\,1)\,0.08)'
$rightPulse = 'gte(t\,1.0)*lt(mod(t\,1)\,0.08)'
$videoFilter = @(
    'drawgrid=w=iw/12:h=ih/8:t=2:c=white@0.20'
    "drawbox=x=0:y=0:w=iw/2:h=ih:color=yellow@0.85:t=fill:enable='$leftPulse'"
    "drawbox=x=iw/2:y=0:w=iw/2:h=ih:color=cyan@0.85:t=fill:enable='$rightPulse'"
    "drawbox=x=0:y=0:w=iw/2:h=ih:color=yellow@0.15:t=12"
    "drawbox=x=iw/2:y=0:w=iw/2:h=ih:color=cyan@0.15:t=12"
    "drawtext=fontfile='$font':text='LEFT / 1 kHz':x=w/12:y=h/3:fontsize=h/11:fontcolor=yellow:box=1:boxcolor=black@0.75"
    "drawtext=fontfile='$font':text='RIGHT / 1.5 kHz':x=w*0.57:y=h/3:fontsize=h/11:fontcolor=cyan:box=1:boxcolor=black@0.75"
    "drawtext=fontfile='$font':text='A/V SYNC TEST - pulse and flash must coincide':x=w/14:y=h/18:fontsize=h/22:fontcolor=white:box=1:boxcolor=black@0.75"
    "drawtext=fontfile='$font':text='0.5 s pre-roll':x=w/14:y=h*0.78:fontsize=h/28:fontcolor=white:box=1:boxcolor=black@0.75:enable='lt(t\,0.5)'"
    "drawtext=fontfile='$font':text='Frame %{n}  Time %{pts\:hms}':x=w/14:y=h*0.88:fontsize=h/24:fontcolor=white:box=1:boxcolor=black@0.75"
) -join ','

# Continuous low-level channel-identification tones remain after the 0.5-second pre-roll.
# Strong pulses alternate left/right every 0.5 seconds and use the same enable expressions
# as the matching full-screen flashes.
$audioSource = @(
    'aevalsrc='
    'gte(t\,0.5)*(0.08*sin(2*PI*440*t)+0.85*'
    "$leftPulse"
    '*sin(2*PI*1000*t))'
    '|'
    'gte(t\,0.5)*(0.08*sin(2*PI*660*t)+0.85*'
    "$rightPulse"
    '*sin(2*PI*1500*t))'
    ":s=48000:d=$duration"
) -join ''

$arguments = @(
    '-y',
    '-hide_banner',
    '-loglevel', 'warning',
    '-f', 'lavfi',
    '-i', "color=c=#101827:s=${width}x${height}:r=${fps}:d=${duration}",
    '-f', 'lavfi',
    '-i', $audioSource,
    '-vf', $videoFilter,
    '-c:v', 'h264_mf',
    '-rate_control', 'cbr',
    '-scenario', 'archive',
    '-b:v', '8M',
    '-g', ($fps * 2),
    '-pix_fmt', 'nv12',
    '-color_primaries', 'bt709',
    '-color_trc', 'bt709',
    '-colorspace', 'bt709',
    '-c:a', 'aac',
    '-b:a', '192k',
    '-ar', '48000',
    '-ac', '2',
    '-shortest',
    '-movflags', '+faststart',
    $outputPath
)

Write-Host "Generating $fileName"
& $ffmpeg @arguments
if ($LASTEXITCODE -ne 0) {
    throw "FFmpeg failed while generating $fileName."
}

$probeJson = & $ffprobe -v error -count_packets -show_entries `
    'format=duration,size,format_name:stream=index,codec_name,codec_type,width,height,avg_frame_rate,sample_rate,channels,duration,nb_read_packets,pix_fmt' `
    -of json $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "ffprobe failed while inspecting $fileName."
}

$probe = ($probeJson -join "`n") | ConvertFrom-Json
$video = $probe.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
$audio = $probe.streams | Where-Object codec_type -eq 'audio' | Select-Object -First 1
if ($video.codec_name -ne 'h264' -or
    [int] $video.width -ne $width -or
    [int] $video.height -ne $height -or
    $video.avg_frame_rate -ne "$fps/1" -or
    [int] $video.nb_read_packets -ne ($duration * $fps) -or
    $audio.codec_name -ne 'aac' -or
    [int] $audio.sample_rate -ne 48000 -or
    [int] $audio.channels -ne 2) {
    throw "Generated media properties did not match the A/V sync specification: $fileName"
}

$manifest = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    purpose = 'A/V sync, channel identification, and seek-to-zero restart validation.'
    timing = [ordered]@{
        preRollSeconds = 0.5
        pulseDurationSeconds = 0.08
        leftPulseSeconds = '0.5, 1.5, 2.5, ...'
        rightPulseSeconds = '1.0, 2.0, 3.0, ...'
        visualCue = 'The matching left or right half flashes for the complete audio pulse.'
    }
    generator = [ordered]@{
        ffmpegVersion = $versionLines[0]
        configuration = $configuration
        ffmpegExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ffmpeg).Hash
        archiveSha256 = $GeneratorArchiveSha256.ToUpperInvariant()
        source = $GeneratorSource
        distribution = 'Generation-only tool; not included in application or repository.'
    }
    file = [ordered]@{
        name = $fileName
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath).Hash
        sizeBytes = [long] $probe.format.size
        durationSeconds = [double] $probe.format.duration
        video = [ordered]@{
            codec = $video.codec_name
            width = [int] $video.width
            height = [int] $video.height
            frameRate = $video.avg_frame_rate
            packetCount = [int] $video.nb_read_packets
            pixelFormat = $video.pix_fmt
        }
        audio = [ordered]@{
            codec = $audio.codec_name
            sampleRate = [int] $audio.sample_rate
            channels = [int] $audio.channels
        }
    }
}

$manifestPath = Join-Path $resolvedOutput 'av-sync-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Generated: $outputPath"
Write-Host "Manifest: $manifestPath"
