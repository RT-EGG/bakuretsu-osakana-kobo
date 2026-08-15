[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int]$Runs = 5,

    [string]$OutputPath = 'docs/phase4/results/product-performance-measurement.json',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'tools\BakuretsuOsakanaKobo.ReleaseValidation\BakuretsuOsakanaKobo.ReleaseValidation.csproj'
$executable = Join-Path $repoRoot 'tools\BakuretsuOsakanaKobo.ReleaseValidation\bin\Release\net10.0-windows\win-x64\BakuretsuOsakanaKobo.ReleaseValidation.exe'
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
$temporaryRoot = Join-Path $repoRoot ('.tmp\phase4-product-performance\' + [Guid]::NewGuid().ToString('N'))

if (-not $NoBuild) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Validation restore failed with exit code $LASTEXITCODE." }
    & dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Validation build failed with exit code $LASTEXITCODE." }
}

[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null

$conditions = @(
    [pscustomobject]@{ id = 'mp4-1080p60-60s'; video = Join-Path $repoRoot 'test-assets\generated\phase2-1920x1080-60s-60fps-h264-aac.mp4' },
    [pscustomobject]@{ id = 'mp4-4k60-60s'; video = Join-Path $repoRoot 'test-assets\generated\phase2-3840x2160-60s-60fps-h264-aac.mp4' },
    [pscustomobject]@{ id = 'wmv-1080p60-60s'; video = Join-Path $repoRoot 'test-assets\generated\phase2-1920x1080-60s-60fps-wmv9-wma.wmv' }
)

$results = [Collections.Generic.List[object]]::new()
foreach ($condition in $conditions) {
    if (-not (Test-Path -LiteralPath $condition.video -PathType Leaf)) {
        throw "Representative video is missing: $($condition.video)"
    }

    for ($run = 1; $run -le $Runs; $run++) {
        $reportPath = Join-Path $temporaryRoot "$($condition.id)-$run.json"
        $startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
        $startInfo.UseShellExecute = $false
        $startInfo.ArgumentList.Add($condition.video)
        $startInfo.ArgumentList.Add($reportPath)
        $process = [Diagnostics.Process]::Start($startInfo)
        if (-not $process) {
            throw "Could not start product performance validation for $($condition.id), run $run."
        }
        try {
            $process.WaitForExit()
            if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
                throw "Product performance validation failed for $($condition.id), run $run."
            }
        } finally {
            $process.Dispose()
        }

        $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
        $result = [pscustomobject]@{
            condition = $condition.id
            run = $run
            muted = [bool]$report.muted
            processStartToWindowLoadedMs = [Math]::Round([double]$report.processStartToWindowLoadedMs, 3)
            openToTimelineReadyMs = [Math]::Round([double]$report.openToTimelineReadyMs, 3)
            openToAudioOutputReadyMs = [Math]::Round([double]$report.openToAudioOutputReadyMs, 3)
            seek50PercentMs = [Math]::Round([double]$report.seek50PercentMs, 3)
            seek90PercentMs = [Math]::Round([double]$report.seek90PercentMs, 3)
        }
        $results.Add($result)
        Write-Output "$($condition.id) $run/${Runs}: start=$($result.processStartToWindowLoadedMs) ms, video=$($result.openToTimelineReadyMs) ms, audio=$($result.openToAudioOutputReadyMs) ms, seek50=$($result.seek50PercentMs) ms, seek90=$($result.seek90PercentMs) ms"
    }
}

function Get-Maximum([object[]]$Items, [string]$PropertyName) {
    return [Math]::Round([double](($Items | Measure-Object -Property $PropertyName -Maximum).Maximum), 3)
}

$summary = @($conditions | ForEach-Object {
    $items = @($results | Where-Object condition -eq $_.id)
    [ordered]@{
        condition = $_.id
        runs = $items.Count
        allMuted = @($items | Where-Object { -not $_.muted }).Count -eq 0
        maximum = [ordered]@{
            processStartToWindowLoadedMs = Get-Maximum $items 'processStartToWindowLoadedMs'
            openToTimelineReadyMs = Get-Maximum $items 'openToTimelineReadyMs'
            openToAudioOutputReadyMs = Get-Maximum $items 'openToAudioOutputReadyMs'
            seek50PercentMs = Get-Maximum $items 'seek50PercentMs'
            seek90PercentMs = Get-Maximum $items 'seek90PercentMs'
        }
    }
})

$thresholds = [ordered]@{
    processStartToWindowLoadedMs = 1500
    openToTimelineReadyMs = 1000
    openToAudioOutputReadyMs = 1000
    seek50PercentMs = 1500
    seek90PercentMs = 1500
}
$passed = @($summary | Where-Object {
    -not $_.allMuted -or
    $_.maximum.processStartToWindowLoadedMs -gt $thresholds.processStartToWindowLoadedMs -or
    $_.maximum.openToTimelineReadyMs -gt $thresholds.openToTimelineReadyMs -or
    $_.maximum.openToAudioOutputReadyMs -gt $thresholds.openToAudioOutputReadyMs -or
    $_.maximum.seek50PercentMs -gt $thresholds.seek50PercentMs -or
    $_.maximum.seek90PercentMs -gt $thresholds.seek90PercentMs
}).Count -eq 0

[ordered]@{
    generatedAt = [DateTimeOffset]::Now.ToString('O')
    method = 'Separate-process WPF validation using the product MainWindow, LibVlcPlaybackBackend, unified LibVLC PCM/NAudio WASAPI output, and seek Slider. Video readiness is the later of LibVLC Vout and playback clock >= 100 ms. Audio readiness requires PCM callbacks and WASAPI output start. PCM is muted before media opens.'
    thresholds = $thresholds
    passed = $passed
    runs = $results
    summary = $summary
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8

Write-Output "Wrote $output"
if (-not $passed) { exit 1 }
