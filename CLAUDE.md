# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows 10/11 tray utility: press a hotkey, drag a rectangle on screen, and the text
inside it is OCR'd and read aloud. A second hotkey pauses, resumes and replays.

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

The release asset is the Inno Setup installer built from `installer/SelectAndRead.iss`, not
the bare exe, which is why that job runs on `windows-latest` — ISCC is a Windows binary and
nothing else about the build needs Windows.

## Development happens on a Mac; the app only runs on Windows

Builds work on macOS via `EnableWindowsTargeting` in the csproj, so compile errors are
caught locally, but nothing can be *run* here. Two consequences shape everything:

- `TextCleaner` and `RealtimeProtocol` are deliberately pure and free of Windows APIs.
  `tests/TextCleaner.Tests` and `tests/RealtimeProtocol.Tests` target plain `net10.0` and
  **compile those files in directly** (linked, not project-referenced), which are the only
  executable tests on the Mac. Keep both classes pure — `RealtimeProtocol` in particular
  must not gain a socket or a `Config` dependency (`Config` touches the registry, so
  depending on it would break the link).
- Everything else is verified in a VMware Fusion VM driven by `vmrun`. `./tests/vm/deploy.sh`
  builds, deploys and runs the OCR fixtures in one step; `./tests/vm/deploy.sh --run` deploys
  and launches the app itself in the guest's interactive session (`--no-build` skips the
  publish). `--exec`, `--shot` and `--stop` cover ad-hoc guest commands, screenshots and
  teardown; prefer them over calling `vmrun` directly, which would put both the VM
  encryption password and the guest password into shell history.

**Read `tests/vm/README.md` before touching the VM.** Driving it has ten non-obvious traps
that each produce a convincing false diagnosis. The four sharpest are in how `vmrun` handles
a guest command line: it does not split the argument string into argv, so only `cmd /c`
works and `powershell.exe` invoked directly always fails; it appends a trailing space that
breaks any script with positional parameters; it provides no stdout channel, so output must
be redirected in the guest and copied back; and embedded double quotes come back as "The
filename or extension is too long". The rest cover session 0 being unable to draw without
`-interactive`, stranded processes contaminating the next capture, an unhidden driver
console being captured instead of your content, `vmrun start` locking you out of the Fusion
UI, `runScriptInGuest` hanging forever, and a blanked display photographing as solid black.

## Architecture

Pipeline: **hotkey → freeze-frame capture → overlay drag → crop → upscale → OCR → clean →
clipboard → speak**. `TrayAppContext` owns the Idle/Selecting/Working/Speaking/Paused state
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
| `--markers ["<text>"] [--voice <n>] [--play]` | Which voices emit word-boundary cues, and what they contain |
| `--read-local <png> [--overlay <x>,<y>]` | Whole local pipeline; logs every word marked, and can draw the real mark |
| `--settings-metrics` | Settings dialog size, scrollability and whether Save is reachable |
| `--highlight-metrics` | The word mark's window rect and whether its region really excludes the word |

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
metrics (`Font.Height * n`), never hardcoded pixel bounds. Absolute `SetBounds` calls look
correct at the default font and become unusable as soon as the user raises the system text
size: labels clipped to one character, rows overlapping, buttons pushed off the client area.
Anything with user-visible text needs to size itself from its content.

**But content-sized is not the same as unbounded, and `SettingsForm` has three layout
invariants that each guard against a dialog you cannot operate.** All are verifiable with
`--settings-metrics`; run it after touching that file.

- **`Form.AutoSize` has no upper bound.** At this row count it sizes the dialog straight past
  the bottom of the screen, taking Save and Cancel with it. The size is instead computed in
  `OnLoad` from `_grid.PreferredSize` — still entirely content- and font-derived — and
  clamped to the working area.
- **A `Fill`-docked child of an `AutoScroll` panel can never scroll.** Docking resizes the
  child to the viewport, so it is never taller than the visible area, no scrollbar appears,
  and the overflow is silently clipped. `_grid` is deliberately left undocked.
- **An `AutoSize` panel never shows a scrollbar.** It reports its full content height as its
  own size, so it never considers itself overfull. The scroll panel must be allowed to be
  smaller than its contents.

**Anything that must always be clickable belongs outside the scrolling region.** Save and
Cancel live in a fixed row of the root layout, not in `_grid` — as grid rows they are the
first thing to fall off the bottom when the dialog clamps.

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

**The ESC hook must always call `CallNextHookEx`** (SPEC §8.2). It observes ESC without
consuming it, so the foreground app still receives its own key, and it is installed only for
the duration of playback or the overlay — never process-wide. "Playback" includes Paused: a
paused reading is the state a user is most likely to sit in, so it is the worst one to have
no escape from.

