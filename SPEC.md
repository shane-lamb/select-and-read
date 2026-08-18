# Select-and-Read — Specification

**Version:** 1.0 (draft)
**Target:** Windows 10 (1809+) and Windows 11, x64 and ARM64
**Status:** approved for implementation

---

## 1. Overview

A tray-resident Windows utility. Press a hotkey, drag a rectangle over any part of the
screen, and the text inside it is recognised and read aloud immediately. A second hotkey
pauses and resumes the reading, and replays the last one once it has finished; ESC stops it
outright.

The feature set is deliberately small. Essentially all of the engineering value is in
details that are easy to get wrong and very visible when they are: DPI correctness on a
scaled display, latency to the first spoken word, and cancellation that never leaves the
user stuck.

### 1.1 Design principles

1. **Offline and instant.** No network, no API keys, no per-use cost, and screen contents
   never leave the machine. Windows' own OCR and speech engines are used.
2. **Audio-first.** The tool exists to be used without looking. Errors are spoken, not
   shown in dialogs that steal focus.
3. **Never trap the user.** Every state is escapable, and the app never permanently
   swallows a key that belongs to another application.
4. **Simple over clever**, except where reliability demands otherwise. Where this spec
   picks the more complex option (freeze-frame capture, physical-pixel coordinates), it
   says why.

### 1.2 Scope

**In scope for v1**

- Hotkey → drag-select → OCR → speak
- Pause, resume and replay-last-reading, on one hotkey (§2.5)
- Stop via ESC, the tray menu, or re-pressing the capture hotkey
- Recognised text also copied to the clipboard
- Settings: hotkeys, voice, speaking rate, OCR language, and toggles

**Out of scope for v1**

Translation, file or PDF input, an installer, code signing, and reading a whole window or
the full screen without dragging.

**Single monitor only.** The app supports the primary monitor and nothing else: the
overlay covers it, and only it, so selections can only be drawn there. On a multi-monitor
machine the app still runs, but secondary displays are simply not reachable. This is a
deliberate scope decision, not a defect — supporting the virtual desktop means handling a
negative-origin coordinate space and per-monitor scaling differences, which is a
disproportionate amount of the app's total risk for a feature that was not required.

---

## 2. User experience

### 2.1 States

| State | Description |
|---|---|
| **Idle** | Tray icon only. No windows. |
| **Selecting** | Full-screen overlay is up; user is dragging. |
| **Working** | Overlay closed; capture is being OCR'd. Typically well under a second. |
| **Speaking** | A reading is live — audio is playing, or is about to be. |
| **Paused** | A live reading is held mid-word, resumable from exactly where it stopped. |

**Speaking is entered before any audio exists**, because recognition and the cloud request
both happen inside it. Nothing in the pipeline cares about the difference, but the playback
hotkey does — pausing a reading that has not started talking would be indistinguishable from
the app having hung — so it asks the engine whether audio is actually live rather than
reading the state alone (§2.5).

The app is a single instance, enforced with a named mutex. Launching a second copy
surfaces the settings window of the running instance rather than starting a new one.

### 2.2 Capture flow

Pressing the **capture hotkey** (default `Alt+Space`):

1. **Freeze frame.** The primary monitor is captured to a bitmap *before anything is drawn
   on screen*.
2. **Overlay.** A borderless, topmost window covering the whole primary monitor displays
   that freeze frame.
3. **Drag.** While the left button is held, a selection rectangle is drawn.

Every line stroke pairs black with white so that one of the two always contrasts, whatever is
underneath — the same reason Windows' own high-contrast focus indicators are doubled. None
of it can reach the OCR'd image, because the crop is taken from the freeze frame.
4. **Release.** The overlay closes immediately and the pipeline runs.

The freeze frame is the single most important decision in this document. Capturing first
and compositing the overlay on top of a still image means:

- the overlay's dimming can never end up in the OCR'd image;
- the app can never accidentally capture its own selection rectangle;
- the scene cannot change underneath the user mid-drag (animations, video, notifications);
- the crop is a pure array operation with no second screen round-trip, so mouse-up to OCR
  is effectively instantaneous.

The alternative — a transparent overlay plus a second capture after the overlay is hidden —
requires hiding the window, waiting for the desktop to repaint, and re-capturing. It is
both slower and racy.

### 2.3 Cancelling a selection

Any of the following returns silently to **Idle** with no audio and no clipboard change:

- ESC — via a low-level hook, so it works even when the overlay does not hold focus
- Right mouse button
- A selection smaller than 5×5 physical pixels — this makes a stray click harmless

**Losing focus does *not* cancel.** Cancelling on deactivation is tempting — it would make
Alt+Tab dismiss the overlay — but on Windows 11 the overlay routinely activates and is then
immediately deactivated by the foreground lock handing focus straight back to the previous
application, which would cancel the selection before the user could draw anything. Any
notification stealing focus does the same. Losing focus is therefore normal; what matters is
that the overlay stays escapable, which the ESC hook guarantees regardless of focus.

### 2.4 Pipeline

```
crop → upscale → OCR → clean → clipboard → speak
```

**Performance target:** first spoken word within ~700 ms of mouse-up for a
paragraph-sized selection on typical hardware.

### 2.5 Pausing, resuming, replaying and stopping

One **playback hotkey** (default `Alt+V`) means whichever of pause, resume and
replay fits the current state:

| State when pressed | Effect |
|---|---|
| Speaking, audio actually playing | Pause |
| Paused | Resume from the same word |
| Idle, with a reading retained | Replay it from the beginning |
| Speaking, but still recognising or connecting | Nothing |
| Selecting, Working, or Idle with nothing retained | Nothing |

