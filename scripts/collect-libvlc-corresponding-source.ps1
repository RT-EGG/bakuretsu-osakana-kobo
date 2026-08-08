[CmdletBinding()]
param(
    [string]$VlcSourceArchive = (Join-Path $PSScriptRoot '..\.tmp\vlc-3.0.23.tar.xz'),

    [string]$ContribCacheDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputArchive
)

$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceArchive = (Resolve-Path -LiteralPath $VlcSourceArchive).Path
$output = [IO.Path]::GetFullPath($OutputArchive)
$tempRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ('.tmp\source-bundle-work-' + [guid]::NewGuid().ToString('N'))))

if (-not $output.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output archive must be inside the repository: $output"
}
if (Test-Path -LiteralPath $output) {
    throw "Output archive already exists: $output"
}

$vlcVersion = '3.0.23'
$vlcSourceSha256 = 'E891CAE6AA3CCDA69BF94173D5105CBC55C7A7D9B1D21B9B21666E69EFF3E7E0'
$nugetVersion = '3.0.23.1'
$nugetSha256 = '70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F'
$officialWin64Sha256 = 'EB4FD8A28291DA73608C733786A09610FEA865FBE94113BCB60B91C1EBB8404A'
$libVlcNugetCommit = '042f49a49609b2da7aeea0c94e51f809cf2e1575'
$libVlcSharpCommit = '59d70e96026229e7c232ce5074ecefbf6f8959b6'

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $sourceArchive).Hash -ne $vlcSourceSha256) {
    throw 'The VLC source archive does not match the pinned VideoLAN SHA-256.'
}

$bundleRoot = Join-Path $tempRoot 'bundle'
$extractRoot = Join-Path $tempRoot 'extract'
$contribRoot = Join-Path $bundleRoot 'vlc-contrib'
[IO.Directory]::CreateDirectory($bundleRoot) | Out-Null
[IO.Directory]::CreateDirectory($extractRoot) | Out-Null
[IO.Directory]::CreateDirectory($contribRoot) | Out-Null

$vlcRoot = Join-Path $bundleRoot 'vlc'
$libVlcSharpRoot = Join-Path $bundleRoot 'libvlcsharp'
$libVlcNugetRoot = Join-Path $bundleRoot 'libvlc-nuget'
[IO.Directory]::CreateDirectory($vlcRoot) | Out-Null
[IO.Directory]::CreateDirectory($libVlcSharpRoot) | Out-Null
[IO.Directory]::CreateDirectory($libVlcNugetRoot) | Out-Null
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $vlcRoot "vlc-$vlcVersion.tar.xz")

& tar -xf $sourceArchive -C $extractRoot
if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }

$sourceTree = Join-Path $extractRoot "vlc-$vlcVersion"
$sumFiles = Get-ChildItem -LiteralPath (Join-Path $sourceTree 'contrib\src') -Recurse -File -Filter 'SHA512SUMS'
$contrib = foreach ($sumFile in $sumFiles) {
    $package = Split-Path -Leaf (Split-Path -Parent $sumFile.FullName)
    foreach ($line in Get-Content -LiteralPath $sumFile.FullName) {
        if ($line -match '^([0-9a-fA-F]{128})\s+\*?(.+)$') {
            [pscustomobject]@{
                package = $package
                file = $Matches[2]
                sha512 = $Matches[1].ToUpperInvariant()
                url = "https://download.videolan.org/pub/contrib/$package/$([Uri]::EscapeDataString($Matches[2]))"
            }
        }
    }
}

$duplicateNames = @($contrib | Group-Object file | Where-Object Count -gt 1)
if ($duplicateNames.Count -ne 0) {
    throw "Contrib source filenames are not unique: $($duplicateNames.Name -join ', ')"
}
if ($contrib.Count -ne 126) {
    throw "Expected 126 pinned contrib source files, found $($contrib.Count)."
}

