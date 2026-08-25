[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\spikes\LibVlcWpfSpike\LibVlcWpfSpike.csproj'),

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateSet('FrameworkDependent', 'SelfContained')]
    [string]$DistributionMode = 'FrameworkDependent',

    [string]$CorrespondingSourceArchive,

    [switch]$ValidationOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$updaterProject = Join-Path $repoRoot 'src\BakuretsuOsakanaKobo.Updater\BakuretsuOsakanaKobo.Updater.csproj'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$releaseAssets = Join-Path $repoRoot 'release-assets'
$licenseVerifier = Join-Path $repoRoot 'scripts\verify-libvlc-plugin-licenses.ps1'
$packageLock = Join-Path (Split-Path -Parent $project) 'packages.lock.json'

$sourceBundle = $null
if (-not [string]::IsNullOrWhiteSpace($CorrespondingSourceArchive)) {
    $sourceBundle = (Resolve-Path -LiteralPath $CorrespondingSourceArchive).Path
} elseif (-not $ValidationOnly) {
    throw 'A complete corresponding-source archive is mandatory for a public release.'
}

if ($sourceBundle) {
    if ([IO.Path]::GetExtension($sourceBundle) -ne '.zip') {
        throw 'The corresponding-source archive must be the verified ZIP produced by collect-libvlc-corresponding-source.ps1.'
    }

    $sourceZip = [IO.Compression.ZipFile]::OpenRead($sourceBundle)
    try {
        $manifestEntry = $sourceZip.GetEntry('source-manifest.json')
        if (-not $manifestEntry) { throw 'The corresponding-source archive has no source-manifest.json.' }

        $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            $sourceManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json
        } finally {
            $manifestReader.Dispose()
        }

        if ($sourceManifest.schemaVersion -ne 1 -or
            $sourceManifest.provenance.nugetPackage -ne 'VideoLAN.LibVLC.Windows 3.0.23.1' -or
            $sourceManifest.provenance.nugetSha256 -ne '70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F' -or
            $sourceManifest.provenance.libVlcNugetCommit -ne '042f49a49609b2da7aeea0c94e51f809cf2e1575' -or
            $sourceManifest.provenance.officialWindowsArchiveSha256 -ne 'EB4FD8A28291DA73608C733786A09610FEA865FBE94113BCB60B91C1EBB8404A' -or
            $sourceManifest.provenance.nugetX64FilesCompared -ne 525 -or
            $sourceManifest.provenance.nugetX64FilesMatchingOfficialArchive -ne 525 -or
            $sourceManifest.vlc.version -ne '3.0.23' -or
            $sourceManifest.vlc.sourceSha256 -ne 'E891CAE6AA3CCDA69BF94173D5105CBC55C7A7D9B1D21B9B21666E69EFF3E7E0' -or
            $sourceManifest.libVlcSharp.commit -ne '59d70e96026229e7c232ce5074ecefbf6f8959b6' -or
            $sourceManifest.contribCount -ne 126 -or
            @($sourceManifest.contrib).Count -ne 126) {
            throw 'The corresponding-source manifest does not match the audited Phase 2 provenance.'
        }

        function Test-ZipEntryHash {
            param(
                [Parameter(Mandatory = $true)] [string]$EntryName,
                [Parameter(Mandatory = $true)] [string]$Algorithm,
                [Parameter(Mandatory = $true)] [string]$Expected
            )

            $entry = $sourceZip.GetEntry($EntryName)
            if (-not $entry) { throw "Missing corresponding-source entry: $EntryName" }
            $stream = $entry.Open()
            try {
                $actual = if ($Algorithm -eq 'SHA256') {
                    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
                } elseif ($Algorithm -eq 'SHA512') {
                    [Convert]::ToHexString([Security.Cryptography.SHA512]::HashData($stream))
                } else {
                    throw "Unsupported hash algorithm: $Algorithm"
                }
            } finally {
                $stream.Dispose()
            }
            if ($actual -ne $Expected) { throw "Hash mismatch for corresponding-source entry: $EntryName" }
        }

        Test-ZipEntryHash -EntryName $sourceManifest.vlc.archive -Algorithm SHA256 -Expected $sourceManifest.vlc.sourceSha256
        Test-ZipEntryHash -EntryName $sourceManifest.libVlcSharp.archive -Algorithm SHA256 -Expected $sourceManifest.libVlcSharp.sha256
        Test-ZipEntryHash -EntryName $sourceManifest.libVlcNuget.archive -Algorithm SHA256 -Expected $sourceManifest.libVlcNuget.sha256
        foreach ($contribSource in $sourceManifest.contrib) {
            Test-ZipEntryHash -EntryName ("vlc-contrib/" + $contribSource.file) -Algorithm SHA512 -Expected $contribSource.sha512
        }
    } finally {
        $sourceZip.Dispose()
    }
}

