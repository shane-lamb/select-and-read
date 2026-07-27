# Select and Read

Press a hotkey, drag a box over anything on screen, and the text inside it is read aloud.

Fully offline — Windows' own OCR and speech engines do the work, so there is no API key,
no per-use cost, no network, and nothing from your screen ever leaves the machine.

| | |
|---|---|
| **Read a selection** | `Ctrl+Shift+F9` |
| **Stop reading** | `Ctrl+Shift+F10`, or `Esc` |
| **Cancel a selection** | `Esc`, or right-click |

Recognised text is also copied to the clipboard.

Requires Windows 10 1809 or later, or Windows 11. **Single monitor only** — the overlay
covers the primary display, so selections can only be drawn there. See [SPEC.md](SPEC.md)
for the full design and the reasoning behind it.

## Building

```bash
dotnet build
```

Publish a single self-contained `.exe` (no .NET runtime install needed on the target):

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

For ARM64 Windows, use `-r win-arm64` (~147 MB). Do not enable trimming — it breaks
WinForms' reflection over designer types.

The build works from macOS and Linux as well as Windows (`EnableWindowsTargeting` in the
csproj), which is useful for compile-checking, though the app itself only runs on Windows.

Unsigned binaries trigger a SmartScreen warning on first run: *More info → Run anyway*.

## Status

The core pipeline is **verified working on Windows 11 ARM64** (build 26200): capture,
overlay, pixel-exact crop, OCR, and speech. OCR of real Windows-rendered text comes back
character-for-character exact.

Not yet verified: **display scaling above 100%** (the test VM runs at 1:1), and the
hotkeys, tray menu and settings dialog. See the manual checklist below and SPEC §13.4.

## Testing

The text normalisation logic is pure and runs anywhere:

```bash
dotnet test tests/TextCleaner.Tests
```

Everything else needs Windows. Four CLI modes drive the risky parts without the
interactive loop:

```bash
SelectAndRead.exe --ocr-file image.png
```

```bash
SelectAndRead.exe --speak "hello there"
```

```bash
SelectAndRead.exe --capture-to out.png
```

```bash
SelectAndRead.exe --freeze-to screen.png
```

`--freeze-to` saves the raw capture with no overlay, which separates "the capture is wrong"
from "the overlay or crop is wrong". `--capture-to` reports *why* a selection was cancelled.

If you have the Parallels VM set up, `./tests/vm/deploy.sh` builds, deploys and runs the
fixtures in one step. Read [tests/vm/README.md](tests/vm/README.md) first — driving a
Windows VM from the Mac has several non-obvious traps.

`tests/fixtures/` holds OCR images with expected output — see the
[fixtures README](tests/fixtures/README.md) for how to run them all. Every expectation is
now measured output from Windows 11, not a prediction.

### Manual checklist

Still unverified — the VM cannot provide these. The first is by far the most likely to
catch a real defect, because it exercises the coordinate handling the whole design rests
on:

- [ ] **A display scaled to 150% or 200%.** Drag a box and confirm the captured region
      matches the drag exactly. Verified at 100%; untested above it.
- [ ] `Esc` during selection, during recognition, and during speech.
- [ ] Capture hotkey pressed again mid-read — should stop and start a new selection.
- [ ] Launch while another app already owns the hotkey — expect a tray balloon naming it.
- [ ] Clipboard contains the recognised text after a capture.
- [ ] Drag over protected video content — expect the documented black-capture behaviour.
- [ ] Second launch of the app surfaces Settings rather than starting a second instance.
- [ ] Time from mouse-up to the first spoken word (target ~700 ms).

## Known limitations

These are design decisions rather than open bugs; each is argued in the spec.

- **Protected content captures as black.** DRM-protected video is blocked from capture by
  the OS. The app reports "Capture failed" rather than reading nonsense. (SPEC 3.1)
- **Multi-column text is reordered.** The engine reads the whole left column, then the
  whole right — better than interleaving, but still wrong for a passage that spans columns.
  Select one column at a time. (SPEC 6.1)
- **Voice quality depends on what is installed.** The app prefers a "Natural" voice when
  one is present and falls back to the system default. A stock Windows 11 install was
  measured as having only David, Zira and Mark — no natural voices — so the fallback is the
  normal path, not the exception. Add better voices under Settings → Accessibility →
  Narrator. (SPEC 7.2)
- **Single monitor only.** The overlay covers the primary display and nothing else; on a
  multi-monitor machine, secondary displays cannot be selected from. A deliberate scope
  decision. (SPEC 1.2)
- **Windows 10 support is by API compatibility, not testing.** Apple Silicon can only
  virtualise Windows 11 ARM64, so there is no Windows 10 machine to test on; every API used
  exists in 1809. (SPEC 13.1)

## Settings

Right-click the tray icon → *Settings*. Stored at
`%APPDATA%\SelectAndRead\config.json`, which you can also edit directly.

Hotkeys, OCR language, voice, speaking rate, upscale-before-OCR, copy-to-clipboard, and
start-with-Windows.

Two notes on the defaults:

- **Function keys** are the defaults deliberately. `Ctrl+Alt+<letter>` collides with AltGr
  on international layouts, and `Ctrl+Shift+<letter>` steals shortcuts that applications
  commonly use.
- **Upscale before OCR** is on because Windows OCR is tuned for document-scale text and is
  markedly weaker on the 10–12px UI text this tool is usually pointed at. Turn it off when
  diagnosing a bad recognition.
