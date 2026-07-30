# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows 10/11 tray utility: press a hotkey, drag a rectangle on screen, and the text
inside it is OCR'd and read aloud.

`SPEC.md` is the design document and carries the reasoning behind every decision below.
When changing behaviour, update it — several sections record findings measured on real
hardware, so it is a log of what is actually true, not just intent.

## Commands

```bash
dotnet build
```

```bash
dotnet test tests/TextCleaner.Tests
```

```bash
dotnet test tests/TextCleaner.Tests --filter "FullyQualifiedName~RejoinsHyphenatedWordAcrossLines"
```

Publish a single self-contained exe (~147 MB; `win-x64` also valid):

```bash
dotnet publish -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true
```

Cut a release by bumping the single integer in `<Version>` in `SelectAndRead.csproj` and
pushing to `main` — `.github/workflows/release.yml` does the rest (SPEC §12.3). That
property is the only place the version is written; nothing else needs updating, and
`app.manifest`'s version is a different thing that stays where it is.

## Development happens on a Mac; the app only runs on Windows

Builds work on macOS via `EnableWindowsTargeting` in the csproj, so compile errors are
caught locally, but nothing can be *run* here. Two consequences shape everything:

- `TextCleaner` and `RealtimeProtocol` are deliberately pure and free of Windows APIs.
  `tests/TextCleaner.Tests` and `tests/RealtimeProtocol.Tests` target plain `net10.0` and
  **compile those files in directly** (linked, not project-referenced), which are the only
  executable tests on the Mac. Keep both classes pure — `RealtimeProtocol` in particular
  must not gain a socket or a `Config` dependency (`Config` touches the registry, so
  depending on it would break the link).
- Everything else is verified in a Parallels VM. `./tests/vm/deploy.sh` builds, deploys and
  runs the OCR fixtures in one step; `./tests/vm/deploy.sh --run` deploys and launches the
  app itself in the guest's interactive session (`--no-build` skips the publish).

**Read `tests/vm/README.md` before touching the VM.** Driving it has four non-obvious traps
that each produce a convincing false diagnosis — `prlctl exec` runs as SYSTEM in session 0
and cannot draw; `prlctl capture` returns solid black over a fullscreen topmost window;
stranded `SelectAndRead.exe` processes make each run capture the previous run's overlay;
and an unhidden driver console covers the desktop and gets captured instead of your content.

## Architecture

Pipeline: **hotkey → freeze-frame capture → overlay drag → crop → upscale → OCR → clean →
clipboard → speak**. `TrayAppContext` owns the Idle/Selecting/Working/Speaking state
machine and sequences all of it; the other classes are single-purpose and independently
testable via the debug CLI modes.

Four debug modes in `Program.cs` exist so the risky parts can be exercised without the
interactive loop, and are the fastest way to diagnose almost anything:

| Mode | Use |
|---|---|
| `--ocr-file <png>` | OCR pipeline against a fixture |
| `--speak "<text>"` | Speech path |
| `--capture-to <png>` | Overlay + crop; reports *why* a selection was cancelled |
| `--freeze-to <png>` | Raw capture, no overlay — separates "capture is wrong" from "overlay is wrong" |
| `--read-file <png>` | Cloud reading engine against a fixture; reports latency and token usage |
| `--settings-metrics` | Settings dialog size, scrollability and whether Save is reachable |

`--ocr-file` also reports the measured glyph height and chosen upscale factor on stderr,
which is the first thing to look at for any "the OCR read it wrong" report. `--read-file` is
its cloud counterpart and reports time-to-first-audio and token usage — the only way to turn
"it was slow" or "it was expensive" into a number, and the way to replace SPEC §14.1's
estimated per-reading cost with a measured one.

## Invariants that will bite you

These are not style preferences. Each was either a real bug found on hardware, or is the
reason a subtle bug does not exist. They are argued in the SPEC sections cited.

**One coordinate space, physical pixels, origin (0,0)** (SPEC §4). The app supports the
primary monitor only, so screen size, capture, overlay position, mouse coordinates and
bitmap indices are all literally the same numbers. Introducing a second space is a defect:
do not use `Screen.Bounds`/`Screen.AllScreens` for geometry (DPI-context dependent), and do
not let WinForms autoscaling touch the overlay (`AutoScaleMode.None`, positioned by
`SetWindowPos`, not the `Bounds` property).

**That rule is scoped to the overlay — ordinary dialogs must do the opposite.**
`SettingsForm` uses `AutoScaleMode.Font`, auto-sizing layout panels and font-relative
metrics (`Font.Height * n`), never hardcoded pixel bounds. It previously used absolute
`SetBounds` calls, which looked correct at the default font and became unusable as soon as
the user raised the system text size: labels clipped to one character, rows overlapping,
buttons pushed off the client area. Anything with user-visible text needs to size itself
from its content.

**But content-sized is not the same as unbounded, and `SettingsForm` now has three layout
invariants that each produced a dialog you could not operate.** All are verifiable with
`--settings-metrics`; run it after touching that file.

- **`Form.AutoSize` has no upper bound.** It sized the dialog straight past the bottom of
  the screen once the cloud rows were added, taking Save and Cancel with it. The size is
  now computed in `OnLoad` from `_grid.PreferredSize` — still entirely content- and
  font-derived — and clamped to the working area.
- **A `Fill`-docked child of an `AutoScroll` panel can never scroll.** Docking resizes the
  child to the viewport, so it is never taller than the visible area, no scrollbar appears,
  and the overflow is silently clipped. `_grid` is deliberately left undocked.
