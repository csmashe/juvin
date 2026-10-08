# Juvin brand assets

Primary navy `#0F2D5B` (mark, headers, wordmark). Accent orange `#F5A623` (Apply button, selected item border, links, highlights). Tagline: "Works beside you".

- `svg/juvin-mark-color.svg` — the mark on a square 128 canvas. Source of truth for the shapes.
- `svg/juvin-mark-white.svg`, `svg/juvin-mark-black.svg` — single-colour variants. The second shape is shifted 6 units right so the channel between shapes survives at 16 px.
- `svg/juvin-symbolic.svg` — GNOME symbolic icon, 16×16 viewBox, neutral fill; install as `juvin-symbolic.svg` so the shell recolours it.
- `svg/juvin-horizontal-*.svg`, `svg/juvin-stacked-*.svg` — lockups. On the dark lockups the large shape uses a lighter navy `#1F4B88` so it stays visible on the `#0F2D5B` background. Text is outlined to paths (Inter Bold for the wordmark, Inter SemiBold for the tagline), so they render identically everywhere.
- `png/juvin-mark-{color,white,black}-{16,22,24,32,48,128,256,512}.png` — rendered from the SVGs at 1024 px and downsampled.

The vectors were redrawn on 2026-10-08 from the approved raster board; the originally supplied vector approximations were replaced because the orange shape had a notch and flat base not present in the approved design.
- `source/approved-brand-board.png` — the approved raster board the vectors were redrawn from.
- `source/outline-text.py` — fontTools script used to outline the lockup text (`python outline-text.py <font.ttf> <text> <size> <letter-spacing> <x> <y> <start|middle>`).
