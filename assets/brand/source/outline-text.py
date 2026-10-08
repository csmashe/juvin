import sys
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

def outline(font_path, text, size, letter_spacing, x, y, anchor="start"):
    f = TTFont(font_path)
    upem = f["head"].unitsPerEm
    cmap = f.getBestCmap()
    glyphs = f.getGlyphSet()
    hmtx = f["hmtx"]
    scale = size / upem
    # advance total for anchoring
    names = [cmap[ord(c)] for c in text]
    total = sum(hmtx[n][0] * scale for n in names) + letter_spacing * (len(names) - 1)
    pen_x = x - total / 2 if anchor == "middle" else x
    paths = []
    for n in names:
        sp = SVGPathPen(glyphs)
        tp = TransformPen(sp, (scale, 0, 0, -scale, pen_x, y))
        glyphs[n].draw(tp)
        d = sp.getCommands()
        if d:
            paths.append(d)
        pen_x += hmtx[n][0] * scale + letter_spacing
    return " ".join(paths)

if __name__ == "__main__":
    font, text, size, ls, x, y, anchor = sys.argv[1:8]
    print(outline(font, text, float(size), float(ls), float(x), float(y), anchor))
