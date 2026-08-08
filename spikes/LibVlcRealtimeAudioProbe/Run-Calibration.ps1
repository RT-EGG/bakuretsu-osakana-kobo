param(
    [Parameter(Mandatory = $true)]
    [string]$MediaPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputJson,

    [ValidateRange(1, 600)]
    [int]$DurationSeconds = 60,

    [ValidateRange(100, 500)]
    [int]$VolumePercent = 500,

    [ValidateRange(-40, 0)]
    [double]$AttenuationDb = -20,

    [ValidateRange(1, 100)]
    [double]$MaximumEndpointVolumePercent = 5
)

$ErrorActionPreference = 'Stop'

$probePath = Join-Path $PSScriptRoot 'bin\x64\Release\net10.0-windows\win-x64\LibVlcRealtimeAudioProbe.exe'
$resolvedProbe = (Resolve-Path -LiteralPath $probePath).Path
$resolvedMedia = (Resolve-Path -LiteralPath $MediaPath).Path
$resolvedOutput = [IO.Path]::GetFullPath($OutputJson)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$calibrationForm = New-Object System.Windows.Forms.Form
$calibrationForm.Text = '500%音量 安全校正'
$calibrationForm.Size = New-Object System.Drawing.Size(620, 320)
$calibrationForm.StartPosition = 'CenterScreen'
$calibrationForm.TopMost = $true
$calibrationForm.FormBorderStyle = 'FixedDialog'
$calibrationForm.MaximizeBox = $false

$statusLabel = New-Object System.Windows.Forms.Label
$statusLabel.Location = New-Object System.Drawing.Point(25, 20)
$statusLabel.Size = New-Object System.Drawing.Size(555, 150)
$statusLabel.TextAlign = 'MiddleCenter'
$statusLabel.Font = New-Object System.Drawing.Font('Yu Gothic UI', 15)
$calibrationForm.Controls.Add($statusLabel)

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point(35, 180)
$progressBar.Size = New-Object System.Drawing.Size(535, 25)
$progressBar.Minimum = 0
$progressBar.Maximum = $DurationSeconds * 10
$calibrationForm.Controls.Add($progressBar)

$stopButton = New-Object System.Windows.Forms.Button
$stopButton.Text = '停止'
$stopButton.Location = New-Object System.Drawing.Point(225, 220)
$stopButton.Size = New-Object System.Drawing.Size(150, 42)
$stopButton.Font = New-Object System.Drawing.Font('Yu Gothic UI', 12)
$calibrationForm.Controls.Add($stopButton)

$script:calibrationStopRequested = $false
$stopButton.Add_Click({ $script:calibrationStopRequested = $true })
$calibrationForm.Add_FormClosing({
    if (-not $script:calibrationCompleted) {
        $script:calibrationStopRequested = $true
    }
})

$calibrationForm.Show()
for ($countdown = 3; $countdown -ge 1; $countdown--) {
    $statusLabel.Text = "${countdown}秒後に開始します`r`nWindows音量上限 $MaximumEndpointVolumePercent% / 追加減衰 $AttenuationDb dB`r`n音量を少しずつ調整してください"
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Seconds 1
    if ($script:calibrationStopRequested) {
        $calibrationForm.Close()
        exit 2
    }
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $resolvedProbe
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add($resolvedMedia)
$startInfo.ArgumentList.Add($resolvedOutput)
$startInfo.ArgumentList.Add($VolumePercent.ToString([Globalization.CultureInfo]::InvariantCulture))
$startInfo.ArgumentList.Add('wasapi-smoke')
$startInfo.ArgumentList.Add($AttenuationDb.ToString([Globalization.CultureInfo]::InvariantCulture))
$startInfo.ArgumentList.Add($MaximumEndpointVolumePercent.ToString([Globalization.CultureInfo]::InvariantCulture))

$calibrationProcess = New-Object System.Diagnostics.Process
$calibrationProcess.StartInfo = $startInfo
if (-not $calibrationProcess.Start()) {
    throw '校正プロセスを開始できませんでした。'
}

$stopwatch = [Diagnostics.Stopwatch]::StartNew()
while (-not $calibrationProcess.HasExited -and -not $script:calibrationStopRequested) {
    $elapsed = [Math]::Min($DurationSeconds, $stopwatch.Elapsed.TotalSeconds)
    $remaining = [Math]::Max(0, [Math]::Ceiling($DurationSeconds - $elapsed))
    $progressBar.Value = [Math]::Min($progressBar.Maximum, [int]($elapsed * 10))
    $statusLabel.Text = "再生中: 残り約 $remaining 秒`r`nWindows音量上限 $MaximumEndpointVolumePercent% / 500%処理 / 追加減衰 $AttenuationDb dB`r`n聞こえた位置で調整を止めてください"
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 100
}

if ($script:calibrationStopRequested -and -not $calibrationProcess.HasExited) {
    $calibrationProcess.Kill($true)
}
$calibrationProcess.WaitForExit()
$standardOutput = $calibrationProcess.StandardOutput.ReadToEnd()
$standardError = $calibrationProcess.StandardError.ReadToEnd()

$script:calibrationCompleted = $true
$statusLabel.Text = if ($script:calibrationStopRequested) {
    '停止しました'
} elseif ($calibrationProcess.ExitCode -eq 0) {
    '校正再生と自動測定が完了しました'
} else {
    '校正プロセスがエラーで終了しました'
}
$progressBar.Value = $progressBar.Maximum
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 2
$calibrationForm.Close()

if ($standardOutput) { Write-Output $standardOutput.TrimEnd() }
if ($standardError) { Write-Error $standardError.TrimEnd() }
exit $calibrationProcess.ExitCode