# A few upstream files are not mirrored under contrib/<rules-directory>/<filename>.
# These URLs are the exact primary URLs expanded from VLC 3.0.23's rules.mak files.
$sourceUrlOverrides = @{
    'AMF-1.4.34.tar.gz' = 'https://github.com/GPUOpen-LibrariesAndSDKs/AMF/releases/download/v1.4.34/AMF-headers.tar.gz'
    'libaom-3.5.0.tar.gz' = 'https://storage.googleapis.com/aom-releases/libaom-3.5.0.tar.gz'
    'libass-0.17.3.tar.gz' = 'https://github.com/libass/libass/releases/download/0.17.3/libass-0.17.3.tar.gz'
    'libbluray-1.4.0.tar.xz' = 'https://download.videolan.org/pub/videolan/libbluray/1.4.0/libbluray-1.4.0.tar.xz'
    'd3d11.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/f701c4c8cc9a881e660904f8c0047908a7b2ed04/tree/mingw-w64-headers/include/d3d11.h?format=raw'
    'd3d11_1.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/aa6ab47929a9cac6897f38e630ce0bb88458e288/tree/mingw-w64-headers/direct-x/include/d3d11_1.h?format=raw'
    'd3d11_2.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/aa6ab47929a9cac6897f38e630ce0bb88458e288/tree/mingw-w64-headers/direct-x/include/d3d11_2.h?format=raw'
    'd3d11_3.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/aa6ab47929a9cac6897f38e630ce0bb88458e288/tree/mingw-w64-headers/direct-x/include/d3d11_3.h?format=raw'
    'd3d11_4.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/6a1e782bb60bb1a93b5ab20fe895394d9c0904c2/tree/mingw-w64-headers/direct-x/include/d3d11_4.h?format=raw'
    'dxgi1_2.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi1_2.h?format=raw'
    'dxgi1_3.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi1_3.h?format=raw'
    'dxgi1_4.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi1_4.h?format=raw'
    'dxgi1_5.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi1_5.h?format=raw'
    'dxgi1_6.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi1_6.h?format=raw'
    'dxgicommon.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgicommon.h?format=raw'
    'dxgitype.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgitype.h?format=raw'
    'dxgiformat.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgiformat.h?format=raw'
    'dxgidebug.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/22333acf22f89b9709c718467e04735157b5d27a/tree/mingw-w64-headers/include/dxgidebug.h?format=raw'
    'dxgi.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/3419b2d4b2b7e8b378696dc79546e3593f00ade6/tree/mingw-w64-headers/include/dxgi.h?format=raw'
    'd3d9caps.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/49e9a673b36a1241747bf3ea95d040f127753f81/tree/mingw-w64-headers/direct-x/include/d3d9caps.h?format=raw'
    'd3d9.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/49e9a673b36a1241747bf3ea95d040f127753f81/tree/mingw-w64-headers/direct-x/include/d3d9.h?format=raw'
    'dxva2api.h' = 'https://sourceforge.net/p/mingw-w64/mingw-w64/ci/45def5d7a10885dfb87af3c7996f8de7197183b5/tree/mingw-w64-headers/include/dxva2api.h?format=raw'
    'dav1d-1.5.1.tar.xz' = 'https://download.videolan.org/pub/videolan/dav1d/1.5.1/dav1d-1.5.1.tar.xz'
    'fluidsynth-2.1.8.tar.gz' = 'https://github.com/FluidSynth/fluidsynth/archive/refs/tags/v2.1.8.tar.gz'
    'game-music-emu-0.6.3.tar.gz' = 'https://github.com/libgme/game-music-emu/archive/refs/tags/0.6.3.tar.gz'
    'libarchive-3.8.0.tar.gz' = 'https://github.com/libarchive/libarchive/releases/download/v3.8.0/libarchive-3.8.0.tar.gz'
    'orc-0.4.33.tar.bz2' = 'https://gitlab.freedesktop.org/gstreamer/orc/-/archive/0.4.33/orc-0.4.33.tar.bz2'
    'mingw-w64-v10.0.0.tar.bz2' = 'https://sourceforge.net/projects/mingw-w64/files/mingw-w64/mingw-w64-release/mingw-w64-v10.0.0.tar.bz2/download'
    'qtsvg-5.6.3.tar.xz' = 'https://download.qt.io/archive/qt/5.6/5.6.3/submodules/qtsvg-opensource-src-5.6.3.tar.xz'
    'pupnp-release-1.14.13.tar.gz' = 'https://github.com/pupnp/pupnp/archive/refs/tags/release-1.14.13.tar.gz'
    'libXau-1.0.6.tar.bz2' = 'https://www.x.org/releases/individual/lib/libXau-1.0.6.tar.bz2'
    'xcb-proto-1.12.tar.bz2' = 'https://www.x.org/archive/individual/xcb/xcb-proto-1.12.tar.bz2'
    'util-macros-1.19.0.tar.bz2' = 'https://www.x.org/archive/individual/util/util-macros-1.19.0.tar.bz2'
    'xproto-7.0.29.tar.bz2' = 'https://www.x.org/releases/individual/proto/xproto-7.0.29.tar.bz2'
}
foreach ($entry in $contrib) {
    if ($sourceUrlOverrides.ContainsKey($entry.file)) {
        $entry.url = $sourceUrlOverrides[$entry.file]
    }
}

