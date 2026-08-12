[CmdletBinding()]
param(
    [string]$OutputRoot = ".tmp/phase4-single-instance"
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Phase4WindowProbe
{
    public const uint WmClose = 0x0010;

    public static int GetForegroundProcessId()
    {
        var handle = GetForegroundWindow();
        GetWindowThreadProcessId(handle, out var processId);
        return unchecked((int)processId);
    }

    public static IntPtr FindProductWindow(int processId, string expectedTitlePart)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((handle, state) =>
        {
            GetWindowThreadProcessId(handle, out var ownerProcessId);
            if (ownerProcessId != unchecked((uint)processId) || !IsWindowVisible(handle))
            {
                return true;
            }

            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString().Contains(expectedTitlePart, StringComparison.Ordinal))
            {
                result = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool CloseWindow(IntPtr handle) => PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
"@

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutMilliseconds,
        [string]$FailureMessage
    )

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($watch.ElapsedMilliseconds -ge $TimeoutMilliseconds) {
            throw $FailureMessage
        }

        Start-Sleep -Milliseconds 50
    }
}

function Start-Product {
    param(
        [string]$ExecutablePath,
        [string[]]$Arguments = @()
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($ExecutablePath)
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument) | Out-Null
    }

    return [Diagnostics.Process]::Start($startInfo)
}

function Get-LaunchEvents {
    param([string]$LogPath)

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return @()
    }

    return @(
        Get-Content -LiteralPath $LogPath |
            ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.EventName -eq "launch-request-handled" }
    )
}

function Wait-ForSecondaryExit {
    param(
        [Diagnostics.Process]$Process,
        [string]$Scenario
    )

    if (-not $Process.WaitForExit(10000)) {
        throw "$Scenario secondary process did not exit."
    }

    if ($Process.ExitCode -ne 0) {
        throw "$Scenario secondary process exited with code $($Process.ExitCode)."
    }
}

$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$buildOutput = Join-Path $workspace "src/BakuretsuOsakanaKobo.App/bin/Release/net10.0-windows/win-x64"
$sourceExecutable = Join-Path $buildOutput "BakuretsuOsakanaKobo.exe"
if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw "Release product executable was not found. Build the solution first."
}

$existingProcesses = @(Get-Process -Name "BakuretsuOsakanaKobo" -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -ne 0) {
    throw "Close the running BakuretsuOsakanaKobo process before this validation."
}

$resolvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $workspace $OutputRoot))
$runRoot = Join-Path $resolvedOutputRoot ("run-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmssfff"))
$appRoot = Join-Path $runRoot "app"
$mediaRoot = Join-Path $runRoot "日本語 空白 フォルダー"
$dataRoot = Join-Path $appRoot "data"
New-Item -ItemType Directory -Path $appRoot, $mediaRoot, $dataRoot -Force | Out-Null
Get-ChildItem -LiteralPath $buildOutput |
    Where-Object { $_.Name -ne "data" } |
    Copy-Item -Destination $appRoot -Recurse -Force

$file1 = Join-Path $mediaRoot "初回 日本語 動画.mp4"
$file2 = Join-Path $mediaRoot "IPC 日本語 動画.wmv"
$missingFile = Join-Path $mediaRoot "欠損 日本語 動画.mp4"
Copy-Item -LiteralPath (Join-Path $workspace "test-assets/generated/phase2-1920x1080-5s-30fps-h264-aac.mp4") -Destination $file1
Copy-Item -LiteralPath (Join-Path $workspace "test-assets/generated/phase2-1920x1080-5s-30fps-wmv9-wma.wmv") -Destination $file2

$profileDocument = [ordered]@{
    SchemaVersion = 1
    Profiles = @(
        [ordered]@{ VideoPath = $file1; VolumePercent = 100; IsMuted = $true; StartPositionMilliseconds = $null },
        [ordered]@{ VideoPath = $file2; VolumePercent = 100; IsMuted = $true; StartPositionMilliseconds = $null }
    )
}
$profileDocument | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dataRoot "video-profiles.json") -Encoding utf8

