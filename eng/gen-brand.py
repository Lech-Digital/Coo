#!/usr/bin/env python3
"""Draws every Mochi brand asset from one set of shapes (owner, 2026-11-06: matcha, soft flat).

Writes the launcher icon, the splash, the in-app Mochi (awake and asleep), the wordmark geometry the app draws
(WordmarkGeometry.g.cs) and the large wordmark SVGs for the store and the video. Edit this file, not the outputs.

Needs fontTools (pip install fonttools) for the wordmark, which is Fredoka SemiBold turned into outlines, so the
app does not ship the font: store-assets/_src/fonts/Fredoka-SemiBold.ttf (SIL OFL, see Fredoka-OFL.txt).

    python3 eng/gen-brand.py
"""
from pathlib import Path

from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path
from fontTools.ttLib import TTFont

def fmt(v: float) -> str:
    return f'{v:.2f}'.rstrip('0').rstrip('.') if v != int(v) else str(int(v))


ROOT = Path(__file__).resolve().parent.parent
MOBILE = ROOT / 'src/Mobile/Coo.Mobile'

# The colours (docs/design/design-system.md §2): matcha glow, the bunny's cream, its outline and shadow.
GLOW = ('#B9D492', '#8DB462', '#5E8A3E')
CREAM, OUTLINE, INNER_EAR, FEATURE, CHEEK = '#FBF7EF', '#E3D8C2', '#F5C2C0', '#4A3326', '#F0A08F'
SHADOW = '#2F4A1F'
BRAND = {'day': '#93B86A', 'night': '#A3C47F'}
STEM = {'day': '#4A7036', 'night': '#A3C47F'}
INK = {'day': '#262A22', 'night': '#D9E3C8'}
DOT_EYES = '#253A19'

# The bunny, on a 108 grid (the adaptive icon's canvas): the dome with the flat bottom, and its ears.
BODY = 'M18 72 C18 51 34 40 54 40 C74 40 90 51 90 72 C90 81 83 84.5 72 84.5 H36 C25 84.5 18 81 18 72 Z'
EAR_L_UP = ('M38 46 C33 30 35 19 41 19 C47 19 48 31 46 43 Z', 'M40.5 42 C37.5 31 38.5 24 41.5 24 C44.5 24 45 32 43.6 41 Z')
EAR_R_UP = ('M70 46 C75 30 73 19 67 19 C61 19 60 31 62 43 Z', 'M67.5 42 C70.5 31 69.5 24 66.5 24 C63.5 24 63 32 64.4 41 Z')
EAR_L_FLOP = ('M46 44 C45 34 38 27 28 27 C20 27 18 33 22 37 C26 41 34 42 40 47 Z', 'M42 42 C40 35 35 31 28 31 C23 31 22 34 25 36 C29 39 35 40 39.5 43.5 Z')
EAR_R_FLOP = ('M62 44 C63 34 70 27 80 27 C88 27 90 33 86 37 C82 41 74 42 68 47 Z', 'M66 42 C68 35 73 31 80 31 C85 31 86 34 83 36 C79 39 73 40 68.5 43.5 Z')
HIGHLIGHT = 'M27 60 C29 53 34 48 40 45.5'
CHEEKS = f'<ellipse cx="37.5" cy="70" rx="4.6" ry="2.9" fill="{CHEEK}" opacity=".85"/><ellipse cx="70.5" cy="70" rx="4.6" ry="2.9" fill="{CHEEK}" opacity=".85"/>'


def stroke(d: str, width: float, colour: str = FEATURE) -> str:
    return f'<path d="{d}" stroke="{colour}" stroke-width="{width}" fill="none" stroke-linecap="round"/>'


