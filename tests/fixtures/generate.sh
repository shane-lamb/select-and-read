#!/usr/bin/env bash
# Regenerates the OCR fixture images (SPEC 13.2).
#
# The fixtures are committed, so you only need this when adding or changing a case.
# Requires ImageMagick 7 (`brew install imagemagick`).
#
# Each <name>.png has a sibling <name>.expected.txt holding the text that
# `SelectAndRead --ocr-file <name>.png` should produce, after TextCleaner has run.
# Compare with:  diff <(SelectAndRead.exe --ocr-file f.png) f.expected.txt

set -euo pipefail
cd "$(dirname "$0")"

# A full font path, since ImageMagick on macOS has no font aliases configured.
# Arial is deliberate: it is also a Windows UI font, so the fixtures resemble the
# text this app will really be pointed at.
FONT="${FONT:-/System/Library/Fonts/Supplemental/Arial.ttf}"

# 1. Small crisp UI text -- the common case, and the one upscaling is aimed at.
magick -background white -fill black -font "$FONT" -pointsize 11 \
  label:"The quick brown fox jumps over the lazy dog.
Recognition should be exact at this size." \
  -bordercolor white -border 8 small-crisp.png

# 2. Same content, softened. Approximates subpixel-antialiased or scaled text.
magick small-crisp.png -blur 0x0.8 small-blurry.png

# 3. Dark mode: light text on a dark background.
magick -background '#1e1e1e' -fill '#e8e8e8' -font "$FONT" -pointsize 12 \
  label:"Dark mode text should read exactly as well
as light mode text does." \
  -bordercolor '#1e1e1e' -border 8 dark-mode.png

# 4. A word broken across lines, to exercise TextCleaner's de-hyphenation.
magick -background white -fill black -font "$FONT" -pointsize 13 \
  label:"Screen readers are particularly inter-
esting when the text has been hyphen-
ated across a line break." \
  -bordercolor white -border 8 hyphenated.png

# 5. Two columns. Expected to interleave -- this fixture pins the documented
#    limitation in SPEC 6.1 rather than asserting correct behaviour.
magick -background white -fill black -font "$FONT" -pointsize 12 \
  label:"Left column line one
Left column line two
Left column line three" left.tmp.png
magick -background white -fill black -font "$FONT" -pointsize 12 \
  label:"Right column line one
Right column line two
Right column line three" right.tmp.png
magick left.tmp.png -size 40x xc:white right.tmp.png +append \
  -bordercolor white -border 8 two-columns.png
rm -f left.tmp.png right.tmp.png

# 6. No text at all: must produce empty output and the spoken "No text found".
magick -size 400x200 gradient:'#4a90d9'-'#c05050' no-text.png

echo "Regenerated $(ls -1 ./*.png | wc -l | tr -d ' ') fixtures in $(pwd)"
