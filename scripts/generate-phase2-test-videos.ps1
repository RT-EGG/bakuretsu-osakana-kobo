[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $FfmpegDirectory,

    [Parameter(Mandatory = $true)]
    [string] $GeneratorSource,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $GeneratorArchiveSha256,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\test-assets\generated'),

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
$prohibitedOptions = @(
    '--enable-gpl',
    '--enable-nonfree',
    '--enable-libx264',
    '--enable-libx265'
)

foreach ($option in $prohibitedOptions) {
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

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

$font = 'C\:/Windows/Fonts/consola.ttf'
$specifications = @(
    [pscustomobject]@{ Width = 1920; Height = 1080; Duration = 5;  Fps = 30; Bitrate = '6M'  },
    [pscustomobject]@{ Width = 1920; Height = 1080; Duration = 5;  Fps = 60; Bitrate = '8M'  },
    [pscustomobject]@{ Width = 1920; Height = 1080; Duration = 60; Fps = 30; Bitrate = '6M'  },
    [pscustomobject]@{ Width = 1920; Height = 1080; Duration = 60; Fps = 60; Bitrate = '8M'  },
    [pscustomobject]@{ Width = 3840; Height = 2160; Duration = 5;  Fps = 30; Bitrate = '10M' },
    [pscustomobject]@{ Width = 3840; Height = 2160; Duration = 5;  Fps = 60; Bitrate = '12M' },
    [pscustomobject]@{ Width = 3840; Height = 2160; Duration = 60; Fps = 30; Bitrate = '10M' },
    [pscustomobject]@{ Width = 3840; Height = 2160; Duration = 60; Fps = 60; Bitrate = '12M' }
)

$manifestItems = [System.Collections.Generic.List[object]]::new()

foreach ($specification in $specifications) {
    $label = '{0}x{1} {2}s {3}fps' -f $specification.Width, $specification.Height,
        $specification.Duration, $specification.Fps
    $fileName = 'phase2-{0}x{1}-{2}s-{3}fps-h264-aac.mp4' -f $specification.Width,
        $specification.Height, $specification.Duration, $specification.Fps
    $outputPath = Join-Path $resolvedOutput $fileName

    if ((Test-Path -LiteralPath $outputPath) -and -not $Force) {
        throw "Output already exists. Use -Force to replace it: $outputPath"
    }

    $videoFilter = @(
        "drawgrid=w=iw/12:h=ih/8:t=2:c=white@0.20"
        "drawbox=x='mod(t*iw/5,iw-iw/8)':y='ih*0.18':w=iw/8:h=ih/8:color=red@0.90:t=fill"
        "drawbox=x='iw-iw/10-mod(t*iw/7,iw-iw/10)':y='ih*0.65':w=iw/10:h=ih/10:color=lime@0.90:t=fill"
        "drawbox=x='iw*0.76':y='ih*0.08':w=iw/24:h=ih/12:color=red:t=fill"
        "drawbox=x='iw*0.81':y='ih*0.08':w=iw/24:h=ih/12:color=green:t=fill"
        "drawbox=x='iw*0.86':y='ih*0.08':w=iw/24:h=ih/12:color=blue:t=fill"
        "drawbox=x='iw*0.91':y='ih*0.08':w=iw/24:h=ih/12:color=white:t=fill"
        "drawtext=fontfile='$font':text='Phase 2 Playback Test':x=w/24:y=h/24:fontsize=h/18:fontcolor=white:box=1:boxcolor=black@0.60"
        "drawtext=fontfile='$font':text='$label':x=w/24:y=h/9:fontsize=h/27:fontcolor=cyan:box=1:boxcolor=black@0.60"
        "drawtext=fontfile='$font':text='Frame %{n}  Time %{pts\:hms}':x=w/24:y=h-h/10:fontsize=h/22:fontcolor=yellow:box=1:boxcolor=black@0.70"
    ) -join ','

    # 440 Hz is always on the left and 660 Hz is always on the right.
    # A short 1 kHz/1.5 kHz pulse alternates every half second for A/V sync checks.
    $audioSource = @(
        'aevalsrc='
        '0.10*sin(2*PI*440*t)+0.85*lt(mod(t\,1)\,0.05)*sin(2*PI*1000*t)'
        '|'
        '0.10*sin(2*PI*660*t)+0.85*lt(mod(t+0.5\,1)\,0.05)*sin(2*PI*1500*t)'
        ":s=48000:d=$($specification.Duration)"
    ) -join ''

    Write-Host "Generating $fileName"
    $arguments = @(
        '-y',
        '-hide_banner',
        '-loglevel', 'warning',
        '-f', 'lavfi',
        '-i', "color=c=#101827:s=$($specification.Width)x$($specification.Height):r=$($specification.Fps):d=$($specification.Duration)",
        '-f', 'lavfi',
        '-i', $audioSource,
        '-vf', $videoFilter,
        '-c:v', 'h264_mf',
        '-rate_control', 'cbr',
        '-scenario', 'archive',
        '-b:v', $specification.Bitrate,
        '-g', ($specification.Fps * 2),
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

    & $ffmpeg @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg failed while generating $fileName."
    }

    $probeJson = & $ffprobe -v error -show_entries `
        'format=duration,size,format_name:stream=index,codec_name,codec_type,width,height,avg_frame_rate,sample_rate,channels,duration,pix_fmt' `
        -of json $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "ffprobe failed while inspecting $fileName."
    }

    $probe = ($probeJson -join "`n") | ConvertFrom-Json
    $video = $probe.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $audio = $probe.streams | Where-Object codec_type -eq 'audio' | Select-Object -First 1

    $manifestItems.Add([pscustomobject]@{
        file = $fileName
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath).Hash
        sizeBytes = [long]$probe.format.size
        durationSeconds = [double]$probe.format.duration
        video = [ordered]@{
            codec = $video.codec_name
            width = [int]$video.width
            height = [int]$video.height
            frameRate = $video.avg_frame_rate
            pixelFormat = $video.pix_fmt
        }
        audio = [ordered]@{
            codec = $audio.codec_name
            sampleRate = [int]$audio.sample_rate
            channels = [int]$audio.channels
        }
    })
}

$manifest = [ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    generator = [ordered]@{
        ffmpegVersion = $versionLines[0]
        configuration = $configuration
        ffmpegExecutableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ffmpeg).Hash
        archiveSha256 = $GeneratorArchiveSha256.ToUpperInvariant()
        source = $GeneratorSource
        distribution = 'Generation-only tool; not included in application or repository.'
    }
    files = $manifestItems
}

$manifestPath = Join-Path $resolvedOutput 'manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Generated $($manifestItems.Count) files."
Write-Host "Manifest: $manifestPath"
