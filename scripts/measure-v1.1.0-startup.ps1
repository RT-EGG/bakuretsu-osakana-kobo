[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int]$Runs = 5,

    [ValidateRange(0, 10000)]
    [int]$BetweenRunsMilliseconds = 1000,

    [ValidateRange(0, 10000)]
    [int]$FileReadyDwellMilliseconds = 2000,

    [string]$OutputPath = 'docs/phase4/results/v1.1.0-startup-baseline.json'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\BakuretsuOsakanaKobo.App\BakuretsuOsakanaKobo.App.csproj'
$videoPath = Join-Path $repoRoot 'test-assets\generated\phase2-1920x1080-5s-30fps-h264-aac.mp4'
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
$temporaryParent = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tmp\v1.1.0-startup-measurement'))
$temporaryRoot = Join-Path $temporaryParent ([Guid]::NewGuid().ToString('N'))
$applicationRoot = Join-Path $temporaryRoot 'app'
$reportsRoot = Join-Path $temporaryRoot 'reports'
$applicationExecutable = Join-Path $applicationRoot 'BakuretsuOsakanaKobo.exe'

function Assert-Measurement {
    param(
        [Parameter(Mandatory = $true)] [bool]$Condition,
        [Parameter(Mandatory = $true)] [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-ExistingData {
    param(
        [Parameter(Mandatory = $true)] [string]$DataRoot,
        [Parameter(Mandatory = $true)] [bool]$IncludeMutedVideo
    )

    [IO.Directory]::CreateDirectory($DataRoot) | Out-Null
    $settings = [ordered]@{
        SchemaVersion = 1
        ThumbnailIntervalPercent = 1.0
        ThumbnailPreviewWidthPercent = 15.0
        LastAutomaticUpdateCheckAttemptUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $profiles = [ordered]@{
        SchemaVersion = 1
        Profiles = if ($IncludeMutedVideo) {
            @([ordered]@{
                VideoPath = [IO.Path]::GetFullPath($videoPath)
                VolumePercent = 100
                IsMuted = $true
                StartPositionMilliseconds = $null
            })
        } else {
            @()
        }
    }
    $recent = [ordered]@{
        SchemaVersion = 1
        Files = if ($IncludeMutedVideo) { @([IO.Path]::GetFullPath($videoPath)) } else { @() }
    }
    $playlist = [ordered]@{
        SchemaVersion = 1
        Entries = @()
        Loop = $false
    }

    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $DataRoot 'settings.json') -Encoding utf8
    $profiles | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $DataRoot 'video-profiles.json') -Encoding utf8
    $recent | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $DataRoot 'recent-files.json') -Encoding utf8
    $playlist | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $DataRoot 'playlist.json') -Encoding utf8
}

function Get-Statistics {
    param(
        [Parameter(Mandatory = $true)] [object[]]$Items,
        [Parameter(Mandatory = $true)] [string]$PropertyName
    )

    $values = @($Items | ForEach-Object { [double]($_.durationsMs.$PropertyName) } | Sort-Object)
    $middle = [int][Math]::Floor($values.Count / 2)
    $median = if ($values.Count % 2 -eq 0) {
        ($values[$middle - 1] + $values[$middle]) / 2
    } else {
        $values[$middle]
    }
    return [ordered]@{
        minimum = [Math]::Round($values[0], 3)
        median = [Math]::Round($median, 3)
        maximum = [Math]::Round($values[-1], 3)
    }
}

Assert-Measurement (Test-Path -LiteralPath $videoPath -PathType Leaf) "Representative video is missing: $videoPath"
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Product restore failed with exit code $LASTEXITCODE." }
& dotnet publish $project `
    --configuration Release `
    --no-restore `
    --runtime win-x64 `
    --self-contained false `
    -p:PublishSingleFile=false `
    --output $applicationRoot
if ($LASTEXITCODE -ne 0) { throw "Product publish failed with exit code $LASTEXITCODE." }

