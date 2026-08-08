# Phase 2 test videos

`scripts/generate-phase2-test-videos.ps1` generates the reproducible MP4 playback-test set under
`test-assets/generated/`. Generated video files are intentionally excluded from Git because the long 4K
files are large. The generated `manifest.json` records media properties, SHA-256 values, and generator
provenance.

The base set contains every combination of:

- 1920x1080 and 3840x2160
- 5 seconds and 60 seconds
- 30 fps and 60 fps

All MP4 files use H.264 8-bit video and AAC-LC stereo audio at 48 kHz. The picture contains a clock,
frame number, grid, fixed color patches, and moving shapes. The left channel contains a 440 Hz tone and
the right channel contains a 660 Hz tone; alternating short pulses support A/V-sync checks.

The generator requires an FFmpeg build that provides `h264_mf` and `aac`. It refuses builds configured
with GPL/nonfree options or libx264/libx265. FFmpeg is a generation-only tool and is not part of the
application distribution. Its download URL and independently calculated archive SHA-256 are required
arguments and are recorded in the manifest rather than inferred from the executable directory.

Run `scripts/transcode-phase2-test-videos-to-wmv.ps1` with Windows PowerShell 5.1 to create matching
VC-1/WMA 9 Professional files through the Windows-provided `Windows.Media.Transcoding` API. No WMV
encoder library is added to the project.

Run `scripts/verify-phase2-test-videos.ps1` to verify all 16 outputs. The verifier checks codecs,
dimensions, duration, exact video-packet count, 48 kHz stereo audio, near-0 dBFS peaks, and SHA-256
hashes, then writes `verification.json`.

## A/V sync set

`scripts/generate-phase2-av-sync-video.ps1` generates a separate 10-second 1080p/60fps MP4 under
`test-assets/generated/av-sync/`. It has 0.5 seconds of silence before the first event. A strong 1 kHz
left pulse and yellow left-half flash start at 0.5, 1.5, 2.5 seconds, while a 1.5 kHz right pulse and
cyan right-half flash start at 1.0, 2.0, 3.0 seconds. Every pulse and flash lasts 80 milliseconds.

Use `scripts/transcode-phase2-test-videos-to-wmv.ps1` with the sync directory as both input and output
to create the matching VC-1/WMA 9 Professional WMV. Generated media remains excluded from Git; the
generator manifest and LibVLC probe report are retained.
