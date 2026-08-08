# Third-party notices for the LibVLC real-time audio probe

| Package | Version | License |
| --- | --- | --- |
| LibVLCSharp | 3.10.0 | GNU LGPL 2.1 family |
| VideoLAN.LibVLC.Windows | 3.0.23.1 | GNU LGPL 2.1 or later |
| NAudio.Core | 2.3.0 | MIT |
| NAudio.Wasapi | 2.3.0 | MIT |

LibVLC is kept in separately replaceable DLLs. GPL-only plug-ins identified by the Phase 2 audit are excluded,
and the remaining plug-ins are checked by `scripts/verify-libvlc-plugin-licenses.ps1`.

Sources and license information:

- https://code.videolan.org/videolan/LibVLCSharp
- https://www.videolan.org/vlc/download-sources.html
- https://code.videolan.org/videolan/libvlc-nuget
- https://github.com/naudio/NAudio/tree/release/2.x

The NAudio MIT license text is included at `licenses/NAudio-MIT.txt`. A distributed release must additionally
include the complete LGPL text and the durable corresponding-source path described in
`docs/phase2/license-review.md`.
