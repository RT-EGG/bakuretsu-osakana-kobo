[CmdletBinding()]
param(
    [ValidateRange(1, 20)]
    [int]$Runs = 5,

    [string]$OutputPath = "docs/phase2/results/wpf-performance-measurement.json",

    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "spikes/LibVlcWpfSpike/LibVlcWpfSpike.csproj"
$exePath = Join-Path $repoRoot "spikes/LibVlcWpfSpike/bin/x64/Release/net10.0-windows/win-x64/LibVlcWpfSpike.exe"
$temporaryRoot = Join-Path $repoRoot ".tmp/wpf-performance"
$resolvedOutputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))

if (-not $NoBuild) {
    dotnet build $projectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "WPF spike build failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutputPath) -Force | Out-Null

$conditions = @(
    [pscustomobject]@{
        id = "mp4-1080p60-60s"
        video = Join-Path $repoRoot "test-assets/generated/phase2-1920x1080-60s-60fps-h264-aac.mp4"
    },
    [pscustomobject]@{
        id = "mp4-4k60-60s"
        video = Join-Path $repoRoot "test-assets/generated/phase2-3840x2160-60s-60fps-h264-aac.mp4"
    },
    [pscustomobject]@{
        id = "wmv-1080p60-60s"
        video = Join-Path $repoRoot "test-assets/generated/phase2-1920x1080-60s-60fps-wmv9-wma.wmv"
    }
)

foreach ($condition in $conditions) {
    if (-not (Test-Path -LiteralPath $condition.video -PathType Leaf)) {
        throw "Representative video is missing: $($condition.video)"
    }
}

function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return 0.0 }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) { return [double]$ordered[$middle] }
    return ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2.0
}

function Get-Statistics($Items, [string]$PropertyName) {
    [double[]]$values = @($Items | ForEach-Object { [double]$_.$PropertyName })
    [pscustomobject]@{
        median = [Math]::Round((Get-Median $values), 3)
        maximum = [Math]::Round(($values | Measure-Object -Maximum).Maximum, 3)
    }
}

$logicalProcessors = [Environment]::ProcessorCount
$allRuns = [Collections.Generic.List[object]]::new()

