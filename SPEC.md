# Select-and-Read — Specification

**Version:** 1.0 (draft)
**Target:** Windows 10 (1809+) and Windows 11, x64 and ARM64
**Status:** approved for implementation

---

## 1. Overview

A tray-resident Windows utility. Press a hotkey, drag a rectangle over any part of the
screen, and the text inside it is recognised and read aloud immediately. Press ESC or a
stop hotkey to stop reading.

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
- Stop via ESC, a stop hotkey, the tray menu, or re-pressing the capture hotkey
- Recognised text also copied to the clipboard
- Settings: hotkeys, voice, speaking rate, OCR language, and toggles

**Out of scope for v1**

Pause/resume, replay-last-capture, cloud AI backends, translation, file or PDF input,
an installer, code signing, and reading a whole window or the full screen without dragging.

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
| **Speaking** | Audio is playing. |

The app is a single instance, enforced with a named mutex. Launching a second copy
surfaces the settings window of the running instance rather than starting a new one.

### 2.2 Capture flow

Pressing the **capture hotkey** (default `Ctrl+Shift+F9`):

1. **Freeze frame.** The primary monitor is captured to a bitmap *before anything is drawn
   on screen*.
2. **Overlay.** A borderless, topmost window covering the whole primary monitor displays
   that freeze frame with a ~45% black wash over it. The cursor becomes a crosshair.
3. **Drag.** While the left button is held, a selection rectangle is drawn. The region
   inside it is painted from the *undimmed* bitmap, with a 1px accent border and a small
   `W × H` pixel readout offset from the cursor.
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

**Losing focus does *not* cancel.** An earlier revision cancelled on deactivation so that
Alt+Tab would dismiss the overlay. Testing on Windows 11 showed the overlay routinely
activates and is then immediately deactivated by the foreground lock handing focus back to
the previous application — cancelling the selection before the user could draw anything.
Any notification stealing focus would do the same. Losing focus is therefore treated as
normal; what matters is that the overlay stays escapable, which the ESC hook guarantees
regardless of focus.

### 2.4 Pipeline

```
crop → upscale → OCR → clean → clipboard → speak
```

**Performance target:** first spoken word within ~700 ms of mouse-up for a
paragraph-sized selection on typical hardware.

### 2.5 Stopping speech

Any of: ESC (from anywhere), the **stop hotkey** (default `Ctrl+Shift+F10`), the tray
menu's *Stop* item, or pressing the **capture hotkey** again — which stops the current
reading and immediately begins a new selection.

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

### 4.3 Rendering performance

Repainting an entire large screen on every mouse-move event is visibly laggy.
The overlay is double-buffered, and on mouse-move only the union of the previous and
current selection rectangles — inflated by ~4 px to cover the border and the size readout
— is invalidated.

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

### 5.2 Upscaling — the main accuracy lever

Windows OCR is tuned for document-scale text and is noticeably weaker on 10–12 px UI text,
which is exactly what this tool captures most often. Upscaling the crop before recognition
is the cheapest accuracy improvement available and is enabled by default.

**Confirmed by measurement** on a real screen capture of Windows-rendered text: with
upscaling the recognition is exact, character for character; without it the engine drops
leading characters (`he quick`, `cale text`, `ather than`, `heapplication`). See
`tests/fixtures/README.md`.

- Choose an **integer** factor in the range 1×–4× such that the crop's *shorter* side
  reaches approximately 1000 px.
- Clamp so that neither dimension exceeds `OcrEngine.MaxImageDimension`.
- If the crop is below `OcrEngine.MinImageDimension` on either axis, upscale at least
  enough to clear it — the engine rejects images that are too small.
- Resample with `InterpolationMode.HighQualityBicubic`.
- Exposed as a settings toggle so it can be ruled out when diagnosing a bad result.

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
column, emitting the entire left column and then the entire right — it does *not* interleave
line by line, as this spec originally predicted. The result is still wrong for reading
aloud when a passage spans columns, but far less garbled than assumed.

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

### 7.5 Stopping

`MediaPlayer.Pause()`, cancel the synthesis `CancellationTokenSource`, and dispose the
current stream and playback list. Stopping must be immediate and must be safe to call in
any state, including when nothing is playing.