if (-not [string]::IsNullOrWhiteSpace($ContribCacheDirectory)) {
    $cache = (Resolve-Path -LiteralPath $ContribCacheDirectory).Path
    foreach ($entry in $contrib) {
        $cachedFile = Join-Path $cache $entry.file
        if ((Test-Path -LiteralPath $cachedFile -PathType Leaf) -and
            (Get-FileHash -Algorithm SHA512 -LiteralPath $cachedFile).Hash -eq $entry.sha512) {
            Copy-Item -LiteralPath $cachedFile -Destination $contribRoot
        }
    }
}

$curlConfig = Join-Path $tempRoot 'curl-config.txt'
$pendingContrib = @($contrib | Where-Object { -not (Test-Path -LiteralPath (Join-Path $contribRoot $_.file) -PathType Leaf) })
$curlLines = foreach ($entry in $pendingContrib) {
    $destination = (Join-Path $contribRoot $entry.file).Replace('\', '/')
    'url = "' + $entry.url + '"'
    'output = "' + $destination + '"'
}
if ($curlLines.Count -ne 0) {
    $curlLines | Set-Content -LiteralPath $curlConfig -Encoding utf8
    & curl.exe --fail --location --retry 3 --silent --show-error --parallel --parallel-max 8 --config $curlConfig
    if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE." }
}

foreach ($entry in $contrib) {
    $download = Join-Path $contribRoot $entry.file
    if (-not (Test-Path -LiteralPath $download -PathType Leaf)) {
        throw "Missing contrib source: $($entry.file)"
    }
    if ((Get-FileHash -Algorithm SHA512 -LiteralPath $download).Hash -ne $entry.sha512) {
        throw "SHA-512 mismatch for contrib source: $($entry.file)"
    }
}

$libVlcSharpName = "LibVLCSharp-$libVlcSharpCommit.tar.gz"
$libVlcSharpUrl = "https://code.videolan.org/videolan/LibVLCSharp/-/archive/$libVlcSharpCommit/$libVlcSharpName"
$libVlcSharpPath = Join-Path $libVlcSharpRoot $libVlcSharpName
& curl.exe --fail --location --retry 3 --silent --show-error --output $libVlcSharpPath $libVlcSharpUrl
if ($LASTEXITCODE -ne 0) { throw "LibVLCSharp source download failed with exit code $LASTEXITCODE." }

$libVlcNugetName = "libvlc-nuget-$libVlcNugetCommit.tar.gz"
$libVlcNugetUrl = "https://code.videolan.org/videolan/libvlc-nuget/-/archive/$libVlcNugetCommit/$libVlcNugetName"
$libVlcNugetPath = Join-Path $libVlcNugetRoot $libVlcNugetName
& curl.exe --fail --location --retry 3 --silent --show-error --output $libVlcNugetPath $libVlcNugetUrl
if ($LASTEXITCODE -ne 0) { throw "libvlc-nuget source download failed with exit code $LASTEXITCODE." }

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    purpose = 'Complete corresponding-source bundle for the x64 LibVLC payload redistributed by this project.'
    provenance = [ordered]@{
        nugetPackage = "VideoLAN.LibVLC.Windows $nugetVersion"
        nugetPackageUrl = "https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/$nugetVersion"
        nugetSha256 = $nugetSha256
        libVlcNugetCommit = $libVlcNugetCommit
        officialWindowsArchive = "vlc-$vlcVersion-win64.7z"
        officialWindowsArchiveUrl = "https://download.videolan.org/pub/videolan/vlc/$vlcVersion/win64/vlc-$vlcVersion-win64.7z"
        officialWindowsArchiveSha256 = $officialWin64Sha256
        nugetX64FilesCompared = 525
        nugetX64FilesMatchingOfficialArchive = 525
    }
    vlc = [ordered]@{
        version = $vlcVersion
        sourceUrl = "https://download.videolan.org/pub/videolan/vlc/$vlcVersion/vlc-$vlcVersion.tar.xz"
        sourceSha256 = $vlcSourceSha256
        archive = "vlc/vlc-$vlcVersion.tar.xz"
    }
    libVlcSharp = [ordered]@{
        commit = $libVlcSharpCommit
        sourceUrl = $libVlcSharpUrl
        archive = "libvlcsharp/$libVlcSharpName"
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $libVlcSharpPath).Hash
    }
    libVlcNuget = [ordered]@{
        commit = $libVlcNugetCommit
        sourceUrl = $libVlcNugetUrl
        archive = "libvlc-nuget/$libVlcNugetName"
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $libVlcNugetPath).Hash
    }
    contribCount = $contrib.Count
    contrib = @($contrib | Sort-Object package,file | ForEach-Object {
        [ordered]@{
            package = $_.package
            file = $_.file
            sourceUrl = $_.url
            sha512 = $_.sha512
        }
    })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundleRoot 'source-manifest.json') -Encoding utf8

