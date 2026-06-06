#!/usr/bin/env bash
# Rewrite SVG viewBox to content bbox + margin for tight icon rendering.
# Usage: ./scripts/tighten-icon-viewboxes.sh <folder> [<folder>...]
#
# Some upstream emoji sets (OpenMoji, Fluent) reserve whitespace in
# viewBox for text-baseline alignment. In G-Helper's fixed-size icon
# slots this makes icons render smaller than peer sets. This tool
# measures each SVG's actual content bbox via rsvg-convert+ImageMagick,
# then tightens the viewBox with a safety margin. Paths are untouched;
# licence attribution preserved.
#
# Requires: rsvg-convert, ImageMagick (convert), python3.
# Idempotent: re-running on already-tightened SVGs is a near no-op.

set -euo pipefail

MARGIN=1.5         # viewBox-units padding on each edge
RENDER_WIDTH=720   # rasterize at 10x viewBox for sub-unit bbox precision
FUZZ=1             # ImageMagick trim tolerance (% of max colour)

for dir in "$@"; do
    [[ -d "$dir" ]] || { echo "skip: $dir"; continue; }
    for svg in "$dir"/*.svg; do
        vb=$(grep -oE 'viewBox="[^"]+"' "$svg" | head -1 | sed 's/viewBox="//;s/"//')
        [[ -z "$vb" ]] && { echo "skip (no viewBox): $svg"; continue; }
        read -r _ _ vw vh <<< "$vb"

        tmp=$(mktemp /tmp/tighten_XXXX.png)
        rsvg-convert -w "$RENDER_WIDTH" "$svg" > "$tmp" 2>/dev/null
        geom=$(convert "$tmp" -fuzz ${FUZZ}% -format "%@" info: 2>/dev/null)
        rm "$tmp"

        new_vb=$(python3 -c "
import re
m = re.match(r'(\d+)x(\d+)\+(\d+)\+(\d+)', '$geom')
if not m: raise SystemExit(2)
pw, ph, px, py = [int(x) for x in m.groups()]
scale = $vw / $RENDER_WIDTH
nx = max(0.0, px*scale - $MARGIN)
ny = max(0.0, py*scale - $MARGIN)
nw = min($vw - nx, pw*scale + 2*$MARGIN)
nh = min($vh - ny, ph*scale + 2*$MARGIN)
# Sanity: if new bbox is too small (< 5% of original), skip - raster failed
if nw < $vw * 0.05 or nh < $vh * 0.05: raise SystemExit(3)
print(f'{nx:.2f} {ny:.2f} {nw:.2f} {nh:.2f}')
") || { echo "skip (bbox compute failed): $svg"; continue; }

        sed -i -E "s|viewBox=\"[^\"]+\"|viewBox=\"$new_vb\"|" "$svg"
        echo "  $(basename "$dir")/$(basename "$svg"): $vb -> $new_vb"
    done
done