**`Stop()` tears down; `Pause()` must not** (SPEC §7.5). The two look similar — both start
with `MediaPlayer.Pause()` — but `Stop()` also cancels the token source, drops the
`MediaSource` and settles the pending completion, after which there is nothing to resume.
`Pause()` does none of those three, on either `SpeechService` or `RealtimeAudioPlayer`.
Folding pause into stop behind a flag puts them one boolean apart in a method whose whole
job is tearing down.

**`IsSpeaking` means "a reading is live", not "audio is audible"** — it stays true across a
pause, which is a consequence of the rule above and is load-bearing for
`TrayAppContext.OnPlaybackHotkey`. Speaking is entered *before* OCR or the cloud request, so
the state alone cannot say whether there is anything to pause; the flag can. Pausing a
reading that has not started talking presents as a hang.

**A stopped reading stays replayable; only a new reading discards it** (SPEC §2.5). Neither
engine's `Stop()` may clear its retention — `DiscardReplay()` and the top of `ReadAsync` are
the only places that do. This is what makes the playback hotkey mean the same thing however
the last reading ended.

**The two engines replay by different means, deliberately.** `LocalReadingEngine` keeps the
text and re-synthesises (local, free, quick — retaining audio to save a few hundred
milliseconds would be the wrong trade). `RealtimeReadingEngine` keeps the PCM chunks, because
re-requesting would charge the user twice for the same page and would not return the same
reading. Never "simplify" the cloud path into a second API call. It also means the cloud
engine holds ~48 KB per second of reading, which is why `ApplyEngineSettings` clears
`_lastSpoken` when it disposes the engine that pointer refers to — replaying through a
disposed engine throws from the `CancellationTokenSource` before it reaches any audio.

**Upscale decisions come from measured glyph height, never crop size** (SPEC §5.2).
Enlarging helps small text and *harms* text the engine already reads cleanly — at 4x a
desktop icon label's `net10.0` becomes `netl 0.0`. So recognition runs once at native scale,
takes the median word bounding-box height, and only re-runs enlarged when that is under
25px. Scaling by the crop's shorter side measures the wrong thing entirely.
`tests/fixtures/icon-label.png` and `windows-ui-text.png` bracket the crossover
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

**The installer is per-user for a correctness reason, not a convenience one** (SPEC §12.4).
Everything the app owns is per-user — `asInvoker` manifest, `%APPDATA%` config, DPAPI
`CurrentUser` API key, `HKCU` Run entry — so `PrivilegesRequired=lowest` in
`installer/SelectAndRead.iss` is what keeps the post-install `[Run]` entry a plain launch.
Move the install to Program Files and it elevates, the launched app inherits the admin
token, and it writes the API key and autostart entry into the wrong profile; recovering
needs `runasoriginaluser` on the `[Run]` line. Two other lines in that file are equally
load-bearing: `AppId` is a fixed GUID and must never change, or a new version installs
*beside* the old one instead of over it; and the `[Code]` `taskkill` is not laziness in
place of Restart Manager — RM closes apps by posting `WM_CLOSE` to top-level windows, and a
tray app has none, so it cannot close the app and demands a reboot instead.

**Word boundaries do not come from `SpeechSynthesisStream.Markers`** (SPEC §16.1). That
list is for SSML `<mark>` bookmarks and is empty for ordinary text however the options are
set — the mechanism is a timed metadata track of `SpeechCue`s on the `MediaPlaybackItem`,
enabled by `Options.IncludeWordBoundaryMetadata`. A cue's `StartPositionInInput` is a
character offset into the submitted string, and **using those offsets rather than counting
cues is what makes the mark correct**: `$12.50` produces five cues and `2026` three, all
pointing at the same input characters, so an ordinal count desynchronises permanently at the
first such token. `EndPositionInInput` is inclusive.

**Cleaning destroys character offsets, so `TextCleaner` records them as it goes**
(SPEC §16.2). Dropped lines, collapsed whitespace and de-hyphenation all move text relative
to its source, so `CleanWords` emits a span table alongside the string. Keep the two `Clean`
entry points sharing one implementation — the string overload is the word one with a single
word per line — or the spoken text starts depending on whether anything asked for spans.
`OcrService` therefore cleans recognised *words*, not `OcrLine.Text`; the fixtures are what
hold "line text == its words joined by spaces" to be true, so a fixture diff there is this.

**Three separate things each silently switch the mark off, and every other check still
passes** (SPEC §16.4) — which is why `--read-local` exists. `IncludeWordBoundaryMetadata`
must be set on the synthesiser or no track is published. The overlay's handle must be created
on the UI thread, because `InvokeRequired` is false for a control with no handle yet, so the
first call from the speech timer would build the window on a pump-less thread. And `Visible`
must go through WinForms rather than `SWP_SHOWWINDOW` alone, or `Invalidate` no-ops on a
window WinForms thinks is hidden.

**Tracking is stopped by `Stop` and never by `Pause`** (SPEC §16.4), which is the same
distinction as §7.5. Position is read from `MediaPlaybackSession.Position` rather than from
elapsed time, so a paused reading freezes its own mark with no extra code.

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
