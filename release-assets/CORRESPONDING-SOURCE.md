# Corresponding source information

The binary distribution must be accompanied in the same GitHub Release by the source archive named in
`release-manifest.json`. This document alone is not a substitute for that archive.

## LibVLCSharp 3.10.0 and LibVLCSharp.WPF 3.10.0

The NuGet packages identify the exact repository commit as:

- Repository: https://code.videolan.org/videolan/LibVLCSharp
- Commit: `59d70e96026229e7c232ce5074ecefbf6f8959b6`
- Archive: https://code.videolan.org/videolan/LibVLCSharp/-/archive/59d70e96026229e7c232ce5074ecefbf6f8959b6/LibVLCSharp-59d70e96026229e7c232ce5074ecefbf6f8959b6.tar.gz

## VideoLAN.LibVLC.Windows 3.0.23.1

- NuGet package: https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1
- Package SHA-256: `70927AFA9AD34B77E7D9A5E6D02CAE099771F6EB3114DA18111A4B76F65B836F`
- Audited packaging commit: `042f49a49609b2da7aeea0c94e51f809cf2e1575`
- Official x64 binary archive: https://download.videolan.org/pub/videolan/vlc/3.0.23/win64/vlc-3.0.23-win64.7z
- Official x64 archive SHA-256: `EB4FD8A28291DA73608C733786A09610FEA865FBE94113BCB60B91C1EBB8404A`
- VLC source archive: https://download.videolan.org/pub/videolan/vlc/3.0.23/vlc-3.0.23.tar.xz
- VLC source SHA-256: `E891CAE6AA3CCDA69BF94173D5105CBC55C7A7D9B1D21B9B21666E69EFF3E7E0`
- NuGet packaging repository: https://code.videolan.org/videolan/libvlc-nuget

All 525 files in the NuGet x64 payload match the official VLC 3.0.23 win64 archive by SHA-256. The source ZIP named
in `release-manifest.json` contains the official VLC source, all 126 contrib inputs pinned by VLC's SHA512SUMS,
the audited packaging source, and the exact LibVLCSharp source. The release script re-hashes every source entry.
Upload that ZIP as a separate asset in the same GitHub Release as the binary distribution.
