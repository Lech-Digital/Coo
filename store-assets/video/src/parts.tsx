import React from 'react';
import { AbsoluteFill, Img, interpolate, spring, staticFile, useCurrentFrame, useVideoConfig } from 'remotion';
import { WORDMARK } from './wordmark.g';

/** The app's own colours (src/Mobile/Coo.Mobile.Presentation/Theme/WashiPalette.cs). */
export const C = {
  ink: '#262A22',
  ink2: '#62685A',
  accent: '#4A7036',
  accentSoft: '#DFEBC8',
  brand: '#93B86A',
  glass: 'rgba(250,250,245,0.82)',
  glassLine: 'rgba(216,218,203,0.8)',
  feed: { tint: '#F5DCCB', mark: '#CC6F3B', ink: '#7A3F20' },
  diaper: { tint: '#F3E6B3', mark: '#A08528', ink: '#6A5410' },
  sleep: { tint: '#E2DCEF', mark: '#8E7FBE', ink: '#473A78' },
  pump: { tint: '#DCE6EE', mark: '#5B84A0', ink: '#2B4A5F' },
  growth: { tint: '#DFEBC8', mark: '#4A7036', ink: '#3D5E2B' },
  night: { ground: '#121511', ink: '#D9E3C8', ink2: '#97A486', accent: '#A3C47F' },
};

/** The app's icons (IconGeometry.cs), 24×24, stroked. */
export const ICON = {
  feed: 'M9 3h6M10 3v3l-2 3v10a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2V9l-2-3V3M8 13h8',
  diaper: 'M12 3.5c3 4 6 7.2 6 10.5a6 6 0 0 1-12 0c0-3.3 3-6.5 6-10.5z',
  sleep: 'M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z',
  pump: 'M5 5h14l-5 5v3h-4v-3zM10 13v6a2 2 0 0 0 4 0v-6',
  growth: 'M4 4v16h16M7 15l4-4 3 3 5-6',
  family: 'M6 8a3 3 0 1 0 6 0a3 3 0 1 0-6 0M3 20a6 6 0 0 1 12 0M16 5a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 6',
  sparkle: 'M10 3l1.8 4.7L16.5 9.5l-4.7 1.8L10 16l-1.8-4.7L3.5 9.5l4.7-1.8zM18 14l0.9 2.1 2.1 0.9-2.1 0.9-0.9 2.1-0.9-2.1-2.1-0.9 2.1-0.9z',
};

export const Icon: React.FC<{ d: string; color: string; size: number; stroke?: number }> = ({ d, color, size, stroke = 2 }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color} strokeWidth={stroke} strokeLinecap="round" strokeLinejoin="round">
    <path d={d} />
  </svg>
);

/** A spring from a given frame: 0 → 1 with a little overshoot. */
export const useSpring = (from: number, damping = 12, mass = 0.6) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return spring({ frame: frame - from, fps, config: { damping, mass, stiffness: 140 } });
};

/** The soft colour wash behind every page: matcha, lavender and peach, drifting slowly; or the night's. */
export const Wash: React.FC<{ night?: number }> = ({ night = 0 }) => {
  const frame = useCurrentFrame();
  const dx = Math.sin(frame / 90) * 6;
  const dy = Math.cos(frame / 110) * 5;
  const day = `radial-gradient(70% 45% at ${100 + dx}% ${0 + dy}%, #BFD6AE 0%, rgba(191,214,174,0) 70%),
    radial-gradient(60% 40% at ${0 - dx}% ${42 + dy}%, #D3C9EC 0%, rgba(211,201,236,0) 70%),
    radial-gradient(70% 45% at ${20 + dy}% ${100 - dx}%, #F2CDB4 0%, rgba(242,205,180,0) 70%),
    linear-gradient(180deg, #E2EAD8 0%, #F2EBE0 100%)`;
  const dark = `radial-gradient(70% 45% at ${100 + dx}% 0%, #1F2C1C 0%, rgba(31,44,28,0) 70%),
    radial-gradient(60% 40% at 0% 45%, #221D33 0%, rgba(34,29,51,0) 70%),
    radial-gradient(70% 45% at 20% 100%, #2B1D16 0%, rgba(43,29,22,0) 70%),
    linear-gradient(180deg, #121511 0%, #14120F 100%)`;
  return (
    <AbsoluteFill>
      <AbsoluteFill style={{ background: day }} />
      <AbsoluteFill style={{ background: dark, clipPath: `circle(${night * 125}% at 50% 100%)` }} />
    </AbsoluteFill>
  );
};