FACES = {
    # The logo's face: happy closed eyes and a smile.
    'happy': stroke('M40 64 q4 -4 8 0', 2.4) + stroke('M60 64 q4 -4 8 0', 2.4) + CHEEKS + stroke('M49.8 69 q4.2 3.4 8.4 0', 2.1),
    'awake': (f'<circle cx="44" cy="63" r="2.8" fill="{FEATURE}"/><circle cx="64" cy="63" r="2.8" fill="{FEATURE}"/>'
              '<circle cx="44.9" cy="62" r=".85" fill="#FAFAF5"/><circle cx="64.9" cy="62" r=".85" fill="#FAFAF5"/>'
              + CHEEKS + stroke('M50.2 69 q3.8 3.4 7.6 0', 2.1)),
    'asleep': stroke('M40 63 q4 3.4 8 0', 2.4) + stroke('M60 63 q4 3.4 8 0', 2.4) + CHEEKS + stroke('M50.75 70 q3.25 3.4 6.5 0', 2.1),
}


def bunny(ears, face: str) -> str:
    """The soft-flat bunny: cream with a faint warm outline, one highlight, a crisp shadow; no gradients, no blur."""
    line = f'stroke="{OUTLINE}" stroke-width="1.2" stroke-linejoin="round"'
    parts = [f'<ellipse cx="54" cy="85.6" rx="31" ry="3.4" fill="{SHADOW}" opacity=".22"/>']
    for outer, inner in ears:
        parts.append(f'<path d="{outer}" fill="{CREAM}" {line}/><path d="{inner}" fill="{INNER_EAR}"/>')
    parts.append(f'<path d="{BODY}" fill="{CREAM}" {line}/>')
    parts.append(f'<path d="{HIGHLIGHT}" stroke="#FFFDF8" stroke-width="3" fill="none" stroke-linecap="round" opacity=".95"/>')
    parts.append(FACES[face])
    return ''.join(parts)


def glow(gid: str, **attrs) -> str:
    extra = ' '.join(f'{k}="{v}"' for k, v in attrs.items())
    return (f'<radialGradient id="{gid}" {extra}><stop offset="0" stop-color="{GLOW[0]}"/>'
            f'<stop offset=".5" stop-color="{GLOW[1]}"/><stop offset="1" stop-color="{GLOW[2]}"/></radialGradient>')


def disc(ears, face: str, size: int) -> str:
    """Mochi on its matcha disc: the splash and the in-app Mochi."""
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 108 108">'
            f'<defs>{glow("g", cx="50%", cy="28%", r="78%")}</defs><circle cx="54" cy="54" r="54" fill="url(#g)"/>'
            f'<g transform="translate(8.6 5.5) scale(.84)">{bunny(ears, face)}</g></svg>')


def icon() -> tuple[str, str]:
    """The adaptive icon: the glow behind (centred on the 72 dp that shows), the bunny in front (in the safe zone)."""
    # 456 px for 108 dp: the visible circle is 304 px across, centred.
    back = (f'<svg xmlns="http://www.w3.org/2000/svg" width="456" height="456" viewBox="0 0 456 456"><defs>'
            f'{glow("g", cx="228", cy="161", r="237", gradientUnits="userSpaceOnUse")}</defs>'
            f'<rect width="456" height="456" fill="url(#g)"/></svg>')
    front = (f'<svg xmlns="http://www.w3.org/2000/svg" width="456" height="456" viewBox="-18 -20 144 144">'
             f'{bunny([EAR_L_UP, EAR_R_FLOP], "happy")}</svg>')
    return back, front


# --- The wordmark: "moch" and a dotless "ı" from Fredoka, and the logo bunny as the dot. -------------------------
TRACKING = -20          # font units (2%), between letters
DOT_SCALE = 5.9         # font units per bunny unit: the dot is about 0.46 em wide
DOT_GAP = 70            # font units between the top of the stem and the bunny's flat bottom
DOT_EARS = [EAR_L_UP, EAR_R_FLOP]