$executable = Join-Path $appRoot "BakuretsuOsakanaKobo.exe"
$logPath = Join-Path $dataRoot "logs/app.log"
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$userSidHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($userSid))
$instanceName = "BakuretsuOsakanaKobo.SingleInstance.v1." + [Convert]::ToHexString($userSidHash).Substring(0, 24)
$primary = $null
$secondaryRuns = [Collections.Generic.List[object]]::new()
$foregroundObserved = $false
$errors = [Collections.Generic.List[string]]::new()
try {
    $primary = Start-Product -ExecutablePath $executable -Arguments @($file1)
    Wait-Until {
        try {
            $mutex = [Threading.Mutex]::OpenExisting("Local\$instanceName")
            $mutex.Dispose()
            return $true
        }
        catch [Threading.WaitHandleCannotBeOpenedException] {
            return $false
        }
    } 5000 "The primary process did not create its named mutex."

    $oneFile = Start-Product -ExecutablePath $executable -Arguments @($file2)
    Wait-ForSecondaryExit -Process $oneFile -Scenario "one-file"
    $secondaryRuns.Add([pscustomobject]@{ Scenario = "one-file"; ProcessId = $oneFile.Id; ExitCode = $oneFile.ExitCode })
    Wait-Until { (Get-LaunchEvents $logPath).Count -ge 2 } 15000 "The primary did not handle initial and IPC file requests."

    $malformedClient = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $instanceName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $malformedClient.Connect(2000)
        $invalidLength = [BitConverter]::GetBytes(0)
        $malformedClient.Write($invalidLength, 0, $invalidLength.Length)
        $malformedClient.Flush()
        if ($malformedClient.ReadByte() -ne 0) {
            throw "Malformed IPC did not receive rejection ACK 0."
        }
    }
    finally {
        $malformedClient.Dispose()
    }

    $activateOnly = Start-Product -ExecutablePath $executable
    $foregroundWatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $activateOnly.HasExited -and $foregroundWatch.ElapsedMilliseconds -lt 10000) {
        if ([Phase4WindowProbe]::GetForegroundProcessId() -eq $primary.Id) {
            $foregroundObserved = $true
        }

        Start-Sleep -Milliseconds 10
        $activateOnly.Refresh()
    }
    Wait-ForSecondaryExit -Process $activateOnly -Scenario "activate-only"
    $secondaryRuns.Add([pscustomobject]@{ Scenario = "activate-only"; ProcessId = $activateOnly.Id; ExitCode = $activateOnly.ExitCode })
    Wait-Until { (Get-LaunchEvents $logPath).Count -ge 3 } 5000 "The activate-only request was not handled after malformed IPC."
    $foregroundObserved = $foregroundObserved -or
        [Phase4WindowProbe]::GetForegroundProcessId() -eq $primary.Id

    $multiple = Start-Product -ExecutablePath $executable -Arguments @($file1, $file2)
    Wait-ForSecondaryExit -Process $multiple -Scenario "multiple-files"
    $secondaryRuns.Add([pscustomobject]@{ Scenario = "multiple-files"; ProcessId = $multiple.Id; ExitCode = $multiple.ExitCode })
    Wait-Until { (Get-LaunchEvents $logPath).Count -ge 4 } 5000 "The multiple-file request was not handled."

    $productProcesses = @(Get-Process -Name "BakuretsuOsakanaKobo" -ErrorAction SilentlyContinue)
    if ($productProcesses.Count -ne 1 -or $productProcesses[0].Id -ne $primary.Id) {
        throw "Expected only primary PID $($primary.Id), found: $($productProcesses.Id -join ', ')."
    }

    $launchEvents = Get-LaunchEvents $logPath
    if ($launchEvents[0].TargetPath -ne $file1 -or $launchEvents[1].TargetPath -ne $file2) {
        throw "Initial and queued IPC launch requests were not handled in order with exact paths."
    }
    if ($launchEvents[2].Message -notmatch "activate-only" -or $null -ne $launchEvents[2].TargetPath) {
        throw "The zero-argument launch request was not classified as activate-only."
    }
    if ($launchEvents[3].Message -notmatch "ignore-multiple" -or $null -ne $launchEvents[3].TargetPath) {
        throw "The multiple-argument launch request was not silently ignored."
    }

    $missing = Start-Product -ExecutablePath $executable -Arguments @($missingFile)
    Wait-ForSecondaryExit -Process $missing -Scenario "missing-file"
    $secondaryRuns.Add([pscustomobject]@{ Scenario = "missing-file"; ProcessId = $missing.Id; ExitCode = $missing.ExitCode })
    Wait-Until { (Get-LaunchEvents $logPath).Count -ge 5 } 5000 "The missing-file request was not handled."

    $recentFilesPath = Join-Path $dataRoot "recent-files.json"
    Wait-Until { Test-Path -LiteralPath $recentFilesPath } 5000 "The recent-file history was not created."
    $recentFiles = @((Get-Content -LiteralPath $recentFilesPath | ConvertFrom-Json).Files)
    if ($recentFiles.Count -ne 2 -or $recentFiles[0] -ne $file2 -or $recentFiles[1] -ne $file1) {
        throw "Recent-file history did not contain only successful opens in newest-first order."
    }
}
catch {
    $errors.Add($_.Exception.ToString())
}
finally {
    if ($null -ne $primary -and -not $primary.HasExited) {
        $window = [Phase4WindowProbe]::FindProductWindow($primary.Id, "爆裂おさかな工房")
        if ($window -ne [IntPtr]::Zero) {
            [Phase4WindowProbe]::CloseWindow($window) | Out-Null
        }
        if (-not $primary.WaitForExit(10000)) {
            $primary.Kill($true)
            $primary.WaitForExit()
            $errors.Add("The primary process required forced termination.")
        }
    }
}

$reportPath = Join-Path $runRoot "report.json"
$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow
    passed = $errors.Count -eq 0
    primaryProcessId = $primary.Id
    secondaryRuns = $secondaryRuns
    foregroundWindowHandleObserved = $foregroundObserved
    launchEvents = @(Get-LaunchEvents $logPath)
    recentFiles = if (Test-Path -LiteralPath (Join-Path $dataRoot "recent-files.json")) {
        @((Get-Content -LiteralPath (Join-Path $dataRoot "recent-files.json") | ConvertFrom-Json).Files)
    } else {
        @()
    }
    errors = $errors
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output "Report: $reportPath"

if ($errors.Count -ne 0) {
    throw ($errors -join [Environment]::NewLine)
}

Write-Output "Phase 4 single-instance product validation passed."