---

## 8. Hotkeys and ESC

### 8.1 Global hotkeys

Registered with `RegisterHotKey` against a message-only `NativeWindow`, including the
`MOD_NOREPEAT` modifier so that holding the key does not re-trigger. `WM_HOTKEY` is
translated into events.

Registration failure raises a tray balloon naming the specific hotkey (see §2.6).

### 8.2 Defaults

| Action | Default |
|---|---|
| Capture | `Ctrl+Shift+F9` |
| Stop | `Ctrl+Shift+F10` |

Function keys are chosen deliberately. `Ctrl+Alt+<letter>` collides with AltGr on
international keyboard layouts, where AltGr generates Ctrl+Alt — such a hotkey would break
text entry for those users. `Ctrl+Shift+<letter>` combinations globally steal shortcuts
that applications commonly use. Both defaults are user-configurable.

### 8.3 ESC while speaking

A `WH_KEYBOARD_LL` low-level keyboard hook, installed **only** for the duration of
playback and removed as soon as speech stops.

The hook **always** calls `CallNextHookEx` — it observes ESC and never swallows it, so
the foreground application still receives its own ESC. A permanently installed hook is
poor citizenship and a plausible source of system-wide input latency; registering ESC as a
global hotkey would consume it outright and break every dialog on the system while the app
happened to be reading.

The hook requires a message pump on its thread, which the UI thread provides.

### 8.4 ESC while selecting

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
| Capture hotkey | `Ctrl+Shift+F9` |
| Stop hotkey | `Ctrl+Shift+F10` |
| OCR language | user profile default |
| Voice id | best available (see §7.2) |
| Speaking rate | 1.0 |
| Upscale before OCR | on |
| Copy to clipboard | on |
| Start with Windows | off |

A small WinForms dialog exposes these: a hotkey-capture control, a voice dropdown
populated from installed voices, a rate slider with a *Test* button, a language dropdown
from `AvailableRecognizerLanguages`, and checkboxes.

*Start with Windows* writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no
elevation required, and per-user, which is correct for a tray utility.

---

## 11. Architecture

WinForms, targeting `net10.0-windows10.0.19041.0`, with `app.manifest` declaring
Per-Monitor-V2 DPI awareness and long-path awareness.

| File | Responsibility |
|---|---|
| `Program.cs` | Mutex, DPI init, bootstrap, debug CLI modes (§12.2) |
| `TrayAppContext.cs` | `ApplicationContext`: tray icon, menu, state machine |
| `Hotkey.cs` | Parses and formats "Ctrl+Shift+F9" ⇄ Win32 modifier flags + virtual key |
| `HotkeyManager.cs` | `RegisterHotKey` on a message-only window; `WM_HOTKEY` → events |
| `ScreenCapture.cs` | Virtual-screen bounds, freeze frame, crop |
| `SelectionOverlay.cs` | The overlay form — largest and most detail-sensitive file |
| `OcrService.cs` | Upscale, `SoftwareBitmap` conversion, recognition |
| `TextCleaner.cs` | Pure functions: joining, de-hyphenation, junk stripping |
| `SpeechService.cs` | Synthesiser + `MediaPlayer`, voice, rate, stop |
| `EscapeWatcher.cs` | Scoped, non-swallowing low-level keyboard hook |
| `Config.cs` | JSON load/save, defaults, Run-key registration |
| `SettingsForm.cs` | Settings dialog |
| `Native.cs` | All P/Invoke declarations |

`TextCleaner` is deliberately pure so that the fiddliest text logic is testable without
any Windows API surface.

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

Self-contained is the right trade: the user downloads one file and runs it, with no .NET
runtime install. **The ARM64 single-file build measures ~147 MB** — considerably larger
than a WinForms-only estimate suggests, because the WinRT projection assemblies come along
too. **Do not enable trimming** — it breaks WinForms' reflection
over designer-generated types.

Unsigned binaries trigger a SmartScreen warning on first run. Documented in the README;
code signing is out of scope for v1.

### 12.2 Debug CLI modes

`Program.cs` supports three non-interactive modes so the risky logic can be exercised
without the full interactive loop:

| Mode | Behaviour |
|---|---|
| `--ocr-file <png>` | Print cleaned OCR text for an image to stdout |
| `--speak "<text>"` | Speak the text and exit |
| `--capture-to <png>` | Run the overlay, write the crop to disk, exit |

These exist primarily for verification (§13) but are also the fastest way to diagnose a
user-reported bad recognition.

---

## 13. Verification

### 13.1 Environment

Development happens on an Apple Silicon Mac. A **Parallels VM running Windows 11 ARM64
(build 26200)** is available and is where all runtime verification is done.

The VM is drivable entirely from the Mac, which is what makes this practical:

- `prlctl exec "<vm>" …` runs commands inside the guest — but as `NT AUTHORITY\SYSTEM`, in
  session 0, which **cannot draw or receive input**. Anything involving the overlay,
  hotkeys or audio must be launched into the logged-on user's session via a scheduled task
  with an `Interactive` principal (see `tests/vm/`).
- `prlctl capture "<vm>" --file x.png` screenshots the guest — **but returns solid black
  while a fullscreen topmost GDI window is up**, so it cannot photograph the overlay.
  Use the app's own `--freeze-to` from a second process for that instead. Several
  apparently alarming "the overlay renders black" results during bring-up were this
  artifact rather than app behaviour.
- Parallels shares only Desktop/Documents/Downloads by default, so builds are staged
  through `~/Downloads` and copied to `C:\` with `robocopy`.

Windows 10 support still rests on API-level compatibility (§12.1) rather than testing:
Apple Silicon can only virtualise Windows 11 ARM64.

### 13.2 Approach

1. **Compile-check on macOS — confirmed working.** With `EnableWindowsTargeting` set, the
   Windows-targeted WinForms + WinRT project builds cleanly from macOS using the .NET 10
   SDK; the reference packs restore from NuGet. The app cannot *run* there, but every
   compile error is caught locally.
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
| Freeze-frame capture of the screen | 3840×2024, correct content |
| Overlay: dim wash, undimmed selection, border, `W × H` readout | Correct — photographed live |
| Drag → crop coordinate fidelity | **Pixel-exact**: a (225,365)–(1260,665) drag produced exactly 1035×300 |
| OCR of real Windows-rendered text | 100% exact, character for character |
| Upscaling on vs off | On: exact. Off: drops leading characters |
| De-hyphenation against real OCR output | Correct |
| Speech synthesis + playback | Works; audio duration scales with `SpeakingRate` exactly |
| Installed voices | Only David / Zira / Mark — no natural voices (§7.2) |
| Debug CLI modes | All four work |

### 13.4 Still unverified

These need hardware or conditions the VM cannot provide, and remain on the README's
manual checklist:

- **Display scaling above 100%.** The VM is a single 3840×2024 display at 100% scaling, so
  while the coordinate discipline in §4 is confirmed pixel-exact, it has only been proven
  at 1:1. A scaled display (125%/150%/200%) is the remaining case where a DPI mistake
  would show up, and is easy to test: set the VM's scaling and re-run the drag.
- Global hotkey registration and conflict reporting (needs an interactive logon session
  driving real keystrokes).
- The tray icon, menu and settings dialog.
- ESC and stop-hotkey cancellation mid-playback.
- Clipboard contents after a capture.
- Protected/DRM content capturing black (§3.1).
- Whether first-word latency meets the ~700 ms target (§7.4).

---

## 14. Open questions

| Question | Status |
|---|---|
| Does cross-compilation from macOS work for this TFM? | **Resolved: yes.** .NET 10 SDK, `EnableWindowsTargeting`, clean build with no warnings. |
| Does upscaling actually improve accuracy? | **Resolved: yes,** decisively, on realistic input (§5.2). It made no clear difference on synthetic fixtures, which is why the real-capture fixture exists. |
| How does the engine order multi-column text? | **Resolved:** by column, not interleaved line by line as originally predicted (§6.1). |
| Does time-to-first-word need chunked synthesis? | **Open.** Not yet measured end to end. §7.4 ships the simple path behind an interface so chunking drops in later without redesign. |
| Is the coordinate handling correct on a scaled display? | **Open, and the biggest remaining risk.** Proven pixel-exact at 100%; untested at 125%/150%/200%. See §13.4. |