Assert-Measurement (Test-Path -LiteralPath $applicationExecutable -PathType Leaf) `
    "Published product executable is missing: $applicationExecutable"
[IO.Directory]::CreateDirectory($reportsRoot) | Out-Null
[IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null

$conditions = @(
    [pscustomobject]@{ id = 'first-empty'; existingData = $false; fileArgument = $false },
    [pscustomobject]@{ id = 'existing-empty'; existingData = $true; fileArgument = $false },
    [pscustomobject]@{ id = 'existing-file-muted'; existingData = $true; fileArgument = $true }
)
$results = [Collections.Generic.List[object]]::new()
$dataRoot = Join-Path $applicationRoot 'data'

try {
    foreach ($condition in $conditions) {
        for ($run = 1; $run -le $Runs; $run++) {
            $resolvedDataRoot = [IO.Path]::GetFullPath($dataRoot)
            Assert-Measurement ([IO.Path]::GetDirectoryName($resolvedDataRoot) -eq [IO.Path]::GetFullPath($applicationRoot)) `
                "Unexpected portable data cleanup target: $resolvedDataRoot"
            if (Test-Path -LiteralPath $resolvedDataRoot) {
                Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
            }
            if ($condition.existingData) {
                Write-ExistingData $resolvedDataRoot $condition.fileArgument
            }

            $reportPath = Join-Path $reportsRoot "$($condition.id)-$run.json"
            $startInfo = [Diagnostics.ProcessStartInfo]::new($applicationExecutable)
            $startInfo.WorkingDirectory = $applicationRoot
            $startInfo.UseShellExecute = $false
            $startInfo.Environment['BOK_STARTUP_TRACE_PATH'] = $reportPath
            if ($condition.fileArgument) {
                $startInfo.ArgumentList.Add($videoPath)
            }

            $process = $null
            $process = [Diagnostics.Process]::Start($startInfo)
            Assert-Measurement ($null -ne $process) "Could not start $($condition.id), run $run."
            try {
                $clock = [Diagnostics.Stopwatch]::StartNew()
                while ($clock.Elapsed -lt [TimeSpan]::FromSeconds(30) -and
                       -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
                    if ($process.HasExited) {
                        throw "Product exited before startup report for $($condition.id), run $run; exit $($process.ExitCode)."
                    }
                    Start-Sleep -Milliseconds 25
                }
                Assert-Measurement (Test-Path -LiteralPath $reportPath -PathType Leaf) `
                    "Timed out waiting for startup report for $($condition.id), run $run."

                $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
                Assert-Measurement ($report.complete -eq $true) `
                    "Startup report was incomplete for $($condition.id), run $run."
                Assert-Measurement ([bool]$report.hasFileArgument -eq [bool]$condition.fileArgument) `
                    "Startup report argument classification was incorrect for $($condition.id), run $run."
                $results.Add([pscustomobject]@{
                    condition = $condition.id
                    run = $run
                    processId = $report.processId
                    durationsMs = $report.durationsMs
                    milestonesMs = $report.milestonesMs
                })

                if ($condition.fileArgument -and $FileReadyDwellMilliseconds -gt 0) {
                    Start-Sleep -Milliseconds $FileReadyDwellMilliseconds
                }

                $process.Refresh()
                Assert-Measurement $process.CloseMainWindow() `
                    "Could not request normal product shutdown for $($condition.id), run $run."
                Assert-Measurement $process.WaitForExit(30000) `
                    "Product did not exit normally for $($condition.id), run $run."
                Assert-Measurement ($process.ExitCode -eq 0) `
                    "Product exited with code $($process.ExitCode) for $($condition.id), run $run."

                Write-Output (
                    "$($condition.id) $run/${Runs}: show=$([Math]::Round([double]$report.durationsMs.processStartToShowReturned, 1)) ms, " +
                    "interactive=$([Math]::Round([double]$report.durationsMs.processStartToInteractiveReady, 1)) ms, " +
                    "window=$([Math]::Round([double]$report.durationsMs.windowConstruction, 1)) ms, " +
                    "playback=$([Math]::Round([double]$report.durationsMs.playbackInitialization, 1)) ms, " +
                    "thumbnail=$([Math]::Round([double]$report.durationsMs.thumbnailInitialization, 1)) ms")
            } finally {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        $process.Kill($true)
                        $process.WaitForExit(5000) | Out-Null
                    }
                    $process.Dispose()
                }
            }

            if ($BetweenRunsMilliseconds -gt 0) {
                Start-Sleep -Milliseconds $BetweenRunsMilliseconds
            }
        }
    }

    $segmentNames = @(
        'windowConstruction',
        'updateResultRead',
        'appSettingsInitialization',
        'videoProfilesInitialization',
        'recentFilesInitialization',
        'playlistInitialization',
        'playbackInitialization',
        'thumbnailInitialization',
        'serviceConfiguration',
        'showCall',
        'showToContentRendered',
        'initialLaunchHandling',
        'processStartToShowReturned',
        'processStartToInteractiveReady'
    )
    $summary = @($conditions | ForEach-Object {
        $condition = $_
        $items = @($results | Where-Object condition -eq $condition.id)
        $statistics = [ordered]@{}
        foreach ($segmentName in $segmentNames) {
            $statistics[$segmentName] = Get-Statistics $items $segmentName
        }
        [ordered]@{
            condition = $condition.id
            runs = $items.Count
            existingData = [bool]$condition.existingData
            fileArgument = [bool]$condition.fileArgument
            statisticsMs = $statistics
        }
    })

    $contributors = @($segmentNames |
        Where-Object { $_ -notlike 'processStartTo*' -and $_ -ne 'initialLaunchHandling' } |
        ForEach-Object {
            $segmentName = $_
            [pscustomobject]@{
                segment = $segmentName
                largestMedianMs = [Math]::Round(
                    [double](($summary | ForEach-Object { $_.statisticsMs[$segmentName].median } |
                        Measure-Object -Maximum).Maximum),
                    3)
            }
        } |
        Sort-Object largestMedianMs -Descending |
        Select-Object -First 5)

    [ordered]@{
        generatedAt = [DateTimeOffset]::Now.ToString('O')
        method = 'Separate product processes using a framework-dependent win-x64 publish. first-empty removes portable data before each run. Existing-data conditions seed four valid schema-1 documents. File-argument runs preseed a muted video profile. Interactive readiness is the latest of ContentRendered, Dispatcher ApplicationIdle, completed initial launch handling, and non-blocking automatic-update-check start. OS file cache is not cleared, so repeated runs are cold-like process starts rather than hardware cold boots.'
        runsPerCondition = $Runs
        betweenRunsMilliseconds = $BetweenRunsMilliseconds
        fileReadyDwellMilliseconds = $FileReadyDwellMilliseconds
        existingInteractiveThresholdMs = 1500
        results = $results
        summary = $summary
        largestMedianContributors = $contributors
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding utf8

    Write-Output "Wrote $output"
} finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    if ([IO.Path]::GetDirectoryName($resolvedTemporaryRoot) -eq $temporaryParent -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot) -match '^[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