@"
# LibVLC corresponding source bundle

This archive accompanies the x64 binary distribution of this project. It contains the official VLC $vlcVersion
source archive, all 126 contrib source inputs pinned by that archive's SHA512SUMS files, the audited
libvlc-nuget packaging source, and the exact LibVLCSharp source commit.

The VideoLAN.LibVLC.Windows $nugetVersion x64 payload was compared file-by-file with the official
vlc-$vlcVersion-win64.7z distribution: 525 of 525 files matched by SHA-256. The application release removes
the separately identified GPL-only plug-ins before distribution.

See source-manifest.json for URLs, commits, and cryptographic hashes.
"@ | Set-Content -LiteralPath (Join-Path $bundleRoot 'README.md') -Encoding utf8

$outputParent = Split-Path -Parent $output
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
[IO.Compression.ZipFile]::CreateFromDirectory($bundleRoot, $output, [IO.Compression.CompressionLevel]::NoCompression, $false)

$archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $output).Hash
$archiveBytes = (Get-Item -LiteralPath $output).Length

$resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
if (-not $resolvedTemp.StartsWith((Join-Path $repoRoot '.tmp') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove unexpected temporary path: $resolvedTemp"
}
Remove-Item -LiteralPath $resolvedTemp -Recurse -Force

Write-Output "Corresponding-source archive: $output"
Write-Output "Bytes: $archiveBytes"
Write-Output "SHA-256: $archiveHash"
Write-Output "Contrib sources: $($contrib.Count)"
