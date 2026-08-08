param(
    [Parameter(Mandatory = $true)]
    [string] $LibVlcDirectory
)

$ErrorActionPreference = 'Stop'

$resolvedDirectory = (Resolve-Path -LiteralPath $LibVlcDirectory).Path
$corePath = Join-Path $resolvedDirectory 'libvlccore.dll'
$pluginsDirectory = Join-Path $resolvedDirectory 'plugins'

if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    throw "libvlccore.dll was not found: $corePath"
}

if (-not (Test-Path -LiteralPath $pluginsDirectory -PathType Container)) {
    throw "LibVLC plugins directory was not found: $pluginsDirectory"
}

if (-not ('VlcPluginLicenseDelegate' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate IntPtr VlcPluginLicenseDelegate();
'@
}

$previousPath = $env:PATH
$env:PATH = "$resolvedDirectory;$previousPath"
$coreHandle = [IntPtr]::Zero
$loadedCount = 0
$unreadable = [System.Collections.Generic.List[string]]::new()
$forbidden = [System.Collections.Generic.List[object]]::new()
$observedLicenses = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

try {
    $coreHandle = [System.Runtime.InteropServices.NativeLibrary]::Load($corePath)

    foreach ($plugin in Get-ChildItem -LiteralPath $pluginsDirectory -Recurse -Filter '*.dll' -File) {
        $pluginHandle = [IntPtr]::Zero
        try {
            $pluginHandle = [System.Runtime.InteropServices.NativeLibrary]::Load($plugin.FullName)
            $entryPoint = [System.Runtime.InteropServices.NativeLibrary]::GetExport(
                $pluginHandle,
                'vlc_entry_license__3_0_0f')
            $licenseDelegate = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
                $entryPoint,
                [VlcPluginLicenseDelegate])
            $licensePointer = $licenseDelegate.Invoke()
            $license = [System.Runtime.InteropServices.Marshal]::PtrToStringAnsi($licensePointer)
            if ([string]::IsNullOrWhiteSpace($license)) {
                throw 'The plug-in returned an empty license string.'
            }

            $loadedCount++
            [void]$observedLicenses.Add($license)

            if ($license -match 'Affero General Public License' -or
                ($license -match 'GNU General Public License' -and
                 $license -notmatch 'GNU Lesser General Public License')) {
                $forbidden.Add([pscustomobject]@{
                    Path = $plugin.FullName.Substring($resolvedDirectory.Length + 1)
                    License = $license
                })
            }
        }
        catch {
            $unreadable.Add("$($plugin.FullName.Substring($resolvedDirectory.Length + 1)): $($_.Exception.Message)")
        }
        finally {
            if ($pluginHandle -ne [IntPtr]::Zero) {
                [System.Runtime.InteropServices.NativeLibrary]::Free($pluginHandle)
            }
        }
    }
}
finally {
    if ($coreHandle -ne [IntPtr]::Zero) {
        [System.Runtime.InteropServices.NativeLibrary]::Free($coreHandle)
    }
    $env:PATH = $previousPath
}

[pscustomobject]@{
    PluginsWithReadableLicense = $loadedCount
    UnreadablePlugins = $unreadable.Count
    ForbiddenPlugins = $forbidden.Count
} | Format-List

$observedLicenses | Sort-Object | ForEach-Object { "Observed license: $_" }

if ($loadedCount -eq 0) {
    throw 'No LibVLC plug-in licenses were inspected.'
}

if ($unreadable.Count -gt 0) {
    Write-Error ("Could not inspect some plug-ins:`n" + ($unreadable -join "`n"))
}

if ($forbidden.Count -gt 0) {
    $forbidden | Format-Table -AutoSize
    throw "GPL-only LibVLC plug-ins were found in the distribution."
}