if (Select-String -LiteralPath $packageLock -Pattern '"VideoLAN\.LibVLC\.Windows\.GPL"' -Quiet) {
    throw 'The forbidden VideoLAN.LibVLC.Windows.GPL package is present in packages.lock.json.'
}

$lgplText = Join-Path $releaseAssets 'licenses\LGPL-2.1.txt'
$expectedLgplSha256 = '730ACA838484E53C7C4838873DE0CF2F77FC08F27B18F3F20AB775A52687042A'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $lgplText).Hash -ne $expectedLgplSha256) {
    throw 'The bundled LGPL-2.1 text does not match the pinned GNU source.'
}
$nAudioMitText = Join-Path $releaseAssets 'licenses\NAudio-MIT.txt'
$expectedNAudioMitSha256 = '809820EA40A37C228470E47C1534332C2B8993157A8AEF3E74F196E917A67907'
$nAudioMitContent = [IO.File]::ReadAllText($nAudioMitText).Replace("`r`n", "`n")
$nAudioMitSha256 = [Security.Cryptography.SHA256]::Create()
try {
    $actualNAudioMitSha256 = [BitConverter]::ToString(
        $nAudioMitSha256.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($nAudioMitContent))).Replace('-', '')
} finally {
    $nAudioMitSha256.Dispose()
}
if ($actualNAudioMitSha256 -ne $expectedNAudioMitSha256) {
    throw 'The bundled NAudio MIT notice does not match the reviewed text.'
}

if (Test-Path -LiteralPath $outputRoot) {
    if (@(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ne 0) {
        throw "Output directory must be absent or empty: $outputRoot"
    }
} else {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
}

$selfContained = $DistributionMode -eq 'SelfContained'
& dotnet publish $project `
    --configuration Release `
    --no-restore `
    --runtime win-x64 `
    --self-contained $selfContained.ToString().ToLowerInvariant() `
    -p:PublishSingleFile=false `
    --output $outputRoot
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$isProductApplication = [IO.Path]::GetFileName($project) -eq 'BakuretsuOsakanaKobo.App.csproj'
if ($isProductApplication) {
    $updaterOutput = Join-Path $outputRoot 'updater'
    & dotnet publish $updaterProject `
        --configuration Release `
        --no-restore `
        --runtime win-x64 `
        --self-contained $selfContained.ToString().ToLowerInvariant() `
        -p:PublishSingleFile=false `
        --output $updaterOutput
    if ($LASTEXITCODE -ne 0) { throw "Updater publish failed with exit code $LASTEXITCODE." }

    $requiredUpdaterFiles = @(
        'BakuretsuOsakanaKobo.Updater.exe',
        'BakuretsuOsakanaKobo.Updater.dll',
        'BakuretsuOsakanaKobo.Updater.deps.json',
        'BakuretsuOsakanaKobo.Updater.runtimeconfig.json',
        'BakuretsuOsakanaKobo.Update.dll'
    )
    foreach ($requiredUpdaterFile in $requiredUpdaterFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $updaterOutput $requiredUpdaterFile) -PathType Leaf)) {
            throw "Required updater file was not published: $requiredUpdaterFile"
        }
    }
}