/** A slim phone with a real screen from the demo build. */
export const Phone: React.FC<{ src: string; width?: number; style?: React.CSSProperties; children?: React.ReactNode }> = ({
  src,
  width = 620,
  style,
  children,
}) => {
  const height = (width * 2424) / 1080;
  const bezel = width * 0.028;
  return (
    <div
      style={{
        width: width + bezel * 2,
        height: height + bezel * 2,
        borderRadius: width * 0.12,
        background: '#1A1C18',
        padding: bezel,
        boxShadow: '0 60px 120px rgba(38,42,34,0.28), 0 18px 40px rgba(38,42,34,0.18)',
        position: 'relative',
        ...style,
      }}
    >
      <div style={{ width, height, borderRadius: width * 0.1, overflow: 'hidden', position: 'relative' }}>
        <Img src={staticFile(src)} style={{ width, height, display: 'block' }} />
        {children}
      </div>
    </div>
  );
};

/** Mochi: the bunny on its matcha glow, soft flat (eng/gen-brand.py); <c>sleep</c> 0 is the logo (one ear up, one
 * flopped, eyes open), 1 both ears flopped and eyes closed. */
export const Mochi: React.FC<{ size: number; sleep?: number; squash?: number }> = ({ size, sleep = 0, squash = 0 }) => {
  const earL = interpolate(sleep, [0, 1], [0, -62]);
  const sy = 1 - squash * 0.12;
  const sx = 1 + squash * 0.08;
  const line = { stroke: '#E3D8C2', strokeWidth: 1.2, strokeLinejoin: 'round' as const };
  return (
    <svg width={size} height={size} viewBox="0 0 108 108">
      <defs>
        <radialGradient id="mochi-glow" cx="50%" cy="28%" r="78%">
          <stop offset="0" stopColor="#B9D492" />
          <stop offset=".5" stopColor="#8DB462" />
          <stop offset="1" stopColor="#5E8A3E" />
        </radialGradient>
      </defs>
      <circle cx="54" cy="54" r="54" fill="url(#mochi-glow)" />
      <g transform="translate(8.6 5.5) scale(.84)">
        <ellipse cx="54" cy="85.6" rx="31" ry="3.4" fill="#2F4A1F" opacity=".22" />
        <g transform={`translate(54 84.5) scale(${sx} ${sy}) translate(-54 -84.5)`}>
          <g transform={`rotate(${earL} 42 44)`}>
            <path d="M38 46 C33 30 35 19 41 19 C47 19 48 31 46 43 Z" fill="#FBF7EF" {...line} />
            <path d="M40.5 42 C37.5 31 38.5 24 41.5 24 C44.5 24 45 32 43.6 41 Z" fill="#F5C2C0" />
          </g>
          <path d="M62 44 C63 34 70 27 80 27 C88 27 90 33 86 37 C82 41 74 42 68 47 Z" fill="#FBF7EF" {...line} />
          <path d="M66 42 C68 35 73 31 80 31 C85 31 86 34 83 36 C79 39 73 40 68.5 43.5 Z" fill="#F5C2C0" />
          <path d="M18 72 C18 51 34 40 54 40 C74 40 90 51 90 72 C90 81 83 84.5 72 84.5 H36 C25 84.5 18 81 18 72 Z" fill="#FBF7EF" {...line} />
          <path d="M27 60 C29 53 34 48 40 45.5" stroke="#FFFDF8" strokeWidth="3" fill="none" strokeLinecap="round" opacity=".95" />
          <g opacity={1 - sleep}>
            <circle cx="44" cy="63" r="2.8" fill="#4A3326" />
            <circle cx="64" cy="63" r="2.8" fill="#4A3326" />
            <circle cx="44.9" cy="62" r=".85" fill="#FAFAF5" />
            <circle cx="64.9" cy="62" r=".85" fill="#FAFAF5" />
          </g>
          <g opacity={sleep}>
            <path d="M40 63 q4 3.4 8 0" stroke="#4A3326" strokeWidth="2.4" fill="none" strokeLinecap="round" />
            <path d="M60 63 q4 3.4 8 0" stroke="#4A3326" strokeWidth="2.4" fill="none" strokeLinecap="round" />
          </g>
          <ellipse cx="37.5" cy="70" rx="4.6" ry="2.9" fill="#F0A08F" opacity=".85" />
          <ellipse cx="70.5" cy="70" rx="4.6" ry="2.9" fill="#F0A08F" opacity=".85" />
          <path d="M50.2 69 q3.8 3.4 7.6 0" stroke="#4A3326" strokeWidth="2.1" fill="none" strokeLinecap="round" />
        </g>
      </g>
    </svg>
  );
};