- **An `AutoSize` panel never shows a scrollbar.** It reports its full content height as its
  own size, so it never considers itself overfull. The scroll panel must be allowed to be
  smaller than its contents.

**Anything that must always be clickable belongs outside the scrolling region.** Save and
Cancel used to be the last row of `_grid`; when the dialog clamped, they were the rows that
fell off the bottom. They now live in a fixed row of the root layout.

**Per-Monitor-V2 DPI awareness is not multi-monitor code** (SPEC §4). It stays even though
the app is single-monitor: without a DPI-aware manifest Windows virtualises the process and
the capture arrives as a blurry upscale, which degrades OCR directly. The manifest owns DPI
awareness, which is why `Program.cs` deliberately does **not** call
`ApplicationConfiguration.Initialize()` — that would emit a contradicting
`SetHighDpiMode` call. `WFO0003` is suppressed in the csproj for the same reason.

**Screen capture must use `BitBlt`, not `Graphics.CopyFromScreen`** (SPEC §3).
`CopyPixelOperation` is not a `[Flags]` enum, so `SourceCopy | CaptureBlt` produces an
undefined value that .NET rejects at runtime. The destination bitmap must be
`Format32bppRgb`, **not** `Format32bppArgb`: `BitBlt` leaves alpha at zero, so an ARGB
bitmap comes back fully transparent and the overlay renders solid black.

**Capture the freeze frame before showing the overlay** (SPEC §2.2). This is what stops the
overlay's dimming from contaminating the OCR'd image and stops the scene changing mid-drag.

**The overlay is drawn for users with very poor vision**
(SPEC §2.2). The app's users cannot see a 1px rectangle, so: nothing is dimmed until
the drag starts — a wash over the whole screen hides the thing the user is aiming at, so
pre-drag the overlay is a pixel-identical copy of the desktop plus a screen-spanning
crosshair, and that crosshair is the *only* cue that the hotkey registered; every stroke is
black paired with white, so one of the two contrasts whatever is underneath; and the
selection border is drawn entirely *outside* the rectangle so it never covers the chosen
content. Sizes are hardcoded constants at the top of `SelectionOverlay.cs` — deliberately
not configurable, since there is no user of this app who wants them smaller.

**The overlay must not cancel when it loses focus** (SPEC §2.3). Windows' foreground lock
routinely activates it and then hands focus straight back, which cancelled selections before
the user could draw. It stays escapable via a low-level ESC hook instead — which is also the
only way an unfocused overlay can be dismissed at all.

**The ESC hook must always call `CallNextHookEx`** (SPEC §8.3). It observes ESC without
consuming it, so the foreground app still receives its own key, and it is installed only for
the duration of playback or the overlay — never process-wide.

**Upscale decisions come from measured glyph height, never crop size** (SPEC §5.2).
Enlarging helps small text and *harms* text the engine already reads cleanly — at 4x a
desktop icon label's `net10.0` becomes `netl 0.0`. So recognition runs once at native scale,
takes the median word bounding-box height, and only re-runs enlarged when that is under
25px. An earlier version scaled by the crop's shorter side, which measures the wrong thing
entirely. `tests/fixtures/icon-label.png` and `windows-ui-text.png` bracket the crossover
(27px must not be upscaled; 20px must be) — keep both passing.

**The cloud engine is opt-in, and local is the fallback** (SPEC §14.1). Enabling it changes
three properties the app otherwise guarantees — readings are free, work offline, and never
leave the machine. `LocalReadingEngine` always exists, and a cloud failure falls back to it
**only when nothing has been spoken yet**: once audio has started, restarting the page from
the top is worse than the truncation, which is what `RealtimeException.Spoke` encodes.

**Two Realtime parsing rules are load-bearing** (SPEC §14.2). Unknown event types must be
ignored rather than treated as errors, or a server-side addition breaks the app. And
`response.done` is *not* automatically success — a filtered or incomplete response arrives
that way too, so checking `status` is what stops a truncated reading from being reported as
a clean one.

**The API key never goes in `config.json`** (SPEC §14.5). That file is plain text the user is
expected to edit; the key lives DPAPI-encrypted in `apikey.dat` beside it, and `SettingsForm`
surfaces it via its own `ApiKey` property rather than through `Config`.

**Freeze-frame scoping in `RunPipelineAsync` is load-bearing, not tidiness.** The frame is
tens of megabytes at 4K, so it is scoped to an inner block that ends the moment the crop
exists. A method-scoped `using` there compiles and works, and silently pins a full screenshot
in memory for the duration of every reading.

**Never enable trimming** on publish — it breaks WinForms reflection over designer types.

**`AttachConsole` replaces the process's std handles**, so `Program.cs` checks for an
existing redirect first; without that check `--ocr-file x.png > out.txt` silently writes an
empty file.

## Fixtures

`tests/fixtures/*.expected.txt` record output **measured on Windows 11 ARM64**, not desired
output. Where the engine gets something wrong the expected file records the wrong answer, so
behaviour changes surface as diffs. The synthetic 11pt ImageMagick fixtures sit below the
engine's accuracy floor deliberately and are not a quality bar — see
`tests/fixtures/README.md`.

## Documented limitations, not bugs

Protected/DRM content captures as black (§3.1); multi-column text is read column-by-column
and so reordered (§6.1); single monitor only (§1.2); Windows 10 support rests on API
compatibility rather than testing, since Apple Silicon can only virtualise Windows 11 ARM64
(§13.1). SPEC §13.4 lists what remains unverified — display scaling above 100% is the
largest open risk.
