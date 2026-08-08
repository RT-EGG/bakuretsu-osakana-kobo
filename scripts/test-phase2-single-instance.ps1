[CmdletBinding()]
param(
    [string]$OutputRoot = ".tmp/single-instance-data"
)

$ErrorActionPreference = "Stop"

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutMilliseconds, [string]$FailureMessage) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($watch.ElapsedMilliseconds -ge $TimeoutMilliseconds) {
            throw $FailureMessage
        }
        Start-Sleep -Milliseconds 50
    }
}

$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$exe = (Resolve-Path -LiteralPath (Join-Path $workspace "spikes/SingleInstanceDataSpike/bin/Release/net10.0-windows/win-x64/SingleInstanceDataSpike.exe")).Path
$resolvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $workspace $OutputRoot))
$runRoot = Join-Path $resolvedOutputRoot ("run-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff"))
$mediaRoot = Join-Path $runRoot "日本語 空白 フォルダー"
New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null

$file1 = Join-Path $mediaRoot "日本語 空白 動画.mp4"
$file2 = Join-Path $mediaRoot "二本目 動画.wmv"
Copy-Item -LiteralPath (Join-Path $workspace "test-assets/generated/phase2-1920x1080-5s-30fps-h264-aac.mp4") -Destination $file1
Copy-Item -LiteralPath (Join-Path $workspace "test-assets/generated/phase2-1920x1080-5s-30fps-wmv9-wma.wmv") -Destination $file2

$instanceId = "phase2-" + [Guid]::NewGuid().ToString("N")
$primaryLog = Join-Path $runRoot "primary-events.jsonl"
$reportPath = Join-Path $runRoot "report.json"
$primaryArgs = @(
    "--instance-id", (Quote-Argument $instanceId),
    "--event-log", (Quote-Argument $primaryLog),
    "--shutdown-after-ms", "30000"
) -join " "

$primary = $null
$secondaryRuns = @()
$errors = [Collections.Generic.List[string]]::new()
try {
    $primary = Start-Process -FilePath $exe -ArgumentList $primaryArgs -PassThru
    Wait-Until { Test-Path -LiteralPath $primaryLog } 5000 "Primary event log was not created."
    Wait-Until { (Get-Content -LiteralPath $primaryLog -ErrorAction SilentlyContinue | Measure-Object).Count -ge 2 } 5000 "Primary window did not finish initial launch handling."

    $instanceBytes = [Text.Encoding]::UTF8.GetBytes($instanceId)
    $instanceHash = [Security.Cryptography.SHA256]::HashData($instanceBytes)
    $pipeName = "BakuretsuOsakanaKobo." + [Convert]::ToHexString($instanceHash).Substring(0, 24)
    $malformedClient = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $malformedClient.Connect(2000)
        $invalidLength = [BitConverter]::GetBytes(0)
        $malformedClient.Write($invalidLength, 0, $invalidLength.Length)
        $malformedClient.Flush()
        $rejection = $malformedClient.ReadByte()
        if ($rejection -ne 0) {
            $errors.Add("Malformed IPC request was not rejected with ACK 0.")
        }
    }
    finally {
        $malformedClient.Dispose()
    }

    $scenarios = @(
        @{ Name = "activate-only"; Files = @() },
        @{ Name = "one-file"; Files = @($file1) },
        @{ Name = "multiple-files"; Files = @($file1, $file2) }
    )
    $expectedPrimaryEvents = 2
    foreach ($scenario in $scenarios) {
        $secondaryLog = Join-Path $runRoot ($scenario.Name + "-secondary.jsonl")
        $parts = @(
            "--instance-id", (Quote-Argument $instanceId),
            "--event-log", (Quote-Argument $secondaryLog)
        )
        foreach ($file in $scenario.Files) {
            $parts += Quote-Argument $file
        }
        $secondary = Start-Process -FilePath $exe -ArgumentList ($parts -join " ") -PassThru -Wait -WindowStyle Hidden
        $secondaryRuns += [pscustomobject]@{
            Name = $scenario.Name
            ProcessId = $secondary.Id
            ExitCode = $secondary.ExitCode
            Exited = $secondary.HasExited
        }
        $expectedPrimaryEvents++
        Wait-Until { (Get-Content -LiteralPath $primaryLog -ErrorAction SilentlyContinue | Measure-Object).Count -ge $expectedPrimaryEvents } 5000 "Primary did not record $($scenario.Name)."
    }

    $matchingProcesses = @(Get-Process -Name "SingleInstanceDataSpike" -ErrorAction SilentlyContinue)
    if ($matchingProcesses.Count -ne 1 -or $matchingProcesses[0].Id -ne $primary.Id) {
        $errors.Add("Expected only primary PID $($primary.Id), found: $($matchingProcesses.Id -join ', ').")
    }
}
catch {
    $errors.Add($_.Exception.ToString())
}
finally {
    if ($null -ne $primary -and -not $primary.HasExited) {
        $primary.CloseMainWindow() | Out-Null
        if (-not $primary.WaitForExit(5000)) {
            $primary.Kill($true)
            $primary.WaitForExit()
        }
    }
}

