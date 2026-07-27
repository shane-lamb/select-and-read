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

## Development happens on a Mac; the app only runs on Windows

Builds work on macOS via `EnableWindowsTargeting` in the csproj, so compile errors are
caught locally, but nothing can be *run* here. Two consequences shape everything:

- `TextCleaner` is deliberately pure and free of Windows APIs. `tests/TextCleaner.Tests`
  targets plain `net10.0` and **compiles `TextCleaner.cs` in directly** (linked, not
  project-referenced), which is the only executable test on the Mac. Keep that class pure.
- Everything else is verified in a Parallels VM. `./tests/vm/deploy.sh` builds, deploys and
  runs the OCR fixtures in one step.

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

`--ocr-file` also reports the measured glyph height and chosen upscale factor on stderr,
which is the first thing to look at for any "the OCR read it wrong" report.

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
