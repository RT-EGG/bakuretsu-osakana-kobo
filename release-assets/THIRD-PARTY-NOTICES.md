# Third-party notices

This distribution uses the following unmodified third-party components.

| Component | Version | License | Corresponding source |
| --- | --- | --- | --- |
| LibVLCSharp | 3.10.0 | LGPL-2.1 | commit `59d70e96026229e7c232ce5074ecefbf6f8959b6` |
| LibVLCSharp.WPF | 3.10.0 | LGPL-2.1 | same LibVLCSharp commit |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | LGPL-2.1-or-later package declaration | see `CORRESPONDING-SOURCE.md` and the bundled source archive |
| .NET application host/runtime components | build-dependent | MIT and third-party licenses | see `licenses/DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt` |

Copyright remains with VideoLAN, Microsoft, and the respective contributors.
The application does not modify the LGPL libraries. They remain separate DLLs and may be replaced with
interface-compatible builds. Reverse engineering for debugging modifications to those libraries is not prohibited.

The complete LGPL 2.1 text is included at `licenses/LGPL-2.1.txt`.
The exact package versions, file hashes, excluded plug-ins, and source-bundle filename are recorded in
`release-manifest.json`.

The following GPL-only plug-ins found in the upstream non-GPL NuGet package are deliberately excluded:

- `plugins/audio_filter/libdolby_surround_decoder_plugin.dll`
- `plugins/audio_filter/libheadphone_channel_mixer_plugin.dll`
- `plugins/codec/libx26410b_plugin.dll`
- `plugins/lua/liblua_plugin.dll`