$events = @()
if (Test-Path -LiteralPath $primaryLog) {
    $events = @(Get-Content -LiteralPath $primaryLog | ForEach-Object { $_ | ConvertFrom-Json })
}
$requests = @($events | Where-Object { $_.eventName -eq "launch-request" })
$initial = @($requests | Where-Object { $_.payload.isInitialLaunch })
$activateOnly = @($requests | Where-Object { -not $_.payload.isInitialLaunch -and $_.payload.action -eq "activate-only" })
$oneFile = @($requests | Where-Object { -not $_.payload.isInitialLaunch -and $_.payload.action -eq "open-one" })
$multiple = @($requests | Where-Object { -not $_.payload.isInitialLaunch -and $_.payload.action -eq "ignore-multiple" })

$checks = [ordered]@{
    PrimaryStarted = @($events | Where-Object { $_.eventName -eq "primary-start" }).Count -eq 1
    InitialLaunchHandled = $initial.Count -eq 1
    SecondaryProcessesExited = @($secondaryRuns | Where-Object { -not $_.Exited -or $_.ExitCode -ne 0 }).Count -eq 0
    ActivateOnlyForwarded = $activateOnly.Count -eq 1
    JapaneseSpacePathForwardedExactly = $oneFile.Count -eq 1 -and $oneFile[0].payload.fileArguments[0] -ceq $file1
    MultipleArgumentsIgnored = $multiple.Count -eq 1 -and $multiple[0].payload.displayedPath -ceq $file1
    MalformedPayloadRejectedAndServerContinued =
        @($events | Where-Object { $_.eventName -eq "coordinator" -and $_.payload.message -like "server-request-rejected:*" }).Count -eq 1 -and
        $activateOnly.Count -eq 1 -and $oneFile.Count -eq 1 -and $multiple.Count -eq 1
    ExistingWindowActivationHandled = $activateOnly.Count -eq 1 -and $oneFile.Count -eq 1 -and $multiple.Count -eq 1 -and
        $activateOnly[0].payload.activationCount -ge 2 -and
        $oneFile[0].payload.activationCount -gt $activateOnly[0].payload.activationCount -and
        $multiple[0].payload.activationCount -gt $oneFile[0].payload.activationCount
    OnlyPrimaryRemained = $errors.Count -eq 0
}

foreach ($check in $checks.GetEnumerator()) {
    if (-not $check.Value) {
        $errors.Add("Check failed: $($check.Key)")
    }
}

$report = [ordered]@{
    StartedAt = if ($events.Count -gt 0) { $events[0].timestamp } else { $null }
    FinishedAt = [DateTimeOffset]::Now
    Passed = $errors.Count -eq 0
    InstanceId = $instanceId
    PrimaryProcessId = if ($null -ne $primary) { $primary.Id } else { $null }
    TestFile = $file1
    SecondaryRuns = $secondaryRuns
    Checks = $checks
    PrimaryEvents = $events
    Errors = $errors
}

$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | ConvertTo-Json -Depth 10
if (-not $report.Passed) {
    exit 1
}