foreach ($condition in $conditions) {
    for ($run = 1; $run -le $Runs; $run++) {
        $reportPath = Join-Path $temporaryRoot "$($condition.id)-run-$run.json"
        Remove-Item -LiteralPath $reportPath -Force -ErrorAction SilentlyContinue

        $startInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
        $startInfo.ArgumentList.Add("--video")
        $startInfo.ArgumentList.Add($condition.video)
        $startInfo.ArgumentList.Add("--performance-report")
        $startInfo.ArgumentList.Add($reportPath)

        $process = [Diagnostics.Process]::Start($startInfo)
        $standardOutputDrain = $process.StandardOutput.ReadToEndAsync()
        $standardErrorDrain = $process.StandardError.ReadToEndAsync()
        $wallClock = [Diagnostics.Stopwatch]::StartNew()
        $lastWallMs = 0.0
        $lastCpuMs = 0.0
        $cpuSamples = [Collections.Generic.List[double]]::new()
        $workingSetSamples = [Collections.Generic.List[double]]::new()
        $gpuSamples = [Collections.Generic.List[double]]::new()
        $gpuDecoderSamples = [Collections.Generic.List[double]]::new()
        $resourceSamples = [Collections.Generic.List[object]]::new()

        while (-not $process.HasExited) {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) { break }

            $wallMs = $wallClock.Elapsed.TotalMilliseconds
            $cpuMs = $process.TotalProcessorTime.TotalMilliseconds
            $cpuPercent = 0.0
            if ($wallMs -gt $lastWallMs) {
                $cpuPercent = (($cpuMs - $lastCpuMs) / ($wallMs - $lastWallMs)) * 100.0 / $logicalProcessors
                $cpuSamples.Add([Math]::Max(0.0, $cpuPercent))
            }
            $workingSetSamples.Add([double]$process.WorkingSet64)
            $lastWallMs = $wallMs
            $lastCpuMs = $cpuMs

            $gpuPercent = 0.0
            $gpuDecoderPercent = 0.0
            $gpuLine = & nvidia-smi --query-gpu=utilization.gpu,utilization.decoder --format=csv,noheader,nounits 2>$null | Select-Object -First 1
            if ($LASTEXITCODE -eq 0 -and $gpuLine -match '^\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)') {
                $gpuPercent = [double]$Matches[1]
                $gpuDecoderPercent = [double]$Matches[2]
                $gpuSamples.Add($gpuPercent)
                $gpuDecoderSamples.Add($gpuDecoderPercent)
            }
            $resourceSamples.Add([pscustomobject]@{
                elapsedMs = $wallMs
                cpuPercent = [Math]::Max(0.0, $cpuPercent)
                gpuPercent = $gpuPercent
                gpuDecoderPercent = $gpuDecoderPercent
                workingSetBytes = [double]$process.WorkingSet64
            })
        }

        $process.WaitForExit()
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "Performance report was not produced for $($condition.id), run $run (exit $($process.ExitCode))."
        }

        $appReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $idleSamples = @($resourceSamples | Where-Object {
            $_.elapsedMs -ge $appReport.ProcessStartToDispatcherReadyMs -and
            $_.elapsedMs -lt $appReport.OpenRequestedProcessElapsedMs
        })
        $playbackSamples = @($resourceSamples | Where-Object {
            $_.elapsedMs -ge $appReport.PlaybackReadyProcessElapsedMs -and
            $_.elapsedMs -lt ($appReport.PlaybackReadyProcessElapsedMs + $appReport.PlaybackSamplingDelayMs)
        })
        $allRuns.Add([pscustomobject]@{
            condition = $condition.id
            run = $run
            processExitCode = $process.ExitCode
            app = $appReport
            processCpuPercentAverage = if ($cpuSamples.Count) { [Math]::Round(($cpuSamples | Measure-Object -Average).Average, 3) } else { 0.0 }
            processCpuPercentMaximum = if ($cpuSamples.Count) { [Math]::Round(($cpuSamples | Measure-Object -Maximum).Maximum, 3) } else { 0.0 }
            systemGpuPercentAverage = if ($gpuSamples.Count) { [Math]::Round(($gpuSamples | Measure-Object -Average).Average, 3) } else { 0.0 }
            systemGpuPercentMaximum = if ($gpuSamples.Count) { [Math]::Round(($gpuSamples | Measure-Object -Maximum).Maximum, 3) } else { 0.0 }
            systemGpuDecoderPercentAverage = if ($gpuDecoderSamples.Count) { [Math]::Round(($gpuDecoderSamples | Measure-Object -Average).Average, 3) } else { 0.0 }
            systemGpuDecoderPercentMaximum = if ($gpuDecoderSamples.Count) { [Math]::Round(($gpuDecoderSamples | Measure-Object -Maximum).Maximum, 3) } else { 0.0 }
            sampledWorkingSetBytesMaximum = if ($workingSetSamples.Count) { [long](($workingSetSamples | Measure-Object -Maximum).Maximum) } else { 0L }
            idleCpuPercentAverage = if ($idleSamples.Count) { [Math]::Round(($idleSamples.cpuPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            idleGpuPercentAverage = if ($idleSamples.Count) { [Math]::Round(($idleSamples.gpuPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            idleGpuDecoderPercentAverage = if ($idleSamples.Count) { [Math]::Round(($idleSamples.gpuDecoderPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            idleWorkingSetMiBAverage = if ($idleSamples.Count) { [Math]::Round((($idleSamples.workingSetBytes | Measure-Object -Average).Average / 1MB), 3) } else { 0.0 }
            playbackCpuPercentAverage = if ($playbackSamples.Count) { [Math]::Round(($playbackSamples.cpuPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            playbackCpuPercentMaximum = if ($playbackSamples.Count) { [Math]::Round(($playbackSamples.cpuPercent | Measure-Object -Maximum).Maximum, 3) } else { 0.0 }
            playbackGpuPercentAverage = if ($playbackSamples.Count) { [Math]::Round(($playbackSamples.gpuPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            playbackGpuDecoderPercentAverage = if ($playbackSamples.Count) { [Math]::Round(($playbackSamples.gpuDecoderPercent | Measure-Object -Average).Average, 3) } else { 0.0 }
            playbackWorkingSetMiBAverage = if ($playbackSamples.Count) { [Math]::Round((($playbackSamples.workingSetBytes | Measure-Object -Average).Average / 1MB), 3) } else { 0.0 }
        })

        Write-Host "$($condition.id) run $run/${Runs}: success=$($appReport.Success), start=$([Math]::Round($appReport.ProcessStartToDispatcherReadyMs)) ms, video=$([Math]::Round([Math]::Max($appReport.OpenToVideoOutputMs, $appReport.OpenToFirstPlaybackClockMs))) ms"
    }
}

$summary = foreach ($condition in $conditions) {
    $items = @($allRuns | Where-Object condition -eq $condition.id)
    $derived = @($items | ForEach-Object {
        [pscustomobject]@{
            processStartToWindowLoadedMs = $_.app.ProcessStartToWindowLoadedMs
            processStartToDispatcherReadyMs = $_.app.ProcessStartToDispatcherReadyMs
            openToVideoReadyMs = [Math]::Max($_.app.OpenToVideoOutputMs, $_.app.OpenToFirstPlaybackClockMs)
            openToAudioOutputMs = $_.app.OpenToAudioOutputMs
            seek50PercentMs = $_.app.Seek50PercentMs
            seek90PercentMs = $_.app.Seek90PercentMs
            rateChangeReflectMs = $_.app.RateChangeReflectMs
            volumeChangeReflectMs = $_.app.VolumeChangeReflectMs
            processCpuPercentAverage = $_.processCpuPercentAverage
            processCpuPercentMaximum = $_.processCpuPercentMaximum
            systemGpuPercentAverage = $_.systemGpuPercentAverage
            systemGpuPercentMaximum = $_.systemGpuPercentMaximum
            systemGpuDecoderPercentAverage = $_.systemGpuDecoderPercentAverage
            systemGpuDecoderPercentMaximum = $_.systemGpuDecoderPercentMaximum
            sampledWorkingSetMiBMaximum = $_.sampledWorkingSetBytesMaximum / 1MB
            idleCpuPercentAverage = $_.idleCpuPercentAverage
            idleGpuPercentAverage = $_.idleGpuPercentAverage
            idleGpuDecoderPercentAverage = $_.idleGpuDecoderPercentAverage
            idleWorkingSetMiBAverage = $_.idleWorkingSetMiBAverage
            playbackCpuPercentAverage = $_.playbackCpuPercentAverage
            playbackCpuPercentMaximum = $_.playbackCpuPercentMaximum
            playbackGpuPercentAverage = $_.playbackGpuPercentAverage
            playbackGpuDecoderPercentAverage = $_.playbackGpuDecoderPercentAverage
            playbackWorkingSetMiBAverage = $_.playbackWorkingSetMiBAverage
        }
    })

    [pscustomobject]@{
        condition = $condition.id
        successfulRuns = @($items | Where-Object { $_.app.Success -and $_.processExitCode -eq 0 }).Count
        totalRuns = $items.Count
        processStartToWindowLoadedMs = Get-Statistics $derived "processStartToWindowLoadedMs"
        processStartToDispatcherReadyMs = Get-Statistics $derived "processStartToDispatcherReadyMs"
        openToVideoReadyMs = Get-Statistics $derived "openToVideoReadyMs"
        openToAudioOutputMs = Get-Statistics $derived "openToAudioOutputMs"
        seek50PercentMs = Get-Statistics $derived "seek50PercentMs"
        seek90PercentMs = Get-Statistics $derived "seek90PercentMs"
        rateChangeReflectMs = Get-Statistics $derived "rateChangeReflectMs"
        volumeChangeReflectMs = Get-Statistics $derived "volumeChangeReflectMs"
        processCpuPercentAverage = Get-Statistics $derived "processCpuPercentAverage"
        processCpuPercentMaximum = Get-Statistics $derived "processCpuPercentMaximum"
        systemGpuPercentAverage = Get-Statistics $derived "systemGpuPercentAverage"
        systemGpuPercentMaximum = Get-Statistics $derived "systemGpuPercentMaximum"
        systemGpuDecoderPercentAverage = Get-Statistics $derived "systemGpuDecoderPercentAverage"
        systemGpuDecoderPercentMaximum = Get-Statistics $derived "systemGpuDecoderPercentMaximum"
        sampledWorkingSetMiBMaximum = Get-Statistics $derived "sampledWorkingSetMiBMaximum"
        idleCpuPercentAverage = Get-Statistics $derived "idleCpuPercentAverage"
        idleGpuPercentAverage = Get-Statistics $derived "idleGpuPercentAverage"
        idleGpuDecoderPercentAverage = Get-Statistics $derived "idleGpuDecoderPercentAverage"
        idleWorkingSetMiBAverage = Get-Statistics $derived "idleWorkingSetMiBAverage"
        playbackCpuPercentAverage = Get-Statistics $derived "playbackCpuPercentAverage"
        playbackCpuPercentMaximum = Get-Statistics $derived "playbackCpuPercentMaximum"
        playbackGpuPercentAverage = Get-Statistics $derived "playbackGpuPercentAverage"
        playbackGpuDecoderPercentAverage = Get-Statistics $derived "playbackGpuDecoderPercentAverage"
        playbackWorkingSetMiBAverage = Get-Statistics $derived "playbackWorkingSetMiBAverage"
    }
}

$cpuName = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$computerSystem = Get-CimInstance Win32_ComputerSystem
$operatingSystem = Get-CimInstance Win32_OperatingSystem
$videoController = Get-CimInstance Win32_VideoController | Where-Object Name -Match 'NVIDIA' | Select-Object -First 1
$zPartition = Get-Partition -DriveLetter Z | Select-Object -First 1
$zDisk = $zPartition | Get-Disk
$activePowerScheme = (powercfg /getactivescheme) -join " "
Add-Type -AssemblyName System.Windows.Forms

$result = [ordered]@{
    generatedAt = [DateTimeOffset]::Now.ToString("O")
    measurementDefinition = [ordered]@{
        coldCondition = "Each run starts a new WPF process. Windows file-system cache is not flushed; run 1 is retained and runs 2-5 may use an OS-warm cache."
        videoReady = "Later of LibVLC Vout-created and playback-clock >= 100 ms. Prior PrintWindow validation establishes that this Vout is connected to the WPF VideoView; this is an event proxy, not a photodiode measurement."
        seekReady = "Time from Position assignment until the playback clock reaches seek target + 100 ms."
        cpu = "Process CPU, normalized to all logical processors, sampled approximately every 200 ms."
        gpu = "Whole NVIDIA GPU and video-decoder utilization from nvidia-smi; other desktop activity may contribute."
        resourceWindow = "A 2-second idle window after Dispatcher-ready and a separate 3-second steady-playback window before seeks; startup/seek aggregate maxima are retained separately."
    }
    baselinePc = [ordered]@{
        motherboard = "$($computerSystem.Manufacturer) $($computerSystem.Model)".Trim()
        cpu = $cpuName
        logicalProcessors = $logicalProcessors
        physicalMemoryBytes = [long]$computerSystem.TotalPhysicalMemory
        gpu = $videoController.Name
        gpuDriverVersion = $videoController.DriverVersion
        os = "$($operatingSystem.Caption) $($operatingSystem.Version) build $($operatingSystem.BuildNumber)"
        dotnetSdk = (& dotnet --version)
        windowsDesktopRuntime = ((& dotnet --list-runtimes) | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 10.*' } | Select-Object -Last 1)
        powerScheme = $activePowerScheme.Trim()
        mediaStorage = "$($zDisk.FriendlyName), $($zDisk.BusType), NTFS"
        displays = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
            [ordered]@{ name = $_.DeviceName; primary = $_.Primary; bounds = $_.Bounds.ToString() }
        })
    }
    conditions = @($conditions | ForEach-Object { [ordered]@{ id = $_.id; video = [IO.Path]::GetRelativePath($repoRoot, $_.video) } })
    runs = $allRuns
    summary = @($summary)
}

$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resolvedOutputPath -Encoding utf8
Write-Host "Wrote $resolvedOutputPath"

if (@($allRuns | Where-Object { -not $_.app.Success -or $_.processExitCode -ne 0 }).Count -gt 0) {
    exit 1
}
