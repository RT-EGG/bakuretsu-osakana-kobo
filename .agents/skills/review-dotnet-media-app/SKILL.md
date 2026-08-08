---
name: review-dotnet-media-app
description: Review C#/.NET 10 and WPF media-player changes in bakuretsu-osakana-kobo, including LibVLCSharp/LibVLC and NAudio lifecycle safety, dispatcher and callback concurrency, portable JSON and single-instance IPC, native assets, licenses, publishing, and release gates. Use for code reviews, pre-commit self-reviews, pull-request reviews, dependency updates, media/audio pipeline changes, WPF windowing changes, persistence or IPC changes, and release-script changes in this repository.
---

# Review .NET Media App

## Prepare

1. Read `docs/develop-process.md` and `docs/project-plan.md`.
2. Read the relevant parts of `docs/specifications/specs.md` and `docs/specifications/todo.md`.
3. Read [references/project-review-constraints.md](references/project-review-constraints.md).
4. Identify the requested review boundary. Do not treat unrelated working-tree changes as authored by the change under review.
5. Inspect the diff and enough surrounding code to understand ownership, call order, error paths, and shutdown behavior.

## Review

Trace behavior instead of checking syntax alone.

1. Establish entry points, state ownership, threading context, unmanaged resources, and externally visible effects.
2. Follow success, cancellation, timeout, malformed-input, partial-initialization, and shutdown paths.
3. For every subscription, callback, handle, native object, stream, process, and background task, locate its matching cleanup and verify cleanup order.
4. Check WPF access against Dispatcher affinity, window lifetime, DPI behavior, focus rules, and popup/airspace constraints.
5. Check LibVLC and NAudio code for callback lifetime, buffer ownership, format agreement, bounded queues, drain semantics, use-after-dispose, and exceptions crossing native callbacks.
6. Check persistence and IPC for atomicity, input limits, same-user boundaries, retry/timeout behavior, path preservation, and safe degradation.
7. Check dependency, publish, and native-asset changes against the fixed versions and license/release gates in the reference.
8. Run the narrowest relevant build and automated tests when execution is allowed. Do not replace missing real-window, real-audio, GPU, or clean-environment evidence with a unit-test claim.

## Judge findings

Report only actionable defects introduced or exposed by the reviewed change. Require a concrete failure mode and point to the smallest useful file and line range.

- `P0`: immediate data loss, arbitrary code execution, or release-wide legal/compliance failure.
- `P1`: crash, deadlock, corrupted persistent state, unsafe audio output, native lifetime failure, or forbidden release artifact in a supported path.
- `P2`: incorrect supported behavior, resource leak, race, unbounded work, material performance regression, or incomplete release obligation.
- `P3`: maintainability problem with a credible path to a future defect. Do not report style-only preferences.

Treat missing tests as a finding only when the change adds behavior whose likely regression would not be caught by existing coverage. Distinguish a code defect from unverified manual evidence.

## Report

Lead with findings ordered by severity. For each finding include:

- priority and concise title;
- concrete trigger and impact;
- why current guards or tests do not prevent it;
- exact file and tight line range;
- minimal correction direction, without implementing unless requested.

After findings, list open questions or residual validation gaps only when they affect confidence. If there are no findings, say so directly and name the most important remaining validation gap. Do not summarize the entire change unless requested.

## Finish

If this is a pre-commit self-review, update `docs/project-plan.md` with material decisions, remaining risks, and the next action. Keep ADR-0001 and the Phase 2 evaluation consistent when a dependency, distribution, license, or supported-media decision changes.
