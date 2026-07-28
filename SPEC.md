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

### 5.2 Upscaling — the main accuracy lever, and its limit

Windows OCR is weak on small UI text, so enlarging it before recognition is a large
accuracy win. But it is **only** a win when the text really is small: enlarging text that
the engine already reads cleanly makes it worse, because bicubic interpolation smears fine
detail. The measured failure is a desktop icon label, where 4x turns `net10.0` into
`netl 0.0` — the digit's flag blurs into a lowercase L.

**The decision is therefore made from measured glyph height, never from the crop's
dimensions.** Recognition runs once at native scale; the median height of the reported word
bounding boxes is then used to decide whether a second, enlarged pass is worthwhile.

An earlier version scaled according to the crop's shorter side, which is simply the wrong
measurement — a 205x145 capture got 4x whether its glyphs were 8px or 30px. Crop size says
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

**The dialog must size itself from its content**, using `AutoScaleMode.Font`, auto-sizing
layout panels and font-relative metrics — never hardcoded pixel bounds. An earlier revision
positioned every control with absolute `SetBounds` calls; it rendered correctly at the
default font and broke completely once the user raised the system text size, clipping every
label to a single character and pushing the buttons off the bottom edge. This is the
opposite of the overlay's rule in §4.1, which pins raw physical pixels on purpose because
its coordinates index the freeze frame. Accessibility settings are exactly the conditions a
read-aloud tool should expect to run under.

*Start with Windows* writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no
elevation required, and per-user, which is correct for a tray utility.

---

### 10.1 Sizing — content-derived, desktop-bounded

The dialog sizes itself from its content and the system font, never from hardcoded pixels.
It does **not** use `Form.AutoSize`, which is content-driven but unbounded: once the cloud
rows of §14 were added it sized the form past the bottom of the screen, and Save and Cancel
went with it — unreachable, with nothing to scroll. `OnLoad` computes the size from
`_grid.PreferredSize` and clamps it to the working area; overflow scrolls.

Three rules follow, each of which was a defect first:

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
| `Hotkey.cs` | Parses and formats "Ctrl+Shift+F9" ⇄ Win32 modifier flags + virtual key |
| `HotkeyManager.cs` | `RegisterHotKey` on a message-only window; `WM_HOTKEY` → events |
| `ScreenCapture.cs` | Virtual-screen bounds, freeze frame, crop |
| `SelectionOverlay.cs` | The overlay form — largest and most detail-sensitive file |
| `OcrService.cs` | Upscale, `SoftwareBitmap` conversion, recognition |
| `TextCleaner.cs` | Pure functions: joining, de-hyphenation, junk stripping |
| `SpeechService.cs` | Synthesiser + `MediaPlayer`, voice, rate, stop |
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

Self-contained is the right trade: the user downloads one file and runs it, with no .NET
runtime install. **The ARM64 single-file build measures ~147 MB** — considerably larger
than a WinForms-only estimate suggests, because the WinRT projection assemblies come along
too. **Do not enable trimming** — it breaks WinForms' reflection
over designer-generated types.

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
- The tray icon and menu.
- The settings dialog's *appearance*. Its geometry is now checked by `--settings-metrics`
  (§10.1) at both 1024×768 and 3840×1926, but nothing confirms it looks right, and it has
  not been exercised at a raised system text size — historically where this dialog breaks.
- ESC and stop-hotkey cancellation mid-playback.
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
input *and* emits streaming audio. Anthropic was evaluated and cannot serve this at all:
every Claude model is text+image in, **text out**, with no audio output anywhere on the
Messages API. `gpt-audio` was rejected for the mirror-image reason — audio out, but text
and audio in only, so it cannot take the crop.

Estimated cost is roughly **$0.017 per reading** (~500 image tokens, ~250 text in, ~800
audio out at $0.80/$0.60/$20.00 per MTok). This is an estimate, not a measurement: the
image-token figure in particular is inferred. `--read-file` reports real `usage` and should
be used to replace it.

### 14.2 Wire format

One WebSocket per reading to `wss://api.openai.com/v1/realtime?model=…`, authenticated with
a bearer token. The session is configured for `output_modalities: ["audio"]`, PCM at 24 kHz
mono, and **turn detection disabled** — the app never sends microphone audio, and server VAD
left on makes the model wait for speech that never arrives. Then one
`conversation.item.create` carrying the crop as a `data:image/png;base64,…` URI followed by
the reading prompt, and one `response.create`.

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
| Does cross-compilation from macOS work for this TFM? | **Resolved: yes.** .NET 10 SDK, `EnableWindowsTargeting`, clean build with no warnings. |
| Does upscaling actually improve accuracy? | **Resolved: yes,** decisively, on realistic input (§5.2). It made no clear difference on synthetic fixtures, which is why the real-capture fixture exists. |
| How does the engine order multi-column text? | **Resolved:** by column, not interleaved line by line as originally predicted (§6.1). |
| Does time-to-first-word need chunked synthesis? | **Open.** Not yet measured end to end. §7.4 ships the simple path behind an interface so chunking drops in later without redesign. |
| Is the coordinate handling correct on a scaled display? | **Open, and the biggest remaining risk.** Proven pixel-exact at 100%; untested at 125%/150%/200%. See §13.4. |
| Can a vision-language model replace OCR *and* synthesis in one call? | **Resolved: yes, on OpenAI only.** `gpt-realtime-2.1-mini` takes an image and streams audio back (§14.1). No Anthropic model can — every Claude model is text out only. |
| What does a cloud reading actually cost? | **Open.** Estimated at ~$0.017 (§14.1) from inferred image-token counts. `--read-file` prints real `usage`; replace the estimate with measurement. |
| Does `MediaStreamSource` stream PCM cleanly on ARM64? | **Open.** The highest-risk untested piece of §14.3; underruns or clicks would show up here first. |
