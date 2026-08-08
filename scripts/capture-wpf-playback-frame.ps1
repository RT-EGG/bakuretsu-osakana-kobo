param(
    [Parameter(Mandatory = $true)]
    [string] $VideoPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [int] $WaitSeconds = 5,

    [string] $ApplicationPath = (
        'spikes/LibVlcWpfSpike/bin/x64/Release/net10.0-windows/win-x64/LibVlcWpfSpike.exe')
)

$ErrorActionPreference = 'Stop'

$resolvedVideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
$resolvedApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    throw "Output directory does not exist: $outputDirectory"
}

$logPath = [IO.Path]::ChangeExtension($resolvedOutputPath, '.startup.log')
$env:BOK_WPF_VALIDATION_LOG = $logPath
$process = Start-Process `
    -FilePath $resolvedApplicationPath `
    -ArgumentList $resolvedVideoPath `
    -PassThru

try {
    Start-Sleep -Seconds $WaitSeconds

    $log = Get-Content -LiteralPath $logPath -Raw
    $handleText = [regex]::Match(
        $log,
        'Dispatcher ready: visible=True, handle=(\d+)').Groups[1].Value
    if (-not $handleText) {
        throw "Visible WPF handle was not recorded. Log: $log"
    }

    if (-not ('WpfPlaybackCapture' -as [type])) {
        Add-Type -AssemblyName System.Drawing
        Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WpfPlaybackCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
}
'@
    }

    $handle = [IntPtr]::new([long]$handleText)
    $rect = New-Object WpfPlaybackCapture+RECT
    if (-not [WpfPlaybackCapture]::GetWindowRect($handle, [ref] $rect)) {
        throw 'GetWindowRect failed while the WPF window was alive.'
    }

    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try {
            if (-not [WpfPlaybackCapture]::PrintWindow($handle, $hdc, 2)) {
                throw 'PrintWindow failed.'
            }
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }

        $bitmap.Save($resolvedOutputPath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    $process.Refresh()
    [pscustomobject]@{
        Video = $resolvedVideoPath
        Screenshot = $resolvedOutputPath
        ProcessId = $process.Id
        Responding = $process.Responding
        WindowWidth = $rect.Right - $rect.Left
        WindowHeight = $rect.Bottom - $rect.Top
    }
}
finally {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Remove-Item Env:BOK_WPF_VALIDATION_LOG -ErrorAction SilentlyContinue
}