def wordmark():
    font = TTFont(ROOT / 'store-assets/_src/fonts/Fredoka-SemiBold.ttf')
    glyphs, cmap = font.getGlyphSet(), font.getBestCmap()

    def outline(text: str, x0: float):
        """The glyphs of text from x0, in font units; returns the pen's commands and the x after."""
        pen, x = SVGPathPen(None, ntos=fmt), x0
        for ch in text:
            glyph = glyphs[cmap[ord(ch)]]
            glyph.draw(TransformPen(pen, (1, 0, 0, 1, x, 0)))
            x += glyph.width + TRACKING
        return pen, x

    first = BoundsPen(glyphs)
    glyphs[cmap[ord('m')]].draw(first)
    start = -first.bounds[0]
    letters, x = outline('moch', start)
    each, cursor = [], start
    for ch in 'moch':
        pen, cursor = outline(ch, cursor)
        each.append(pen)
    stem_glyph = glyphs[cmap[0x131]]
    stem_bounds = BoundsPen(glyphs)
    stem_glyph.draw(stem_bounds)
    stem, _ = outline('ı', x)
    stem_centre = x + (stem_bounds.bounds[0] + stem_bounds.bounds[2]) / 2
    stem_top = stem_bounds.bounds[3]

    # The bunny (108 grid, y down) into font units (y up): its centre over the stem, its flat bottom above it.
    to_font = (DOT_SCALE, 0, 0, -DOT_SCALE, stem_centre - 54 * DOT_SCALE, stem_top + DOT_GAP + 84.5 * DOT_SCALE)

    def bunny_path(*ds: str):
        pen = SVGPathPen(None, ntos=fmt)
        for d in ds:
            parse_path(d, TransformPen(pen, to_font))
        return pen

    dot = bunny_path(DOT_EARS[0][0], DOT_EARS[1][0], BODY)
    inner = bunny_path(DOT_EARS[0][1], DOT_EARS[1][1])
    eyes = bunny_path('M38 66 Q43 60.5 48 66', 'M60 66 Q65 60.5 70 66')

    # The box: from the top of the bunny's ear to the lowest point of the letters.
    top = stem_top + DOT_GAP + (84.5 - 19) * DOT_SCALE + 10
    bottom = -20
    right = stem_centre + (90 - 54) * DOT_SCALE + 10   # the flopped ear reaches past the stem

    def svg_units(pen) -> str:
        """Font units to wordmark units (100 per em, y down, from the top of the box)."""
        out = SVGPathPen(None, ntos=fmt)
        # Re-draw the commands through a transform by replaying the path string.
        parse_path(pen.getCommands(), TransformPen(out, (0.1, 0, 0, -0.1, 0, top / 10)))
        return out.getCommands()

    return {
        'letters': svg_units(letters), 'each': [svg_units(p) for p in each], 'stem': svg_units(stem), 'dot': svg_units(dot), 'inner': svg_units(inner), 'eyes': svg_units(eyes),
        'eyes_width': round(5 * DOT_SCALE / 10, 2), 'width': round(right / 10, 1), 'height': round((top - bottom) / 10, 1),
        'to_font': to_font, 'top': top, 'right': right, 'bottom': bottom,
    }


def dot_transform(w) -> str:
    """Bunny units (108 grid) straight to wordmark units."""
    sx, _, _, sy, tx, ty = w['to_font']
    return f'matrix({fmt(sx / 10)} 0 0 {fmt(-sy / 10)} {fmt(tx / 10)} {fmt((w["top"] - ty) / 10)})'


def large_dot(mode: str) -> str:
    """The bunny dot at large sizes, in bunny units: soft flat like the icon, with the logo's face."""
    ears = ''.join(f'<path d="{o}" fill="{BRAND[mode]}"/><path d="{i}" fill="{INNER_EAR}"/>' for o, i in DOT_EARS)
    return (f'{ears}<path d="{BODY}" fill="{BRAND[mode]}"/>'
            f'<path d="{HIGHLIGHT}" stroke="#FFFDF8" stroke-width="3.4" fill="none" stroke-linecap="round" opacity=".7"/>{FACES["happy"]}')


