# Project review constraints

Use the repository documents as the source of truth; this file is a compact review index.

## Fixed stack and distribution

- C# / .NET 10 LTS, WPF, `win-x64`.
- `LibVLCSharp` and `LibVLCSharp.WPF` 3.10.0.
- `VideoLAN.LibVLC.Windows` 3.0.23.1 x64 assets, dynamically linked and separately replaceable.
- `NAudio.Core` and `NAudio.Wasapi` 2.3.0 for the custom audio path.
- `System.Text.Json` versioned files under the executable directory's `data` folder.
- Framework-dependent folder ZIP; require .NET 10 Desktop Runtime x64.
- Treat version changes as new review events, not routine upgrades.

Read `docs/decisions/0001-technology-stack.md` for the complete decision and residual risks.

## License and release invariants

- Never reference `VideoLAN.LibVLC.Windows.GPL`.
- Do not publish the unmodified non-GPL VideoLAN NuGet output: remove the four identified GPL-only plugins.
- Require all remaining 319 LibVLC plugins to be readable and free of GPL-only/AGPL declarations.
- Require the 525 x64 native assets to match the audited official VLC 3.0.23 distribution.
- Publish the verified corresponding-source ZIP in the same GitHub Release as the binary ZIP. It includes VLC, 126 pinned contrib sources, LibVLCSharp, and packaging sources.
- Preserve LGPL/MIT texts, copyright notices, source instructions, asset hashes, and separate replaceability.
- Review `docs/phase2/license-review.md`, `docs/phase2/libvlc-source-provenance.md`, and `docs/phase2/publish-distribution-validation.md` for release changes.

## WPF and concurrency invariants

- Declare `PerMonitorV2, PerMonitor`; do not infer DPI correctness from the primary display alone.
- Access Dispatcher-bound UI only on its Dispatcher and tolerate window closure during queued work.
- Unsubscribe window, media, timer, and application events before disposing their publishers or targets.
- Do not block the UI thread on tasks that require the Dispatcher to complete.
- Keep popup coordinates relative to their placement target, clamp to the relevant screen, and account for mouse hit testing and the LibVLC/WPF airspace boundary.
- Treat foreground activation as an OS-mediated best effort; keep IPC receipt and final focus observation as separate evidence.

## LibVLC and audio invariants

- Keep delegates, callback state, buffers, and `GCHandle` instances alive until LibVLC can no longer invoke them.
- Catch and record exceptions inside native callbacks; never allow managed exceptions to cross the native boundary.
- Stop callbacks and detach media/player events before freeing buffers or disposing `MediaPlayer`, `Media`, or `LibVLC`.
- Preserve the verified `S16N`, 48 kHz, stereo contract unless the entire DSP/output path negotiates and validates a new format.
- Bound audio and thumbnail queues; make cancellation and shutdown observable and idempotent.
- Drain limiter state and queued audio in the documented order. Verify underrun, overflow, dropped frames, non-finite values, and undisposed buffers remain zero where required.
- Keep the 500% path capped at -1 dBFS with an immediate stop path and safe OS-volume validation procedure.
- Run thumbnail extraction in a separate `MediaPlayer`, sequentially at low priority, with current hover work prioritized and old-video work cancelled.

Review `docs/phase2/volume-boost-spike.md`, `docs/phase2/thumbnail-extraction-spike.md`, and `docs/phase2/rate-transition-investigation.md` when these paths change.

## Persistence and IPC invariants

- Save to a unique temporary file in the same data directory, flush durable content, then replace/move atomically; delete owned temporary files on failure.
- Preserve the previous valid file on failed writes. Back up corrupt JSON and continue with validated defaults.
- Surface write failure to the user while allowing playback without persistence.
- Use Named Mutex for primary election and a current-user-only Named Pipe for launch requests.
- Bound IPC payload length and timeouts. Preserve Japanese and whitespace paths exactly.
- Isolate each pipe client failure so malformed length, invalid JSON, null fields, or early disconnect cannot terminate the listener loop. Prove a valid client succeeds after a rejected client.
- Complete initial launch-state handling before accepting secondary requests, or otherwise define and test their ordering.
- Accept zero or one file argument; ignore two or more without changing current playback state.

## Supported behavior and evidence boundaries

- Guarantee H.264/AAC MP4 through 4K/60fps and VC-1/WMA 9 Professional WMV through 1080p/60fps on the documented verified environment.
- Treat 4K WMV as outside the performance guarantee. Treat HEVC, VP9, and VFR as best effort.
- Do not promise seamless speed changes; a short transition dropout is accepted.
- Do not call the verified Ryzen 9 5900X / RTX 3070 Ti environment a minimum hardware requirement.
- Keep automated backend evidence distinct from WPF rendering, D3D11VA, WASAPI, listening, foreground, multi-DPI, and clean-environment evidence.

## Relevant verification commands

Choose only those affected by the change:

- `dotnet build <project> -c Release`
- `dotnet test <test-project> -c Release`
- `scripts/test-phase2-single-instance.ps1`
- `scripts/measure-phase2-wpf-performance.ps1`
- license/source/publish validation scripts documented in `docs/phase2/publish-distribution-validation.md`

Do not run real-audio or foreground/manual UI validations without warning the developer first.