/** The wordmark (Fredoka outlines, wordmark.g.ts): <c>letter(i)</c> 0..1 brings in m, o, c, h and the stem one by one,
 * <c>dot</c> 0..1 drops the bunny onto the "i". */
export const Wordmark: React.FC<{ height: number; letter?: (i: number) => number; dot?: number; ink?: string; accent?: string }> = ({
  height,
  letter = () => 1,
  dot = 1,
  ink = C.ink,
  accent = C.accent,
}) => {
  const scale = height / WORDMARK.height;
  const rise = (v: number) => `translate(0 ${(1 - v) * 30})`;
  return (
    <svg width={WORDMARK.width * scale} height={height} viewBox={`0 0 ${WORDMARK.width} ${WORDMARK.height}`} style={{ overflow: 'visible' }}>
      {WORDMARK.letters.map((d, i) => (
        <path key={i} d={d} fill={ink} opacity={Math.min(1, letter(i))} transform={rise(letter(i))} />
      ))}
      <path d={WORDMARK.stem} fill={accent} opacity={Math.min(1, letter(4))} transform={rise(letter(4))} />
      <g transform={`translate(0 ${(1 - dot) * -40})`} opacity={Math.min(1, dot * 2)}>
        <g transform={WORDMARK.dotTransform} dangerouslySetInnerHTML={{ __html: WORDMARK.dot }} />
      </g>
    </svg>
  );
};

/** A caption at the top: a small kicker in the accent, then the line, each springing up on its own beat. */
export const Caption: React.FC<{ kicker: string; text: string; from: number; dark?: boolean; top?: number }> = ({ kicker, text, from, dark, top = 150 }) => {
  const a = useSpring(from, 14);
  const b = useSpring(from + 4, 14);
  const frame = useCurrentFrame();
  return (
    <div style={{ position: 'absolute', top, left: 70, right: 70, textAlign: 'left' }}>
      <div
        style={{
          fontSize: 30,
          fontWeight: 700,
          letterSpacing: 4,
          color: dark ? C.night.accent : C.accent,
          opacity: a,
          transform: `translateY(${(1 - a) * 30}px)`,
          textTransform: 'uppercase',
        }}
      >
        {kicker}
      </div>
      <div
        style={{
          marginTop: 14,
          fontSize: 76,
          lineHeight: 1.06,
          fontWeight: 600,
          letterSpacing: -2,
          color: dark ? C.night.ink : C.ink,
          opacity: interpolate(frame - from - 4, [0, 6], [0, 1], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' }),
          transform: `translateY(${(1 - b) * 60}px)`,
        }}
      >
        {text}
      </div>
    </div>
  );
};

/** A frosted card as in the app. */
export const Glass: React.FC<{ style?: React.CSSProperties; children: React.ReactNode; dark?: boolean }> = ({ style, children, dark }) => (
  <div
    style={{
      background: dark ? 'rgba(27,31,25,0.86)' : C.glass,
      border: `2px solid ${dark ? '#2C3228' : C.glassLine}`,
      borderRadius: 40,
      boxShadow: '0 24px 60px rgba(38,42,34,0.12)',
      ...style,
    }}
  >
    {children}
  </div>
);

export const Fonts: React.FC = () => (
  <style>{`
    @font-face { font-family: Figtree; src: url(${staticFile('Figtree-Regular.ttf')}) format('truetype'); font-weight: 400; }
    @font-face { font-family: Figtree; src: url(${staticFile('Figtree-Medium.ttf')}) format('truetype'); font-weight: 500; }
    @font-face { font-family: Figtree; src: url(${staticFile('Figtree-SemiBold.ttf')}) format('truetype'); font-weight: 600; }
    @font-face { font-family: Figtree; src: url(${staticFile('Figtree-Bold.ttf')}) format('truetype'); font-weight: 700; }
    @font-face { font-family: Figtree; src: url(${staticFile('Figtree-Light.ttf')}) format('truetype'); font-weight: 300; }
  `}</style>
);