**One hotkey rather than several.** Pause, resume and replay are never simultaneously
meaningful, so splitting them across separate bindings would only add combinations to
remember for a tool whose users cannot read the tray menu to remind themselves. Abandoning a
reading outright is the one playback action this hotkey does not cover; ESC and re-pressing
capture do that.

**Pausing must be told apart from stopping in the playback layer, not just in the state
machine.** They look similar — both begin with `MediaPlayer.Pause()` — but stopping also
cancels the token source, drops the `MediaSource` and settles the pending completion, after
which there is nothing left to resume. Pause touches none of those three.

**Nothing happens when a reading has not started talking yet.** Speaking is entered before
recognition and before the cloud request (§2.1), so the state alone does not say whether
there is audio to hold; the engine's "is a reading live" flag does. Pausing silence would
present as a hang, and ESC already covers aborting a slow cloud call, since the ESC hook is
installed for the whole of Speaking and not just for the audible part of it.

**Stopping** is ESC (from anywhere), the tray menu's *Stop* item, or pressing the **capture
hotkey** again — which stops the current reading and immediately begins a new selection. ESC
stays a hard stop and never becomes a toggle: principle 3 wants one fixed, always-known way
out, and a key that sometimes resumes what you were trying to escape is not that.

**A stopped reading stays replayable.** Retained audio is dropped only when the next reading
starts, so "playback hotkey with nothing playing" means replay-from-the-top however the last
reading ended — whether it finished on its own or the user cut it short. The alternative,
clearing the retention on a deliberate stop, makes the hotkey's meaning depend on history the
user has to remember.

**How a replay is produced differs by engine, and has to.** The local engine keeps the text
and re-synthesises: synthesis is local, free and quick, so holding megabytes of PCM to save a
few hundred milliseconds would be the wrong trade. The cloud engine cannot do that — a second
request costs real money (§14.1) and would not return the same reading — so it keeps the PCM
it already received (§14.3).

### 2.6 Failure feedback

Spoken, never modal:

| Condition | Behaviour |
|---|---|
| OCR returned nothing | Speak "No text found." |
| No OCR engine available | Speak an error naming the missing language pack |
| Hotkey registration failed | Tray balloon naming the specific hotkey that conflicted |
| Screen capture returned a blank image | Speak "Capture failed." |

Hotkey conflicts must never fail silently. A user whose hotkey is quietly stolen by
another application experiences the app as simply broken.

---

## 3. Screen capture

Capture the primary monitor, whose size comes from `GetSystemMetrics` with `SM_CXSCREEN`
and `SM_CYSCREEN`. Its origin is (0,0) by definition, so no offset is carried anywhere: a
`Size` is enough, and screen, capture, overlay and mouse coordinates are all literally the
same numbers.

The virtual-desktop metrics (`SM_*VIRTUALSCREEN`) are deliberately unused — see §1.2.

Use **`BitBlt` directly** with `SRCCOPY | CAPTUREBLT`, so that layered windows are
included. `Graphics.CopyFromScreen` cannot express this: `CopyPixelOperation` is not a
`[Flags]` enum, so `SourceCopy | CaptureBlt` evaluates to an undefined member and the
managed overload rejects it at runtime with `ArgumentException`. That mistake broke every
capture in the app until it was caught on real hardware.

Two further details, both learned the same way:

- The destination bitmap must be **`Format32bppRgb`, not `Format32bppArgb`**. `BitBlt`
  copies only the colour channels and leaves alpha at zero, so an ARGB bitmap comes back
  fully transparent — `DrawImage` then paints nothing and the overlay renders solid black.
- The mouse cursor is not included, which is the desired behaviour.

### 3.1 Known limitation — protected content

DRM-protected surfaces (some browser video players, some streaming apps) capture as solid
black. This is enforced by the OS and cannot be worked around with this capture method.

`Windows.Graphics.Capture` handles these cases but requires a capture session, frame pool,
and Direct3D device, and on older Windows builds draws a yellow border around the captured
region. That complexity is not justified for a tool whose subject is on-screen text. This
limitation is documented for users rather than engineered around.

---

## 4. DPI and display scaling

This is where utilities of this kind usually break: the user drags a box on a 150%-scaled
4K display and gets back an image of somewhere else entirely. Restricting the app to one
monitor removes a large part of that risk but **not** this part — a single display at
125%, 150% or 200% is entirely ordinary, and is exactly the case that goes wrong when a
process is DPI-unaware.

### 4.1 Rules

1. The process declares **Per-Monitor-V2** DPI awareness in `app.manifest`. Without a
   DPI-aware manifest Windows virtualises the process: `GetSystemMetrics` returns logical
   pixels, and the capture comes back as a blurry upscale of a lower-resolution image —
   which would degrade OCR accuracy directly. Per-Monitor-V2 additionally survives the
   user changing display scaling while the app is running, which "System" awareness does
   not.
2. The overlay form sets `AutoScaleMode.None`. WinForms autoscaling would silently
   introduce a second coordinate space.
3. The overlay is positioned with `SetWindowPos` at (0,0) using **raw physical pixel**
   dimensions. Under Per-Monitor-V2, `SetWindowPos` operates in physical pixels.
4. All overlay drawing is 1:1 physical pixels against the freeze-frame bitmap.
5. Mouse coordinates from the overlay's own client area are already physical pixels and
   index directly into the freeze frame.

Because capture, window positioning, painting and hit-testing all live in one physical
pixel space anchored at (0,0), display scaling needs **no special-case code at all** — the
app never converts between coordinate spaces because it only ever has one.

