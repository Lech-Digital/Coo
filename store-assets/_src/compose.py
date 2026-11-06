#!/usr/bin/env python3
"""Store images from the demo build's screenshots (store-assets/PLAN.md §3): captioned 1080x1920 phone images, the
1024x500 feature graphic and the 512 px icon, rendered from HTML by headless Chrome. Raw shots: store-assets/raw/
(made with -p:CooDemo=true on the coo-review emulator, 2026-11-05). Run: python3 store-assets/_src/compose.py
(add `brand` to render only the icon and the feature graphic)."""
import base64, os, subprocess, tempfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
REPO = os.path.abspath(os.path.join(ROOT, '..'))
CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
FONTS = os.path.join(REPO, 'src/Mobile/Coo.Mobile/Resources/Fonts')

# slot, raw shot, headline (the part in <em> is accented), subline, night look
SHOTS = [
    (1, 's2-log', 'Every feed, nap and diaper <em>on one timeline.</em>', 'The whole day at a glance', False),
    (2, 's1-today', 'One tap to start. <em>Mochi keeps the time.</em>', 'Feeds, naps, diapers and pumping, live', False),
    (3, 's7-night', 'Night colours <em>for night feeds.</em>', 'A dark screen and big buttons for one hand', True),
    (4, 's4-stats', 'Your baby’s average day, <em>7, 14 or 30 days.</em>', 'Milk, feeds, sleep, diapers and pumping', False),
    (5, 's3-zoom', 'Sleep that runs <em>past midnight.</em>', 'Zoom into any hours of the day or night', False),
    (6, 's6-advisor', 'Questions about your baby? <em>Ask the Advisor.</em>', 'AI answers that know your baby’s log · not medical advice', False),
]

def font_face():
    css = ''
    for weight, name in [(400, 'Regular'), (600, 'SemiBold'), (700, 'Bold')]:
        data = base64.b64encode(open(os.path.join(FONTS, f'Figtree-{name}.ttf'), 'rb').read()).decode()
        css += f"@font-face{{font-family:Figtree;font-weight:{weight};src:url(data:font/ttf;base64,{data})}}"
    return css

def b64(path):
    return base64.b64encode(open(path, 'rb').read()).decode()

def render(html, out, width, height):
    with tempfile.NamedTemporaryFile('w', suffix='.html', delete=False, encoding='utf-8') as f:
        f.write(html)
    subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
                    f'--window-size={width},{height}', f'--screenshot={out}', 'file://' + f.name],
                   check=True, capture_output=True)
    os.unlink(f.name)
    print(os.path.relpath(out, REPO))

FACES = font_face()

def phone(slot, shot, headline, subline, night):
    ground, ink, ink2, accent = ('#121511', '#E6EDD9', '#97A486', '#A3C47F') if night else ('#EEEEE6', '#262A22', '#62685A', '#4A7036')
    html = f"""<!doctype html><html><head><meta charset="utf-8"><style>{FACES}
    html,body{{margin:0;width:1080px;height:1920px;background:{ground};font-family:Figtree;overflow:hidden}}
    .cap{{position:absolute;top:96px;left:80px;right:80px;text-align:center}}
    h1{{margin:0;font-size:68px;line-height:1.12;font-weight:700;color:{ink};letter-spacing:-0.5px}}
    h1 em{{font-style:normal;color:{accent}}}
    p{{margin:26px 0 0;font-size:34px;color:{ink2}}}
    .phone{{position:absolute;left:50%;top:430px;transform:translateX(-50%);width:640px;height:1436px;border-radius:64px;
      overflow:hidden;box-shadow:0 30px 80px rgba(0,0,0,{0.5 if night else 0.18});border:14px solid {'#000' if night else '#262A22'}}}
    .phone img{{width:100%;display:block}}
    </style></head><body><div class="cap"><h1>{headline}</h1><p>{subline}</p></div>
    <div class="phone"><img src="data:image/png;base64,{b64(os.path.join(ROOT, 'raw', shot + '.png'))}"></div></body></html>"""
    render(html, os.path.join(ROOT, 'phone', f'phone-{slot}-{shot[3:]}.png'), 1080, 1920)

def icon_svg():
    # The launcher icon as one square (eng/gen-brand.py), soft flat on the matcha glow.
    return open(os.path.join(ROOT, 'brand', 'mochi-icon.svg'), encoding='utf-8').read()

def wordmark_svg():
    return open(os.path.join(ROOT, 'brand', 'mochi-wordmark-day.svg'), encoding='utf-8').read()

def icon():
    html = f"""<!doctype html><html><head><style>html,body{{margin:0;width:512px;height:512px;overflow:hidden}}
    svg{{width:512px;height:512px;display:block}}</style></head><body>{icon_svg()}</body></html>"""
    render(html, os.path.join(ROOT, 'icon-512.png'), 512, 512)

def feature():
    html = f"""<!doctype html><html><head><meta charset="utf-8"><style>{FACES}
    html,body{{margin:0;width:1024px;height:500px;background:#EEEEE6;font-family:Figtree;overflow:hidden}}
    .icon{{position:absolute;left:84px;top:110px;width:280px;height:280px;border-radius:64px;overflow:hidden;box-shadow:0 18px 50px rgba(0,0,0,.15)}}
    .icon svg{{width:280px;height:280px;display:block}}
    .t{{position:absolute;left:430px;top:118px;right:60px}}
    .wm svg{{height:118px;width:auto;display:block;margin-top:-30px}}
    p{{margin:14px 0 0;font-size:42px;line-height:1.22;color:#262A22;white-space:nowrap}}
    .k{{margin-top:26px;display:flex;gap:12px}}
    .k i{{display:block;width:62px;height:12px;border-radius:6px}}
    </style></head><body><div class="icon">{icon_svg()}</div>
    <div class="t"><div class="wm">{wordmark_svg()}</div><p>Baby tracker for feeds,<br>sleep, diapers &amp; pumping</p>
    <div class="k"><i style="background:#E07B4F"></i><i style="background:#C9A43A"></i><i style="background:#8E7CC3"></i><i style="background:#5B84A0"></i></div></div>
    </body></html>"""
    render(html, os.path.join(ROOT, 'feature-graphic.png'), 1024, 500)

if __name__ == '__main__':
    import sys
    if 'brand' not in sys.argv[1:]:
        os.makedirs(os.path.join(ROOT, 'phone'), exist_ok=True)
        for s in SHOTS:
            phone(*s)
    icon()
    feature()
