# Store images

Images for the Chrome Web Store and Firefox Add-ons listings; `docs/store-listings.md` says where each goes.

- `screenshot-1-menu.png` to `screenshot-4-review.png`, 1280x800: Chrome on macOS with the extension's bundled test page (synthetic mail, no real account), Juvin 0.1.0 and qwen3.5:27b. In order: the action menu beside a selected line, the rewrite applied with the undo note, the right-click list with the thread status and Review, and Review in the desktop panel. Captured October 23, 2026 at the window's size, scaled to 1280x800, and saved as 24-bit PNGs without alpha, which the Chrome Web Store requires (`magick <file> -alpha remove -alpha off PNG24:<file>`).
- `promo-small-440x280.png` and `promo-marquee-1400x560.png`: the dark horizontal lockup on brand navy, rendered from the SVG:

```sh
magick -density 192 -background none assets/brand/svg/juvin-horizontal-dark.svg lockup.png
magick lockup.png -bordercolor '#0F2D5B' -border 1 -fuzz 2% -trim +repage trimmed.png
magick -size 440x280 xc:'#0F2D5B' \( trimmed.png -resize 330x \) -gravity center -composite -depth 8 -strip assets/store/promo-small-440x280.png
magick -size 1400x560 xc:'#0F2D5B' \( trimmed.png -resize 900x \) -gravity center -composite -depth 8 -strip assets/store/promo-marquee-1400x560.png
```

Retake the screenshots when the menu or panel changes visibly.