### 4.2 Anti-requirements

Introducing any second coordinate space is a defect. In particular, do not use
`Screen.Bounds` / `Screen.AllScreens` for overlay geometry (these are affected by the
process's DPI awareness context and are a common source of this bug), and do not let any
WinForms scaling apply to the overlay.

---

## 5. OCR

### 5.1 Engine

`Windows.Media.Ocr.OcrEngine`, resolved in this order:

1. `OcrEngine.TryCreateFromLanguage(...)` for the configured language, if one is set
2. `OcrEngine.TryCreateFromUserProfileLanguages()`
3. `OcrEngine.TryCreateFromLanguage(new Language("en-US"))`

If all three return null, the machine has no usable OCR language pack; speak an error
directing the user to Settings → Time & language → Language & region.

`OcrEngine.AvailableRecognizerLanguages` populates the language dropdown in settings.

### 5.2 Upscaling — the main accuracy lever, and its limit

Windows OCR is weak on small UI text, so enlarging it before recognition is a large
accuracy win. But it is **only** a win when the text really is small: enlarging text that
the engine already reads cleanly makes it worse, because bicubic interpolation smears fine
detail. The measured failure is a desktop icon label, where 4x turns `net10.0` into
`netl 0.0` — the digit's flag blurs into a lowercase L.

**The decision is therefore made from measured glyph height, never from the crop's
dimensions.** Recognition runs once at native scale; the median height of the reported word
bounding boxes is then used to decide whether a second, enlarged pass is worthwhile.

Scaling by the crop's shorter side is the obvious shortcut and is simply the wrong
measurement: it gives a 205x145 capture 4x whether its glyphs are 8px or 30px. Crop size says
nothing about text size.

| Median glyph height | Behaviour |
|---|---|
| ≥ 25px | Keep the native-scale result; no second pass |
| < 25px | Re-recognise upscaled towards ~80px, integer factor, capped at 4x |
| 0 (nothing detected) | Enlarge by the maximum and retry — the text may be too small to see at all |

Both constants are calibrated from measurement rather than chosen:

- **The 25px ceiling** is bracketed by two real cases. A Notepad capture with 20px glyphs is
  recognised exactly when upscaled and has four errors at native scale. A desktop icon label
  with 27px glyphs is correct at native scale and wrong when upscaled.
- **The 80px target** is well above the ceiling on purpose. That same 20px capture is exact
  at 4x but has three errors at 2x, so text worth enlarging is worth enlarging properly.

Crops below `MinEngineDimension` on either axis are enlarged before any pass, since the
engine rejects them outright.

The cost is a second recognition pass on small text. Large text now skips the expensive
upscaled pass entirely, so the common case is no slower than before.

### 5.3 Bitmap conversion

`Bitmap` → PNG-encode into a `MemoryStream` → copy into an `InMemoryRandomAccessStream` →
`BitmapDecoder.CreateAsync` → `GetSoftwareBitmapAsync()` → `OcrEngine.RecognizeAsync`.

The PNG round-trip is not the bottleneck at these image sizes and avoids hand-rolling
pixel-format conversion into a `SoftwareBitmap`.

---

## 6. Text cleanup

Raw OCR output read aloud verbatim sounds wrong — hard line breaks become unnatural
pauses and hyphenated words are read as two words. Minimal, high-value normalisation only:

1. Join `OcrResult.Lines` with a single space.
2. **De-hyphenate:** if a line ends with `-` and the next line begins with a lowercase
   letter, drop the hyphen and join with no space.
3. Collapse runs of whitespace to a single space.
4. Drop lines consisting entirely of punctuation or box-drawing characters — table borders
   and separator rules otherwise become a stream of noise.
5. Trim.

### 6.1 Known limitation — multi-column text

Lines are read in the engine's own order. **Measured on Windows 11:** the engine groups by
column, emitting the entire left column and then the entire right, rather than interleaving
line by line. The result is still wrong for reading aloud when a passage spans columns, but
far less garbled than interleaving would be.

Detecting columns reliably is a substantially harder problem than the rest of this
application combined. v1 documents the limitation and advises selecting one column at a
time.

---

## 7. Text-to-speech

### 7.1 Engine

`Windows.Media.SpeechSynthesis.SpeechSynthesizer` → `SynthesizeTextToStreamAsync` →
`Windows.Media.Playback.MediaPlayer`.

The WinRT synthesiser is chosen over `System.Speech` (SAPI) because it can reach the newer
OneCore and natural voices, which sound dramatically better — and voice quality is most of
the perceived quality of a read-aloud tool.

Two `MediaPlayer` settings are required:

- `AudioCategory.Speech`, so the OS ducks other audio appropriately.
- `CommandManager.IsEnabled = false`, otherwise the app appears in the System Media
  Transport Controls and hijacks the keyboard's media keys.

### 7.2 Voice selection

Enumerate `SpeechSynthesizer.AllVoices`. Prefer a voice whose display name contains
"Natural" when one is present; otherwise use `DefaultVoice`. The configured voice is
matched by id, falling back to this probe if that id is no longer installed.

Whether Windows' downloadable natural voices are exposed to non-Narrator applications
varies by Windows build and installation state. This must therefore be a **runtime probe
with a graceful fallback**, never a hard dependency, and the settings dropdown must show
whatever is actually installed rather than a hardcoded list.

**Measured on a stock Windows 11 ARM64 install:** only `Microsoft David`, `Microsoft Zira`
and `Microsoft Mark` are present — no natural voices at all. The fallback is therefore the
normal path, not the exceptional one, which vindicates making it a probe. Users wanting
better voices must add them under Settings → Accessibility → Narrator.

### 7.3 Rate

`SpeechSynthesizer.Options.SpeakingRate`, valid range 0.5–6.0, default 1.0, exposed as a
settings slider with a *Test* button. Speaking rate is the setting users re-tune most.

### 7.4 Latency

Start with the simple path: synthesise the whole text, then play it. Latency scales with
text length, which is fine for the typical paragraph-sized selection.

If measurement on real hardware shows first-word latency above the ~700 ms target for
realistic selections, switch to chunked synthesis: split the text into groups of roughly
two sentences, begin playing group 1 while group 2 synthesises, and append items to a
`MediaPlaybackList` as they become ready.

Synthesis therefore sits behind an interface from the start so that this substitution is
local, but chunking is **not** built until measurement justifies it.

### 7.5 Stopping, and why pausing is not a variant of it

`MediaPlayer.Pause()`, cancel the synthesis `CancellationTokenSource`, and dispose the
current stream and playback list. Stopping must be immediate and must be safe to call in
any state, including when nothing is playing.

**Pausing (§2.5) is a separate operation, not a flag on this one.** It calls
`MediaPlayer.Pause()` and stops there: the token source, the `MediaSource` and the pending
completion are all left intact, which is precisely what makes the utterance resumable and
keeps the awaiting `SpeakAsync` alive. Reusing the stop path with a "don't tear down" flag
would put the two behaviours one boolean apart in a method whose entire job is tearing down.

**"Is speaking" therefore has to mean "a reading is live", not "audio is audible right
now"** — it stays true across a pause. That is the flag §2.5's dispatch relies on to tell a
pausable reading from one still being synthesised, and it only works because pausing settles
nothing.

---

## 8. Hotkeys and ESC

### 8.1 Global hotkeys

Registered with `RegisterHotKey` against a message-only `NativeWindow`, including the
`MOD_NOREPEAT` modifier so that holding the key does not re-trigger. `WM_HOTKEY` is
translated into events.

Registration failure raises a tray balloon naming the specific hotkey (see §2.6).

A registered hotkey takes precedence over the foreground application's own handling of that
chord, so whatever is bound here stops reaching other programs while the app runs. Defaults
are listed with the rest of the settings in §10.

### 8.2 ESC while speaking

A `WH_KEYBOARD_LL` low-level keyboard hook, installed **only** for the duration of
playback and removed as soon as speech stops.

"For the duration of playback" spans **Speaking and Paused alike** (§2.1). A paused reading
is still a reading the user has to be able to abandon, and it is the one state where they
might sit for a while — so dropping the hook on pause would take away the escape at the exact
moment it is most likely to be wanted. It is still never installed while Idle, which is the
property that matters: the hook exists only while the app has something to escape from.

The hook **always** calls `CallNextHookEx` — it observes ESC and never swallows it, so
the foreground application still receives its own ESC. A permanently installed hook is
poor citizenship and a plausible source of system-wide input latency; registering ESC as a
global hotkey would consume it outright and break every dialog on the system while the app
happened to be reading.

The hook requires a message pump on its thread, which the UI thread provides.

### 8.3 ESC while selecting

No hook is needed. The overlay window has focus and handles the key directly.

---

## 9. Clipboard

On every successful recognition, the cleaned text is placed on the clipboard. This makes
the tool useful even when audio is not wanted, for roughly five lines of code.

The clipboard must be set from an STA thread, and `SetText` must retry once after a short
delay on `ExternalException` — the Windows clipboard genuinely fails intermittently when
another process holds it open. A clipboard failure must never abort the speech.

Toggleable in settings; on by default.

---

## 10. Settings

Stored at `%APPDATA%\SelectAndRead\config.json` via `System.Text.Json`, written
atomically (temp file plus replace) so a crash mid-write cannot corrupt the file. A
missing or unparseable file falls back to defaults rather than failing to start.

| Setting | Default |
|---|---|
| Capture hotkey | `Alt+Space` |
| Pause hotkey (pause, resume, replay — §2.5) | `Alt+V` |
| OCR language | user profile default |
| Voice id | best available (see §7.2) |
| Speaking rate | 1.0 |
| Upscale before OCR | on |
| Copy to clipboard | on |
| Start with Windows | off |

A small WinForms dialog exposes these: a hotkey-capture control, a voice dropdown
populated from installed voices, a rate slider with a *Test* button, a language dropdown
from `AvailableRecognizerLanguages`, and checkboxes.

**The dialog must size itself from its content**, using `AutoScaleMode.Font`, auto-sizing
layout panels and font-relative metrics — never hardcoded pixel bounds. Positioning controls
with absolute `SetBounds` calls renders correctly at the default font and breaks completely
once the user raises the system text size: every label clips to a single character and the
buttons are pushed off the bottom edge. This is the opposite of the overlay's rule in §4.1,
which pins raw physical pixels on purpose because its coordinates index the freeze frame.
Accessibility settings are exactly the conditions a read-aloud tool should expect to run
under.

*Start with Windows* writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no
elevation required, and per-user, which is correct for a tray utility.

---

### 10.1 Sizing — content-derived, desktop-bounded

The dialog sizes itself from its content and the system font, never from hardcoded pixels.
It does **not** use `Form.AutoSize`, which is content-driven but unbounded: at this row count
it sizes the form past the bottom of the screen and takes Save and Cancel with it —
unreachable, with nothing to scroll. `OnLoad` computes the size from `_grid.PreferredSize`
and clamps it to the working area; overflow scrolls.

Three rules follow, each of which produces an inoperable dialog when broken:

- Save and Cancel live **outside** the scrolling region. Anything that must always be
  clickable cannot be a row that can scroll away.
- The grid is **not** docked inside the scroll panel. A `Fill`-docked child is resized to the
  viewport, so it is never taller than the visible area, no scrollbar appears, and the
  overflow is clipped instead.
- The scroll panel is **not** `AutoSize`. An auto-sizing panel reports its full content
  height as its own size and so never considers itself overfull.

`--settings-metrics` (§12.2) checks all of this and exits non-zero if Save is off screen or
content overflows with no scrollbar.

---

## 11. Architecture

WinForms, targeting `net10.0-windows10.0.19041.0`, with `app.manifest` declaring
Per-Monitor-V2 DPI awareness and long-path awareness.

| File | Responsibility |
|---|---|
| `Program.cs` | Mutex, DPI init, bootstrap, debug CLI modes (§12.2) |
| `TrayAppContext.cs` | `ApplicationContext`: tray icon, menu, state machine |
| `Hotkey.cs` | Parses and formats "Alt+Space" ⇄ Win32 modifier flags + virtual key |
| `HotkeyManager.cs` | `RegisterHotKey` on a message-only window; `WM_HOTKEY` → events |
| `ScreenCapture.cs` | Virtual-screen bounds, freeze frame, crop |
| `SelectionOverlay.cs` | The overlay form — largest and most detail-sensitive file |
| `OcrService.cs` | Upscale, `SoftwareBitmap` conversion, recognition |
| `TextCleaner.cs` | Pure functions: joining, de-hyphenation, junk stripping |
| `SpeechService.cs` | Synthesiser + `MediaPlayer`, voice, rate, pause/resume/stop |
| `ReadingEngine.cs` | `IReadingEngine` seam + `LocalReadingEngine` (OCR → cleanup → speech) |
| `RealtimeProtocol.cs` | Pure functions: Realtime frame construction and parsing (§14.2) |
| `RealtimeClient` (in `RealtimeReadingEngine.cs`) | WebSocket transport, fallback policy |
| `RealtimeAudioPlayer.cs` | `MediaStreamSource` fed from a channel — plays PCM as it arrives |
| `ApiKeyStore.cs` | DPAPI-encrypted API key, kept out of `config.json` |
| `EscapeWatcher.cs` | Scoped, non-swallowing low-level keyboard hook |
| `Config.cs` | JSON load/save, defaults, Run-key registration |
| `SettingsForm.cs` | Settings dialog |
| `Native.cs` | All P/Invoke declarations |

`TextCleaner` and `RealtimeProtocol` are deliberately pure so that the fiddliest logic —
text normalisation and wire-format handling — is testable without any Windows API surface.

---

## 12. Compatibility, build and distribution

### 12.1 Targets

- TFM `net10.0-windows10.0.19041.0`, which provides the WinRT projections for
  `Windows.Media.Ocr` and `Windows.Media.SpeechSynthesis`.
- `SupportedOSPlatformVersion` of `10.0.17763.0` so the app runs on Windows 10 1809 and
  later. Every API used exists in 1809.

Publish:

```
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true
```

Self-contained is the right trade: no .NET runtime install on the target. **The ARM64
single-file build measures ~147 MB** — considerably larger than a WinForms-only estimate
suggests, because the WinRT projection assemblies come along too. **Do not enable
trimming** — it breaks WinForms' reflection over designer-generated types.

Unsigned binaries trigger a SmartScreen warning on first run. Documented in the README;
code signing is out of scope for v1.

### 12.2 Debug CLI modes

`Program.cs` supports six non-interactive modes so the risky logic can be exercised
without the full interactive loop:

| Mode | Behaviour |
|---|---|
| `--ocr-file <png>` | Print cleaned OCR text for an image to stdout |
| `--read-file <png>` | Read an image via the cloud engine; report latency and token usage |
| `--speak "<text>"` | Speak the text and exit |
| `--capture-to <png>` | Run the overlay, write the crop to disk, exit |
| `--freeze-to <png>` | Save the raw freeze frame with no overlay involved |
| `--settings-metrics` | Report dialog size, scrollability and Save reachability; non-zero if unusable |

These exist primarily for verification (§13) but are also the fastest way to diagnose a
user-reported bad recognition.

### 12.3 Versioning and releases

The version is a **single integer** — `1`, `2`, `3` — held in `<Version>` in
`SelectAndRead.csproj`, which is the only place it is written down. There is no
major/minor split because there is nothing for it to communicate: the app has no API, no
file format anyone else consumes, and no persisted state a new build could break, so a
"breaking change" is not a category that exists here. A number that only goes up is enough
to answer the one question that matters, *which build is this*, and it makes bumping a
release a one-character edit rather than a judgement call.

`app.manifest` also carries a `version="1.0.0.0"`. That is the Win32 side-by-side assembly
identity, is unrelated to the product version, and is deliberately **not** kept in sync —
nothing reads it, and changing it risks the manifest for no benefit.

Releases are cut by `.github/workflows/release.yml`:

1. A push to `main` reads `<Version>` back out of the csproj on a Linux runner.
2. If a tag `v<n>` already exists, the run stops there — green, seconds long, no build.
   Ordinary pushes to `main` therefore cost nothing.
3. Otherwise it runs both test projects on a Windows runner, publishes the §12.1 `win-x64`
   single-file build, wraps it in the §12.4 installer, and attaches that to a new release
   tagged `v<n>` as `SelectAndRead-v<n>-setup.exe`.

So the entire release process is: bump the integer, push to `main`.

The build job runs on `windows-latest` **only because ISCC is a Windows binary**. Publishing
itself remains host-agnostic (§13.2), which is what `tests/vm/deploy.sh` relies on to build
the exe on a Mac. WiX would not have avoided this: its v4+ rewrite moved the toolset onto
.NET, but MSI creation still needs Windows Installer components that do not exist on Linux,
and the containers that claim otherwise run the WiX CLI under Wine.

The version is surfaced in the tray menu as a disabled item above *Exit*, read from
`AssemblyInformationalVersionAttribute` at runtime, so a user can name their build without
finding the exe. This is also why the csproj sets
`IncludeSourceRevisionInInformationalVersion` to `false` — the SDK otherwise appends
`+<commit-sha>`, which would be shown verbatim.

### 12.4 The installer

The published artifact is `installer/SelectAndRead.iss`, an Inno Setup script wrapping the
`win-x64` publish. It installs to `%LOCALAPPDATA%\Programs\SelectAndRead`, registers a
Start menu shortcut, and launches the app when it finishes.

**The install is per-user, and that is a correctness decision rather than a convenience
one.** Everything the app owns is already per-user: the `asInvoker` manifest, `%APPDATA%`
config, the DPAPI `CurrentUser` API key (§14.5) and the `HKCU` Run entry (§10). A Program
Files install would need elevation none of that benefits from, and the elevation is not
merely redundant — an elevated installer hands the launched app an admin token, and the app
then writes its API key and autostart entry into the administrator's profile instead of the
user's. `PrivilegesRequired=lowest` is what lets the post-install launch be a plain `[Run]`
entry rather than needing `runasoriginaluser`, and what puts the uninstaller in the user's
own hive so it can clean up the Run value it is responsible for.

Three further things the script has to get right, each of which fails quietly otherwise:

- **`AppId` is a fixed GUID.** It is what makes an install replace its predecessor instead
  of sitting beside it. Changing it produces two copies and two Add/Remove Programs entries.
- **The running app is killed with `taskkill`, not Restart Manager.** RM closes applications
  by posting `WM_CLOSE` to top-level windows; a tray app has none, so RM cannot close it and
  falls back to demanding a reboot. `CloseApplications=no` turns RM off and the `[Code]`
  section does it directly — which it must, since a running exe cannot be overwritten.
- **`ArchitecturesInstallIn64BitMode` is deliberately unset.** It steers `{autopf}` and the
  registry view, and this install touches neither. `ArchitecturesAllowed=x64compatible`
  still admits ARM64 Windows, which runs the x64 payload under emulation.

Settings are untouched by install, upgrade and uninstall alike, because they live in
`%APPDATA%\SelectAndRead` rather than beside the exe.

Upgrading from a pre-installer copy leaves the user's old downloaded exe where it was —
setup has no way to find it — so the README asks them to delete it. The stale absolute path
such a copy may have written into the Run key is repaired by the app itself:
`Config.RepairStartWithWindowsPath`, called once at startup, repoints an *existing* entry at
the running exe. It never creates one, because an absent entry is the user's decision not to
autostart.

`icon.ico` exists for the installer's sake: it is what Explorer, the Start menu shortcut and
the Add/Remove Programs entry display. The tray icon is still drawn in code
(`TrayAppContext.CreateTrayIcon`) since it is built at one fixed size for one purpose, but
the two are the same mark and should stay in step.

---

## 13. Verification

### 13.1 Environment

Development happens on an Apple Silicon Mac. A **VMware Fusion 26 VM running Windows 11
ARM64** is available and is where all runtime verification is done.

The VM is drivable entirely from the Mac with `vmrun`, which is what makes this practical:

- `vmrun … runProgramInGuest <vmx> -interactive …` runs commands in the logged-on user's
  console session, where the overlay, hotkeys and audio all work. Without `-interactive`
  the command lands in session 0, which **cannot draw** — `--freeze-to` there fails with
  `Screen capture failed.` Both sessions run as the real user with the real profile, so
  DPAPI and the saved API key work either way.
- `vmrun … captureScreen <vmx> x.png` screenshots the guest, **including over the
  fullscreen topmost overlay**. It returns solid black only if the guest display has
  blanked.
- Files go in one at a time with `CopyFileFromHostToGuest`; there are no shared folders and
  no UNC paths. The cost is throughput — about 1.7 MB/s, so the 148 MB exe takes 85 s, which
  is why `tests/vm/deploy.sh` stamps it in the guest and skips unchanged copies.
- The VM is encrypted (Windows 11 needs a vTPM, and VMware requires an encrypted config to
  hold one), so every call also needs `-vp`. Both that and the guest password come from the
  login keychain.

`vmrun`'s guest operations all depend on VMware Tools running in the guest, and its command
line has four sharp edges that produce convincing false diagnoses. Both are written up in
`tests/vm/README.md`, which is required reading before driving the VM.

Windows 10 support still rests on API-level compatibility (§12.1) rather than testing:
Apple Silicon can only virtualise Windows 11 ARM64.

### 13.2 Approach

1. **Building on macOS — confirmed working, up to and including a shippable exe.** With
   `EnableWindowsTargeting` set, the Windows-targeted WinForms + WinRT project builds
   cleanly from macOS using the .NET 10 SDK; the reference packs restore from NuGet. This
   is **not** limited to compile-checking: the full `--self-contained
   -p:PublishSingleFile=true` publish for a Windows RID works too, which is exactly what
   `tests/vm/deploy.sh` does on every deploy — the exe it hands to the VM was built on the
   Mac. **The only thing that requires Windows is running the result** — and, since §12.4,
   packaging it, because ISCC is a Windows binary. That is the sole reason the release job
   is on a Windows runner; the publish inside it would be just as happy on Linux.
2. **`TextCleaner` unit tests run anywhere.** `tests/TextCleaner.Tests` targets plain
   `net10.0` and compiles `TextCleaner.cs` in directly (linked, not referenced), so the
   normalisation rules in §6 are genuinely executed and asserted during development
   rather than merely inspected. This is the only part of the app verifiable without
   Windows, which is precisely why §11 keeps that class pure.
3. **Headless CLI modes** (§12.2) drive the OCR and speech pipelines with no UI.
4. **Fixture images** under `tests/fixtures/`, including one captured from a real Windows
   screen through the app's own capture path. Every expected-output file now records
   output measured on Windows 11 ARM64 rather than predicted.

### 13.3 Verified on Windows 11 ARM64

| Behaviour | Result |
|---|---|
| Freeze-frame capture of the screen | 2048×1536, correct content, at 200% scaling |
| Overlay: dim wash, undimmed selection, border, `W × H` readout | Correct — photographed live, but against the *pre-accessibility* overlay. Of §2.2's additions, the undimmed pre-drag state, the screen-spanning crosshair and its reticle are now confirmed by `vmrun captureScreen`; the double-stroke border and corner brackets are still **unverified** |
| Drag → crop coordinate fidelity | **Pixel-exact, and at 200% scaling**: a (225,365)–(1260,665) drag produced exactly 1035×300 |
| OCR of real Windows-rendered text | 100% exact, character for character |
| Upscaling on vs off | On: exact. Off: drops leading characters |
| De-hyphenation against real OCR output | Correct |
| Speech synthesis + playback | Works; audio duration scales with `SpeakingRate` exactly |
| Installed voices | Only David / Zira / Mark — no natural voices (§7.2) |
| Debug CLI modes | All four work |

### 13.4 Still unverified

These need hardware or conditions the VM cannot provide, and remain on the README's
manual checklist:

- Global hotkey registration and conflict reporting. This still needs a human at the guest:
  `vmrun typeKeystrokesInGuest` fails with `Insufficient permissions in the host operating
  system`, so keystrokes cannot be injected from the Mac the way mouse input can.
- **Whether the settings dialog's hotkey box can capture the default chords.** `Alt+Space` and
  `Alt+<letter>` arrive as `WM_SYSKEYDOWN` and would otherwise open the window menu or match
  a control mnemonic; `HotkeyBox` claims them via `IsInputKey` and `Handled`, which should
  pre-empt both, but the interaction has not been exercised. If either escapes, the default
  is still registrable — the user just could not rebind *to* it from the dialog.
- The tray icon and menu.
- The settings dialog's *appearance*. Its geometry is now checked by `--settings-metrics`
  (§10.1) at both 1024×768 (session 0's desktop) and the 2048×1440 working area of the real
  session, and Save is reachable in both, but nothing confirms it looks right, and it has
  not been exercised at a raised system text size — historically where this dialog breaks.
- ESC cancellation mid-playback.
- **Pause, resume and replay (§2.5), on either engine.** Nothing here has run: whether
  `MediaPlayer.Pause()` resumes a `MediaStreamSource` mid-stream without a gap or a click is
  the largest unknown, and holding a cloud reading paused past the 60 s receive timeout is
  the second.
- Clipboard contents after a capture.
- Protected/DRM content capturing black (§3.1).
- Whether first-word latency meets the ~700 ms target (§7.4).
- **The entire cloud reading engine (§14).** Nothing below the wire format has run: the
  `RealtimeProtocol` unit tests cover frame construction and parsing, but the WebSocket
  transport, `MediaStreamSource` playback, the DPAPI key store and the fallback policy are
  all unexercised. `--read-file` is the way in.
- Whether `MediaStreamSource` streams PCM cleanly on ARM64 without underruns (§14.3).

---

## 14. Cloud reading engine

### 14.1 Why, and why it is opt-in

Windows OCR is the accuracy ceiling of the local pipeline: it is weak on small text (§5.2),
reorders multi-column content (§6.1), and has no way to tell body text from window chrome.
A vision-language model that accepts the crop and returns speech directly removes both that
ceiling and the sequential recognise-then-synthesise latency of §7.4, because audio starts
arriving before the model has finished reading.

It is **off by default and gated on an API key**. Enabling it changes three properties the
app otherwise guarantees — readings are free, work offline, and never leave the machine —
and none of those should change without the user asking. The local pipeline remains the
default and the fallback.

**Chosen model: `gpt-realtime-2.1-mini`.** It is the cheapest model that accepts image
input *and* emits streaming audio. Anthropic cannot serve this at all: every Claude model is
text+image in, **text out**, with no audio output anywhere on the Messages API. `gpt-audio`
fails for the mirror-image reason — audio out, but text and audio in only, so it cannot take
the crop.

Estimated cost is roughly **$0.017 per reading** (~500 image tokens, ~250 text in, ~800
audio out at $0.80/$0.60/$20.00 per MTok). This is an estimate, not a measurement: the
image-token figure in particular is inferred. `--read-file` reports real `usage` and should
be used to replace it.

### 14.2 Wire format

One WebSocket per reading to `wss://api.openai.com/v1/realtime?model=…`, authenticated with
a bearer token. The session is configured for `output_modalities: ["audio"]`, PCM at 24 kHz
mono, and **turn detection disabled** — the app never sends microphone audio, and server VAD
left on makes the model wait for speech that never arrives. Then one
`conversation.item.create` carrying the crop as a `data:image/png;base64,…` URI, and one
`response.create`.

Frame construction and parsing live in `RealtimeProtocol`, which is pure and free of both
sockets and Windows APIs — the same discipline as `TextCleaner`, for the same reason, and it
makes the wire format the only part of this feature testable off the VM.

Two parsing rules are load-bearing:

- **Unknown event types are ignored, not errors.** The Realtime API emits many events this
  client has no use for and adds new ones over time; treating them as failures would break
  the app on a server-side addition.
- **`response.done` is not automatically success.** A response cut short by a content filter
  or an incomplete turn arrives as `response.done` with a non-completed `status`. Treating
  every `response.done` as completion would truncate the reading silently and report nothing.

### 14.3 Streaming playback

`SpeechService` cannot be reused: it synthesises a whole utterance to a stream and hands the
finished stream to `MediaPlayer`, which is precisely the full-utterance latency this engine
exists to remove. Instead a `MediaStreamSource` with `AudioEncodingProperties.CreatePcm` is
pulled on demand from an unbounded channel, so playback starts on the first chunk.

`MediaStreamSource` is chosen over `AudioGraph` because it needs no `unsafe` code and no
`IMemoryBufferByteAccess` interop, and because it preserves the `MediaPlayer` configuration
§7 already settled: `AudioCategory.Speech` for correct OS ducking, and `CommandManager`
disabled so the app does not hijack the media keys.

`byte[]` reaches WinRT as an `IBuffer` via `DataWriter`, **not** `AsBuffer()` — the same
choice, and the same reason, as the conversion in §5.3.

Leaving `request.Sample` null is how `MediaStreamSource` is told the stream has ended; there
is no separate "finished" call. That null path is therefore the normal end of every reading,
not an error path.

**Pausing a stream source works, with one catch.** `MediaPlayer.Pause()` simply stops the
sample requests; the socket carries on filling the channel, and the queued chunks are there
waiting on resume. The catch is that playback auto-starts on the *first* chunk, so that call
has to be suppressed while paused — otherwise a chunk landing during a pause restarts the
audio on its own.

**Every chunk is retained so the reading can be replayed** (§2.5). Consumed samples are
dropped by the source and `CanSeek` is false, so replay means feeding a fresh player the
whole sequence again — which also means pause and resume behave identically on a replay and
on a live reading. Re-requesting the audio instead would charge the user a second time for
something they have already paid for, and would not return the same reading.
At 24 kHz mono 16-bit the retention costs 48 KB per second — around 14 MB for a five-minute
reading, an order of magnitude below the freeze frame the pipeline already takes care to
release — and it is dropped when the next reading starts.

### 14.4 Failure and fallback

A cloud failure falls back to the local engine, **but only if nothing was spoken yet**.
Once audio has started, restarting the page from the top would be more disruptive than the
truncation, so the failure is reported instead. `RealtimeException.Spoke` carries that
distinction.

A superseded capture neither reports nor falls back: the balloon would be stale, and the
local reading would talk over the selection the user has since started.

### 14.5 Key storage

The API key lives in `%APPDATA%\SelectAndRead\apikey.dat`, DPAPI-encrypted at
`CurrentUser` scope — **not** in `config.json`, which is plain text the user is expected to
open and edit, and which is the wrong place for a token that grants spend on their account.

This is obfuscation against casual disclosure, not a secret store: any code running as the
user can call `Unprotect` exactly as the app does. That is the same guarantee the browsers'
saved-password stores offer, and the right level for a tray utility.

---

## 15. Open questions

| Question | Status |
|---|---|
| Does cross-compilation from macOS work for this TFM? | **Resolved: yes,** and further than a compile — .NET 10 SDK with `EnableWindowsTargeting` gives a clean build *and* a working self-contained single-file publish for a Windows RID. `tests/vm/deploy.sh` relies on it, and so does the release workflow (§12.3). |
| Does upscaling actually improve accuracy? | **Resolved: yes,** decisively, on realistic input (§5.2). It made no clear difference on synthetic fixtures, which is why the real-capture fixture exists. |
| How does the engine order multi-column text? | **Resolved:** by column, not interleaved line by line (§6.1). |
| Does time-to-first-word need chunked synthesis? | **Open.** Not yet measured end to end. §7.4 ships the simple path behind an interface so chunking drops in later without redesign. |
| Is the coordinate handling correct on a scaled display? | **Open, and the biggest remaining risk.** Proven pixel-exact at 100%; untested at 125%/150%/200%. See §13.4. |
| Can a vision-language model replace OCR *and* synthesis in one call? | **Resolved: yes, on OpenAI only.** `gpt-realtime-2.1-mini` takes an image and streams audio back (§14.1). No Anthropic model can — every Claude model is text out only. |
| What does a cloud reading actually cost? | **Open.** Estimated at ~$0.017 (§14.1) from inferred image-token counts. `--read-file` prints real `usage`; replace the estimate with measurement. |
| Does `MediaStreamSource` stream PCM cleanly on ARM64? | **Open.** The highest-risk untested piece of §14.3; underruns or clicks would show up here first. |
| Does resuming a paused `MediaStreamSource` pick up cleanly mid-stream? | **Open.** Pausing a live source is the one part of §2.5 with no local equivalent to fall back on. A gap, a click or a dropped chunk on resume shows up here; the local engine's seekable stream is not at risk in the same way. |
| Does a cloud reading survive being paused past the 60 s receive timeout? | **Open.** It should: the timeout caps silence *from the server*, and the socket finishes independently of playback, so a pause should not reach it. Untested (§13.4). |
