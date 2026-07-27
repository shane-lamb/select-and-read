# OCR fixtures

Images for exercising the OCR pipeline headlessly on Windows, without the interactive
capture loop (SPEC 13.2).

**Every expected file below records output actually measured on Windows 11 ARM64
(build 26200)** — they are no longer predictions. Where the engine gets something wrong,
the expected file records the wrong answer, so that a change in behaviour shows up as a
diff rather than hiding.

Run one:

```bash
SelectAndRead.exe --ocr-file windows-ui-text.png
```

Compare all of them:

```bash
for f in *.png; do diff <(SelectAndRead.exe --ocr-file "$f") "${f%.png}.expected.txt" \
  && echo "PASS $f" || echo "FAIL $f"; done
```

## The fixtures

| Fixture | Source | Result |
|---|---|---|
| `windows-ui-text.png` | **Real screen capture** of Notepad, via `--capture-to` | Exact, character for character |
| `dark-mode.png` | ImageMagick, 12pt | Exact |
| `hyphenated.png` | ImageMagick, 13pt | Exact — `TextCleaner` de-hyphenation confirmed against real OCR output |
| `two-columns.png` | ImageMagick, 12pt | Columns read in order, not interleaved (see below) |
| `no-text.png` | Gradient, no text | Empty, as required |
| `small-crisp.png` | ImageMagick, 11pt | **Garbled** — see below |
| `small-blurry.png` | `small-crisp` blurred | **Badly garbled** — see below |

`windows-ui-text.png` is the important one: it is a genuine capture of Windows-rendered
text taken through the app's own capture path, so it is representative of what the tool is
actually pointed at. It regenerates via `--capture-to`; the others via `./generate.sh`
(needs ImageMagick 7).

## Two findings worth keeping in mind

**The 11pt ImageMagick fixtures are not representative, and should not be treated as a
quality bar.** `small-crisp.png` produces `Recognitbn shouh be exact at this Ste.`, yet the
real screen capture at a comparable size is perfect. ImageMagick renders small text with
thin, unhinted glyphs that look nothing like Windows' ClearType output. They are kept as a
deliberate stress case marking roughly where the engine's floor sits — not as a defect to
be fixed.

**Upscaling is what makes the real capture exact.** Measured on `windows-ui-text.png`:

| Setting | Output |
|---|---|
| `UpscaleBeforeOcr: true` | 100% exact |
| `UpscaleBeforeOcr: false` | `he quick…`, `cale text`, `ather than`, `heapplication` — dropped leading characters |

That is the evidence behind SPEC 5.2. Note that it only shows up on realistic input: on the
synthetic 11pt fixtures the comparison is muddy, which is exactly why the real capture
fixture was added.

## Multi-column behaviour is better than the spec first assumed

The original spec predicted that a two-column layout would interleave line by line. It does
not — Windows OCR groups by column, emitting the whole left column and then the whole
right. Still wrong for reading aloud in the general case (a sentence spanning columns is
reordered), but far less garbled than predicted. SPEC 6.1 has been corrected to match.
