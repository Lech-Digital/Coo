# Fonts

**Figtree** is the only typeface (design system §3), licensed under the SIL Open Font License 1.1 (`OFL.txt`).
Its copyright line declares no Reserved Font Name, so modified versions may keep "Figtree" in their name.

## The original weights

These are the static TTFs from the upstream release, tag `v2.0.3` (https://github.com/erikdkennedy/figtree),
from `fonts/ttf/`. They are not modified.

| File | SHA-256 |
|---|---|
| `Figtree-Light.ttf` | `1106f4315201909c39a6f6dd40a4ab2ac6b23fea24ded6cfb27118673fa9a05f` |
| `Figtree-Regular.ttf` | `e6f1cae136bc7ea35fec04ffce4617d5e9137ac8844d6a5df619bcd5368ea4ee` |
| `Figtree-Medium.ttf` | `e605259d449b9f0e34dd935abb3d75d4f86759e5b947af6d11d9a7db28c8b3fd` |
| `Figtree-SemiBold.ttf` | `a63306f13cbf3864b092672073864b71df0bfd0cfc2e873c9e3e3a6b075eb574` |
| `Figtree-Bold.ttf` | `2a33fa55aa9411948d2ae776ffa9eaf97d72b8847b34857e7d59d6c8e26c563e` |

Figtree's default digits are proportional (advance widths 413–641 units in Regular), so a timer set in them would
jitter. The font has a `tnum` feature with tabular digits.

## Tabular figures: Android's `tnum` (decided 2026-10-01)

The style gallery compared, on a Pixel 10, plain Figtree with two ways to get tabular digits: Android's
`fontFeatureSettings = "tnum"` on the original fonts, and fonts with `tnum` frozen in by `pyftfeatfreeze`.
Plain Figtree jittered ("1111" narrower than "8888"); both tabular approaches aligned perfectly, in all five weights.
**The `tnum` font feature is used** (`Components/Tabular.cs`), so the fonts stay unmodified. The frozen fonts were
deleted (Spec 005 §4.2).