$licenses = Join-Path $outputRoot 'licenses'
[IO.Directory]::CreateDirectory($licenses) | Out-Null
$documentation = Join-Path $outputRoot 'docs'
[IO.Directory]::CreateDirectory($documentation) | Out-Null
Copy-Item -LiteralPath (Join-Path $releaseAssets 'THIRD-PARTY-NOTICES.md') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $releaseAssets 'CORRESPONDING-SOURCE.md') -Destination $outputRoot
Copy-Item -LiteralPath $lgplText -Destination $licenses
Copy-Item -LiteralPath $nAudioMitText -Destination $licenses
Copy-Item -LiteralPath (Join-Path $releaseAssets 'licenses\DOTNET-LICENSE.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $releaseAssets 'DOTNET-THIRD-PARTY-NOTICES.txt') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\supported-media-formats.md') -Destination $documentation

$packageInventoryJson = & dotnet list $project package --include-transitive --no-restore --format json
if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed with exit code $LASTEXITCODE." }
$packageInventory = ($packageInventoryJson -join [Environment]::NewLine) | ConvertFrom-Json
foreach ($inventoryProject in $packageInventory.projects) {
    $inventoryProject.path = [IO.Path]::GetRelativePath($repoRoot, $inventoryProject.path).Replace('\', '/')
}
$packageInventory | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'dotnet-packages.json') -Encoding utf8

$sourceBundleName = $null
$sourceBundleSha256 = $null
if ($sourceBundle) {
    $sourceBundleName = [IO.Path]::GetFileName($sourceBundle)
    $sourceBundleSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceBundle).Hash
}

$forbiddenPluginNames = @(
    'libdolby_surround_decoder_plugin.dll',
    'libheadphone_channel_mixer_plugin.dll',
    'libx26410b_plugin.dll',
    'liblua_plugin.dll'
)
$forbiddenFiles = @(
    Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
        Where-Object { $_.Name -in $forbiddenPluginNames }
)
if ($forbiddenFiles.Count -ne 0) {
    throw "Forbidden GPL-only plug-ins were published: $($forbiddenFiles.FullName -join ', ')"
}

$libVlcDirectory = Join-Path $outputRoot 'libvlc\win-x64'
& $licenseVerifier -LibVlcDirectory $libVlcDirectory
if ($LASTEXITCODE -ne 0) { throw 'LibVLC plug-in license verification failed.' }

$allFiles = @(
    Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
        Where-Object { $_.Name -ne 'release-manifest.json' } |
        Sort-Object FullName
)
$inventory = foreach ($file in $allFiles) {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
        bytes = $file.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
    }
}

$runtimeConfig = Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.runtimeconfig.json' | Select-Object -First 1
$includedFrameworks = @()
if ($runtimeConfig) {
    $runtime = Get-Content -Raw -LiteralPath $runtimeConfig.FullName | ConvertFrom-Json
    if ($null -ne $runtime.runtimeOptions.includedFrameworks) {
        $includedFrameworks = @($runtime.runtimeOptions.includedFrameworks)
    }
}

[ordered]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    validationOnly = [bool]$ValidationOnly
    distributionMode = $DistributionMode
    runtimeIdentifier = 'win-x64'
    publishSingleFile = $false
    dotnetSdk = (& dotnet --version)
    includedFrameworks = $includedFrameworks
    packages = [ordered]@{
        LibVLCSharp = '3.10.0'
        'LibVLCSharp.WPF' = '3.10.0'
        'VideoLAN.LibVLC.Windows' = '3.0.23.1'
        'NAudio.Core' = '2.3.0'
        'NAudio.Wasapi' = '2.3.0'
    }
    excludedGplPlugins = $forbiddenPluginNames
    libVlcPluginCount = @(Get-ChildItem -LiteralPath (Join-Path $libVlcDirectory 'plugins') -Recurse -File -Filter '*.dll').Count
    correspondingSourceArchive = $sourceBundleName
    correspondingSourceSha256 = $sourceBundleSha256
    correspondingSourceDistribution = if ($sourceBundle) { 'Separate asset in the same GitHub Release' } else { $null }
    totalFiles = $inventory.Count
    totalBytes = ($allFiles | Measure-Object Length -Sum).Sum
    files = $inventory
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'release-manifest.json') -Encoding utf8

Write-Output "Release validation completed: $outputRoot"