def wordmark_svg(w, mode: str) -> str:
    """The large wordmark (store, video): the bunny dot with the logo's face, soft flat like the icon."""
    dot = f'<g transform="{dot_transform(w)}">{large_dot(mode)}</g>'
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w["width"]} {w["height"]}" width="{w["width"] * 4}" height="{w["height"] * 4}">'
            f'<path d="{w["letters"]}" fill="{INK[mode]}"/><path d="{w["stem"]}" fill="{STEM[mode]}"/>{dot}</svg>')


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text + ('' if text.endswith('\n') else '\n'))
    print('wrote', path.relative_to(ROOT))


def main() -> None:
    back, front = icon()
    write(MOBILE / 'Resources/AppIcon/appicon.svg', back)
    write(MOBILE / 'Resources/AppIcon/appiconfg.svg', front)
    write(MOBILE / 'Resources/Splash/splash.svg', disc([EAR_L_UP, EAR_R_FLOP], 'happy', 128))
    write(MOBILE / 'Resources/Images/mochi_awake.svg', disc([EAR_L_UP, EAR_R_UP], 'awake', 96))
    write(MOBILE / 'Resources/Images/mochi_asleep.svg', disc([EAR_L_FLOP, EAR_R_FLOP], 'asleep', 96))

    w = wordmark()
    write(ROOT / 'store-assets/brand/mochi-wordmark-day.svg', wordmark_svg(w, 'day'))
    write(ROOT / 'store-assets/brand/mochi-wordmark-night.svg', wordmark_svg(w, 'night'))
    write(ROOT / 'store-assets/brand/mochi-icon.svg', icon_flat())
    cs = f'''// <auto-generated>Made by eng/gen-brand.py from Fredoka SemiBold and the Mochi shapes; edit the script, not this file.</auto-generated>
namespace Coo.Mobile.Components;

/// <summary>The "mochi" wordmark as outlines, 100 units to the em, y down from the top of the bunny's ear.</summary>
internal static class WordmarkGeometry
{{
    public const double Width = {w['width']};

    public const double Height = {w['height']};

    /// <summary>"moch", in the ink.</summary>
    public const string Letters = "{w['letters']}";

    /// <summary>The stem of the "i".</summary>
    public const string Stem = "{w['stem']}";

    /// <summary>The dot: the logo bunny, flat.</summary>
    public const string Dot = "{w['dot']}";

    /// <summary>The pink inside of the bunny's ears, so the small dot reads as a bunny.</summary>
    public const string InnerEars = "{w['inner']}";

    /// <summary>Two sleepy eyes on the dot, stroked <see cref="EyesStroke"/> wide, so the small dot reads as Mochi.</summary>
    public const string Eyes = "{w['eyes']}";

    public const double EyesStroke = {w['eyes_width']};
}}
'''
    write(MOBILE / 'Components/WordmarkGeometry.g.cs', cs)
    letters = ',\n    '.join(f"'{d}'" for d in w['each'])
    ts = f'''// Made by eng/gen-brand.py: the "mochi" wordmark for the film, 100 units to the em. Edit the script, not this file.
export const WORDMARK = {{
  width: {w['width']},
  height: {w['height']},
  /** m, o, c, h, each on its own so they can arrive one by one. */
  letters: [
    {letters},
  ],
  stem: '{w['stem']}',
  /** Places the bunny (its 108 grid) on the stem. */
  dotTransform: '{dot_transform(w)}',
  /** The bunny dot, soft flat with the logo's face, in the day colours. */
  dot: '{large_dot('day')}',
}};
'''
    write(ROOT / 'store-assets/video/src/wordmark.g.ts', ts)


def icon_flat() -> str:
    """The icon as one square picture (512 for the store): the glow, then the bunny at the launcher's size."""
    # The launcher shows the middle 72 dp of the foreground's 144-unit window (-18 -20): 96 units from (6, 4).
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 96 96">'
            f'<defs>{glow("g", cx="50%", cy="28%", r="78%")}</defs><rect width="96" height="96" fill="url(#g)"/>'
            f'<g transform="translate(-6 -4)">{bunny([EAR_L_UP, EAR_R_FLOP], "happy")}</g></svg>')


if __name__ == '__main__':
    main()
