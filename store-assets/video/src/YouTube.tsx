import React from 'react';
import { AbsoluteFill, Audio, Easing, Img, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from 'remotion';
import { C, Fonts, Glass, Icon, ICON, Mochi, Phone, Wash, Wordmark } from './parts';

/** The YouTube film: 16:9, 1920 × 1080, 68 s, the launch film's beats with room to breathe: the words on the left, the
 * real screens on the right. 120 BPM at 30 fps: a beat is 15 frames, a bar 60; every cut lands on a bar. */
const BEAT = 15;
const clamp = { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' } as const;

// The app's icons (IconGeometry.cs) the vertical film doesn't need.
const MORE_ICON = {
  growth: 'M4 17L17 4l3 3L7 20zM8 13l2 2M11 10l2 2M14 7l2 2',
  medicine: 'M11.43 17.37L17.37 11.43A3.4 3.4 0 0 0 12.57 6.63L6.63 12.57A3.4 3.4 0 0 0 11.43 17.37zM14.40 14.40L9.60 9.60',
  temperature: 'M10 5a2 2 0 0 1 4 0v9.3a4 4 0 1 1-4 0zM12 9v8',
  vaccine: 'M17 3l4 4M19 5l-9.5 9.5-4-4L15 1M7.5 12.5L3 17M4 21l3-3M8 9l2 2M11 6l2 2',
  visit: 'M6 3v6a4 4 0 0 0 8 0V3M10 13v2a5 5 0 0 0 10 0v-3M18 10a2 2 0 1 0 4 0a2 2 0 1 0-4 0',
  chart: 'M4 4v16h16M7 15l4-4 3 3 5-6',
  bath: 'M3 12h18v1.5A5.5 5.5 0 0 1 15.5 19h-7A5.5 5.5 0 0 1 3 13.5zM6 12V6.5a2.5 2.5 0 0 1 5 0M7 19l-1.5 2M17 19l1.5 2',
  tummy: 'M4 11.5a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0 -5 0M9 15h8.5a2.5 2.5 0 0 1 0 5H8.5M8 20l2-3M3 20h18',
  solids: 'M5 7a3 4 0 1 0 6 0a3 4 0 1 0 -6 0M8 11v10M13 13h8a4 4 0 0 1 -8 0z',
  milestone: 'M12 3l2.6 5.6 6.1 0.7-4.5 4.2 1.2 6L12 16.6 6.6 19.5l1.2-6L3.3 9.3l6.1-0.7z',
  mic: 'M9 6a3 3 0 0 1 6 0v5a3 3 0 0 1-6 0zM5 11a7 7 0 0 0 14 0M12 18v3',
  health: 'M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zM12 8v8M8 12h8',
};

export const useSpringAt = (from: number, damping = 13, stiffness = 140, mass = 0.6) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return spring({ frame: frame - from, fps, config: { damping, stiffness, mass } });
};

/** The words on the left: a kicker in the accent, the line, and an optional quieter line under it. */
export const Side: React.FC<{ kicker: string; text: string; sub?: string; from?: number; dark?: boolean; exit?: number }> = ({
  kicker,
  text,
  sub,
  from = 2,
  dark,
  exit = 0,
}) => {
  const a = useSpringAt(from, 14);
  const b = useSpringAt(from + 4, 14);
  const c = useSpringAt(from + 14, 14);
  return (
    <div
      style={{
        position: 'absolute',
        left: 130,
        width: 760,
        top: 540,
        transform: `translateY(-50%) translateX(${-exit * 900}px)`,
        opacity: 1 - exit,
      }}
    >
      <div
        style={{
          fontSize: 28,
          fontWeight: 700,
          letterSpacing: 4,
          textTransform: 'uppercase',
          color: dark ? C.night.accent : C.accent,
          opacity: a,
          transform: `translateY(${(1 - a) * 30}px)`,
        }}
      >
        {kicker}
      </div>
      <div
        style={{
          marginTop: 16,
          fontSize: 88,
          lineHeight: 1.04,
          fontWeight: 600,
          letterSpacing: -2.5,
          color: dark ? C.night.ink : C.ink,
          opacity: Math.min(1, b * 1.4),
          transform: `translateY(${(1 - b) * 60}px)`,
        }}
      >
        {text}
      </div>
      {sub && (
        <div
          style={{
            marginTop: 26,
            fontSize: 38,
            lineHeight: 1.3,
            fontWeight: 400,
            color: dark ? C.night.ink2 : C.ink2,
            opacity: c,
            transform: `translateY(${(1 - c) * 30}px)`,
          }}
        >
          {sub}
        </div>
      )}
    </div>
  );
};

const PHONE_W = 420;
const PHONE_LEFT = 1240;
const PHONE_TOP = 70;
const K = PHONE_W / 1080; // screen px per screenshot px

// Where things sit in the screenshots (1080 × 2424).
const START = { x: 184, y: 1616, w: 308, h: 98 }; // s1-today: Next up's "Start right"
const TIMER = { x: 66, y: 818, w: 946, h: 572 }; // s8-timer: the running feed card

const ring = (r: { x: number; y: number; w: number; h: number }, s: number, color: string, radius = 40) => (
  <div
    style={{
      position: 'absolute',
      left: r.x * K - 8,
      top: r.y * K - 8,
      width: r.w * K + 16,
      height: r.h * K + 16,
      borderRadius: radius,
      border: `5px solid ${color}`,
      boxShadow: '0 0 0 9999px rgba(38,42,34,0.16)',
      opacity: Math.min(1, s),
      transform: `scale(${1.4 - 0.4 * s})`,
    }}
  />
);

// 1. Words (0–4 s): the five things it logs, one per beat, then they collapse into Mochi.
const HOOK = [
  { word: 'Feeds.', k: C.feed, icon: ICON.feed },
  { word: 'Sleep.', k: C.sleep, icon: ICON.sleep },
  { word: 'Diapers.', k: C.diaper, icon: ICON.diaper },
  { word: 'Pumping.', k: C.pump, icon: ICON.pump },
  { word: 'Growth.', k: C.growth, icon: ICON.growth },
];

const Words: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const collapse = interpolate(frame, [88, 108], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const square = spring({ frame: frame - 100, fps, config: { damping: 11, stiffness: 160 } });
  return (
    <AbsoluteFill>
      {HOOK.map((h, i) => {
        const s = spring({ frame: frame - i * BEAT, fps, config: { damping: 11, mass: 0.6, stiffness: 170 } });
        const y = (i - 2) * 176;
        return (
          <div
            key={h.word}
            style={{
              position: 'absolute',
              top: 540 - 66 + y * (1 - collapse),
              left: 640 + collapse * 180,
              display: 'flex',
              alignItems: 'center',
              gap: 36,
              opacity: s * (1 - collapse),
              transform: `translateX(${(1 - s) * -160}px) scale(${(0.6 + 0.4 * s) * (1 - collapse * 0.7)})`,
            }}
          >
            <div style={{ width: 132, height: 132, borderRadius: 38, background: h.k.tint, display: 'grid', placeItems: 'center' }}>
              <Icon d={h.icon} color={h.k.ink} size={74} />
            </div>
            <div style={{ fontSize: 120, fontWeight: 600, letterSpacing: -4, color: h.k.ink }}>{h.word}</div>
          </div>
        );
      })}
      <div style={{ position: 'absolute', left: 960 - 190, top: 540 - 190, transform: `scale(${square})`, filter: 'drop-shadow(0 40px 60px rgba(63,107,74,0.35))' }}>
        <Mochi size={380} />
      </div>
    </AbsoluteFill>
  );
};

// 2. The question (4–8 s): "Left or right?", "Mochi remembers.", and a ring on Next up's "Start right".
const Question: React.FC = () => {
  const rise = useSpringAt(0, 15);
  const q = useSpringAt(BEAT, 11, 170);
  const a = useSpringAt(2 * BEAT, 11, 170);
  const r = useSpringAt(3 * BEAT, 9, 180);
  const big = { fontSize: 128, fontWeight: 600, letterSpacing: -4.5, lineHeight: 1.02 } as const;
  return (
    <AbsoluteFill>
      <div style={{ position: 'absolute', left: 130, top: 540, transform: 'translateY(-50%)' }}>
        <div style={{ ...big, color: C.ink, opacity: q, transform: `translateY(${(1 - q) * 70}px)` }}>Left or right?</div>
        <div style={{ ...big, color: C.accent, opacity: a, transform: `translateY(${(1 - a) * 70}px)` }}>Mochi remembers.</div>
      </div>
      <div style={{ position: 'absolute', left: PHONE_LEFT, top: PHONE_TOP + (1 - rise) * 1100 }}>
        <Phone src="s1-today.png" width={PHONE_W}>{ring(START, r, C.feed.mark)}</Phone>
      </div>
    </AbsoluteFill>
  );
};

// 3. Breastfeeding timer (8–14 s): the tap on "Start right", then the real running timer on the right side.
const Timer: React.FC = () => {
  const frame = useCurrentFrame();
  const tap = interpolate(frame, [10, 28], [0, 1], clamp);
  const swap = interpolate(frame, [22, 30], [0, 1], clamp);
  const r = useSpringAt(50, 10, 170);
  const exit = interpolate(frame, [168, 180], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  return (
    <AbsoluteFill>
      <Side kicker="Breastfeeding timer" text="One tap starts the next side." sub="Pause, switch sides, and it keeps the time for each." exit={exit} />
      <div style={{ position: 'absolute', left: PHONE_LEFT, top: PHONE_TOP, transform: `translateX(${exit * 900}px)` }}>
        <Phone src="s1-today.png" width={PHONE_W}>
          <Img src={staticFile('s8-timer.png')} style={{ position: 'absolute', inset: 0, width: '100%', opacity: swap }} />
          <div
            style={{
              position: 'absolute',
              left: (START.x + START.w / 2) * K - 70,
              top: (START.y + START.h / 2) * K - 70,
              width: 140,
              height: 140,
              borderRadius: 70,
              background: 'rgba(204,111,59,0.4)',
              transform: `scale(${tap * 2})`,
              opacity: tap > 0 ? 1 - tap : 0,
            }}
          />
          {ring(TIMER, r, C.feed.mark, 30)}
        </Phone>
      </div>
    </AbsoluteFill>
  );
};

// 4. Quick log (14–18 s): the four tiles, one per beat.
const TILES = [
  { k: C.feed, icon: ICON.feed, name: 'Feed', sub: 'breast or bottle' },
  { k: C.diaper, icon: ICON.diaper, name: 'Diaper', sub: 'wet or dirty' },
  { k: C.sleep, icon: ICON.sleep, name: 'Sleep', sub: 'nap or night' },
  { k: C.pump, icon: ICON.pump, name: 'Pump', sub: 'time and amount' },
];

const QuickLog: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const exit = interpolate(frame, [110, 120], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Quick log" text="Bottle, diaper, sleep, pump." sub="One tap each, or say it: “Tell Mochi”." />
      {TILES.map((t, i) => {
        const s = spring({ frame: frame - BEAT * (i + 1), fps, config: { damping: 10, stiffness: 170, mass: 0.6 } });
        const col = i % 2;
        const row = i >> 1;
        return (
          <div
            key={t.name}
            style={{
              position: 'absolute',
              left: 1000 + col * 400,
              top: 170 + row * 380,
              width: 360,
              height: 340,
              borderRadius: 48,
              background: t.k.tint,
              boxShadow: '0 30px 60px rgba(38,42,34,0.12)',
              padding: 40,
              boxSizing: 'border-box',
              transform: `scale(${s}) rotate(${(1 - s) * (col ? 8 : -8)}deg)`,
            }}
          >
            <Icon d={t.icon} color={t.k.ink} size={88} />
            <div style={{ position: 'absolute', right: 34, top: 34, width: 64, height: 64, borderRadius: 32, background: 'rgba(255,255,255,0.7)', display: 'grid', placeItems: 'center' }}>
              <Icon d="M12 5v14M5 12h14" color={t.k.ink} size={36} stroke={2.4} />
            </div>
            <div style={{ position: 'absolute', left: 40, bottom: 92, fontSize: 64, fontWeight: 600, letterSpacing: -1.5, color: t.k.ink }}>{t.name}</div>
            <div style={{ position: 'absolute', left: 40, bottom: 46, fontSize: 32, color: t.k.ink, opacity: 0.8 }}>{t.sub}</div>
          </div>
        );
      })}
    </AbsoluteFill>
  );
};

// 5. Baby log (18–24 s): the day as one band, then zoomed in.
const Day: React.FC = () => {
  const frame = useCurrentFrame();
  const a = useSpringAt(0, 16, 120);
  const b = useSpringAt(60, 15, 130);
  const exit = interpolate(frame, [170, 180], [0, 1], clamp);
  const w = 380;
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Baby log" text="The whole day on one timeline." sub="Tap it to zoom in on any six hours." />
      <div style={{ position: 'absolute', left: 1010, top: 110, transform: `translateX(${(1 - a) * 1000}px)` }}>
        <Phone src="s2-log.png" width={w} />
      </div>
      <div style={{ position: 'absolute', left: 1440, top: 150, transform: `translateX(${(1 - b) * 700}px) rotate(${3 * b}deg)` }}>
        <Phone src="s3-zoom.png" width={w} />
      </div>
    </AbsoluteFill>
  );
};

// 6. Sleep pattern (24–30 s): a fortnight side by side, noon to noon, until the nights line up.
const DAYS = Array.from({ length: 14 }, (_, d) => {
  const r = (n: number) => Math.abs(Math.sin(d * 12.9898 + n * 78.233) * 43758.5453) % 1;
  const nightStart = 21.6 - d * 0.075 + (r(1) - 0.5) * 0.5;
  const firstEnd = 1.2 + d * 0.06 + (r(2) - 0.5) * 0.6 + 24;
  const naps = [
    [13.2 + r(3) * 0.4, 14.4 + r(4) * 0.5],
    [16.1 + r(5) * 0.4, 16.9 + r(6) * 0.4],
    [8.7 + 24 + r(7) * 0.3, 9.9 + 24 + r(8) * 0.4],
  ];
  return { nightStart, firstEnd, secondStart: firstEnd + 0.35, secondEnd: 30.5 + r(9) * 0.4, naps, feeds: [12.5, 15.4, 18.2, 20.9, firstEnd + 0.05, 30.6, 33.6].map((f, i) => f + (r(10 + i) - 0.5) * 0.4) };
});
const USUAL = [...DAYS.map((d) => d.nightStart)].sort((a, b) => a - b)[DAYS.length >> 1];
const USUAL_LABEL = `${Math.floor(USUAL) - 12}:${String(Math.round((USUAL % 1) * 60)).padStart(2, '0')} PM`;

const Pattern: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const card = useSpringAt(0, 15);
  const exit = interpolate(frame, [170, 180], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const W = 860;
  const H = 900;
  const chartTop = 100;
  const chartH = 730;
  const left = 130;
  const slot = (W - left - 40) / DAYS.length;
  const y = (h: number) => chartTop + ((h - 12) / 24) * chartH;
  const line = useSpringAt(80, 13);
  return (
    <AbsoluteFill>
      <Side kicker="Sleep pattern" text="Her sleep pattern, from your own log." sub="Two weeks side by side, noon to noon." exit={exit} />
      <div style={{ position: 'absolute', left: 960, top: 90, transform: `translateY(${(1 - card) * 1000 - exit * 1200}px)` }}>
        <Glass style={{ width: W, height: H, position: 'relative', overflow: 'hidden' }}>
          <div style={{ position: 'absolute', left: 40, top: 32, fontSize: 32, fontWeight: 600, color: C.ink }}>Last 14 days · noon to noon</div>
          {['12 PM', '6 PM', '12 AM', '6 AM', '12 PM'].map((t, i) => (
            <React.Fragment key={i}>
              <div style={{ position: 'absolute', left: 10, width: 100, textAlign: 'right', top: y(12 + i * 6) - 15, fontSize: 22, color: C.ink2 }}>{t}</div>
              <div style={{ position: 'absolute', left, right: 40, top: y(12 + i * 6), height: 2, background: 'rgba(216,218,203,0.7)' }} />
            </React.Fragment>
          ))}
          {DAYS.map((d, i) => {
            const grow = spring({ frame: frame - 14 - i * 3, fps, config: { damping: 14, stiffness: 160 } });
            const x = left + slot * i + slot * 0.18;
            const w = slot * 0.64;
            const bar = (from: number, to: number, solid: boolean, key: string) => (
              <div key={key} style={{ position: 'absolute', left: x, top: y(from), width: w, height: (y(to) - y(from)) * grow, borderRadius: 9, background: C.sleep.mark, opacity: solid ? 1 : 0.45 }} />
            );
            return (
              <React.Fragment key={i}>
                <div style={{ position: 'absolute', left: x, top: chartTop, width: w, height: chartH * grow, borderRadius: 9, background: 'rgba(226,220,239,0.45)' }} />
                {d.naps.map((n, j) => bar(n[0], n[1], false, `n${j}`))}
                {bar(d.nightStart, d.firstEnd, true, 'night')}
                {bar(d.secondStart, d.secondEnd, false, 'second')}
                {d.feeds.map((f, j) => (
                  <div key={`f${j}`} style={{ position: 'absolute', left: x - 5, top: y(f) - 2, width: w + 10, height: 5, borderRadius: 3, background: C.feed.mark, opacity: grow }} />
                ))}
              </React.Fragment>
            );
          })}
          <div style={{ position: 'absolute', left, right: 40, top: y(USUAL) - 2, borderTop: `5px dashed ${C.sleep.ink}`, opacity: line, transform: `scaleX(${line})`, transformOrigin: '0 50%' }} />
          <div
            style={{
              position: 'absolute',
              right: 50,
              top: y(USUAL) - 90,
              transform: `scale(${line})`,
              transformOrigin: '100% 100%',
              background: C.sleep.ink,
              color: '#fff',
              borderRadius: 22,
              padding: '12px 24px',
              fontSize: 30,
              fontWeight: 600,
            }}
          >
            Longest sleep usually from {USUAL_LABEL}
          </div>
        </Glass>
      </div>
    </AbsoluteFill>
  );
};

// 7. Next up (30–36 s): what comes next, one card per bar's beat. The next nap is Premium and says so.
const CARDS = [
  { k: C.feed, icon: ICON.feed, label: 'Next side', value: 'right', premium: false },
  { k: C.feed, icon: ICON.feed, label: 'Next feed', value: 'likely 12:37 – 1:07 PM', premium: false },
  { k: C.sleep, icon: ICON.sleep, label: 'Next nap', value: 'likely 1:10 – 1:35 PM', premium: true },
];

const NextUp: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const exit = interpolate(frame, [170, 180], [0, 1], clamp);
  const peek = useSpringAt(110, 10);
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Next up" text="When’s the next feed? Mochi works it out." sub="On your phone, from her own log. “Why this time?” shows how." />
      {CARDS.map((c, i) => {
        const s = spring({ frame: frame - BEAT * 2 - i * 30, fps, config: { damping: 10, stiffness: 170, mass: 0.6 } });
        return (
          <div key={i} style={{ position: 'absolute', left: 1000, width: 800, top: 190 + i * 250, transform: `translateX(${(1 - s) * 1000}px) rotate(${(1 - s) * 6}deg)` }}>
            <Glass style={{ padding: '32px 38px', display: 'flex', alignItems: 'center', gap: 34 }}>
              <div style={{ width: 120, height: 120, borderRadius: 36, background: c.k.tint, display: 'grid', placeItems: 'center', flexShrink: 0 }}>
                <Icon d={c.icon} color={c.k.ink} size={66} />
              </div>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
                  <div style={{ fontSize: 28, fontWeight: 700, letterSpacing: 3, color: C.ink2, textTransform: 'uppercase' }}>{c.label}</div>
                  {c.premium && <div style={{ fontSize: 20, fontWeight: 700, letterSpacing: 2, color: '#6B4E0A', background: '#F6E2A8', borderRadius: 10, padding: '4px 12px' }}>PREMIUM</div>}
                </div>
                <div style={{ fontSize: 56, fontWeight: 600, letterSpacing: -1.5, color: c.k.ink }}>{c.value}</div>
              </div>
            </Glass>
          </div>
        );
      })}
      <div style={{ position: 'absolute', left: 1640, top: 900, transform: `translateY(${(1 - peek) * 300}px) rotate(${Math.sin(frame / 5) * 4}deg)` }}>
        <Mochi size={170} sleep={interpolate(frame, [130, 136, 144, 150], [0, 1, 1, 0], clamp)} />
      </div>
    </AbsoluteFill>
  );
};

// 8. Stats & trends (36–40 s): this week against the weeks before, and the averages.
const Trends: React.FC = () => {
  const frame = useCurrentFrame();
  const a = useSpringAt(0, 15, 130);
  const b = useSpringAt(10, 15, 130);
  const exit = interpolate(frame, [110, 120], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Stats & trends" text="By day, week or month." sub="Sleep, feeds and diapers, against the week before." />
      <div style={{ position: 'absolute', left: 1010, top: 130, transform: `translateY(${(1 - a) * 1100}px) rotate(${-3 * a}deg)` }}>
        <Phone src="s4-stats.png" width={380} />
      </div>
      <div style={{ position: 'absolute', left: 1440, top: 90, transform: `translateY(${(1 - b) * 1100}px)` }}>
        <Phone src="s9-trends.png" width={380} />
      </div>
    </AbsoluteFill>
  );
};

// 9. Night mode (40–46 s): the dark wash wipes up and the night screen rises with it; the drums rest.
const Night: React.FC = () => {
  const frame = useCurrentFrame();
  const rise = useSpringAt(6, 15, 120);
  const exit = interpolate(frame, [170, 180], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Night mode" text="Night colours. Big buttons." sub="For one hand in the dark, by itself during your quiet hours." from={6} dark />
      <div style={{ position: 'absolute', left: PHONE_LEFT, top: PHONE_TOP + (1 - rise) * 1100 }}>
        <Phone src="s7-night.png" width={PHONE_W} />
      </div>
      <div style={{ position: 'absolute', left: 1700, top: 800, transform: `scale(${rise}) rotate(${Math.sin(frame / 8) * 3}deg)` }}>
        <Mochi size={190} sleep={1} />
      </div>
    </AbsoluteFill>
  );
};

// 10. Together (46–52 s): two phones, two parents, one log.
const Together: React.FC = () => {
  const frame = useCurrentFrame();
  const a = useSpringAt(4, 13);
  const b = useSpringAt(12, 13);
  const exit = interpolate(frame, [170, 180], [0, 1], clamp);
  const w = 360;
  const parent = (name: string, s: number, color: string) => (
    <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 18, opacity: s, transform: `scale(${s})` }}>
      <div style={{ width: 60, height: 60, borderRadius: 30, background: color, color: '#fff', display: 'grid', placeItems: 'center', fontSize: 30, fontWeight: 700 }}>{name[0]}</div>
      <div style={{ fontSize: 36, fontWeight: 600, color: C.ink }}>{name}</div>
    </div>
  );
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="Shared baby log" text="One log for the whole family." sub="Each parent on their own phone. A nanny can log too, grandparents can follow." />
      <div style={{ position: 'absolute', left: 1030, top: 70, transform: `translateX(${(1 - a) * 900}px) rotate(${-4 * a}deg)` }}>
        {parent('Anna', a, C.feed.mark)}
        <Phone src="s2-log.png" width={w} />
      </div>
      <div style={{ position: 'absolute', left: 1450, top: 130, transform: `translateX(${(1 - b) * 900}px) rotate(${4 * b}deg)` }}>
        {parent('Tom', b, C.pump.mark)}
        <Phone src="s1-today.png" width={w} />
      </div>
    </AbsoluteFill>
  );
};

// 11. And more (52–58 s): health, growth and the firsts, one chip per half beat.
const MORE = [
  { k: C.growth, d: MORE_ICON.growth, t: 'WHO growth charts' },
  { k: C.feed, d: MORE_ICON.medicine, t: 'Medicines & vitamins' },
  { k: C.feed, d: MORE_ICON.temperature, t: 'Temperature' },
  { k: C.pump, d: MORE_ICON.vaccine, t: 'Vaccinations' },
  { k: C.pump, d: MORE_ICON.visit, t: 'Doctor visits' },
  { k: C.growth, d: MORE_ICON.health, t: 'PDF for the paediatrician' },
  { k: C.pump, d: MORE_ICON.bath, t: 'Bath & reminders' },
  { k: C.diaper, d: MORE_ICON.tummy, t: 'Tummy time' },
  { k: C.diaper, d: MORE_ICON.solids, t: 'Solid food' },
  { k: C.sleep, d: MORE_ICON.milestone, t: 'Milestones' },
  { k: C.growth, d: MORE_ICON.mic, t: 'Log by voice' },
  { k: C.sleep, d: ICON.sparkle, t: 'Baby Advisor' },
];

const More: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const exit = interpolate(frame, [170, 180], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Side kicker="And more" text="Health, growth and the firsts." sub="All in the same log, all on the same phones." />
      {MORE.map((m, i) => {
        const s = spring({ frame: frame - 12 - i * 7.5, fps, config: { damping: 11, stiffness: 180, mass: 0.6 } });
        const col = i % 2;
        const row = i >> 1;
        return (
          <div key={m.t} style={{ position: 'absolute', left: 990 + col * 430, top: 110 + row * 148, transform: `scale(${s})`, opacity: Math.min(1, s * 2) }}>
            <Glass style={{ width: 400, height: 120, borderRadius: 34, display: 'flex', alignItems: 'center', gap: 20, padding: '0 22px', boxSizing: 'border-box' }}>
              <div style={{ width: 76, height: 76, borderRadius: 24, background: m.k.tint, display: 'grid', placeItems: 'center', flexShrink: 0 }}>
                <Icon d={m.d} color={m.k.ink} size={42} />
              </div>
              <div style={{ fontSize: 30, fontWeight: 600, color: C.ink, lineHeight: 1.15 }}>{m.t}</div>
            </Glass>
          </div>
        );
      })}
    </AbsoluteFill>
  );
};

// 12. Yours (58–62 s): three plain promises on the beat, and the seven languages.
const LINES = ['Works offline.', 'No ads.', 'No trackers.'];
const LANGS = ['English', 'Deutsch', 'Français', 'Español', 'Italiano', 'Polski', 'Nederlands'];

const Yours: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const exit = interpolate(frame, [110, 120], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ alignItems: 'center', opacity: 1 - exit }}>
      <div style={{ position: 'absolute', top: 250, display: 'flex', gap: 60 }}>
        {LINES.map((l, i) => {
          const s = spring({ frame: frame - BEAT * (i + 1), fps, config: { damping: 10, stiffness: 180, mass: 0.6 } });
          return (
            <div key={l} style={{ fontSize: 104, fontWeight: 600, letterSpacing: -3.5, color: i === 0 ? C.accent : C.ink, opacity: Math.min(1, s * 2), transform: `translateY(${(1 - s) * 80}px) scale(${0.7 + 0.3 * s})` }}>
              {l}
            </div>
          );
        })}
      </div>
      <div style={{ position: 'absolute', top: 470, fontSize: 40, color: C.ink2, opacity: useSpringAt(60, 14) }}>Your baby’s log stays on your phone.</div>
      <div style={{ position: 'absolute', top: 640, display: 'flex', gap: 18 }}>
        {LANGS.map((l, i) => {
          const s = spring({ frame: frame - 66 - i * 3, fps, config: { damping: 12, stiffness: 180 } });
          return (
            <div key={l} style={{ fontSize: 32, fontWeight: 600, color: C.accent, background: C.accentSoft, borderRadius: 40, padding: '14px 28px', transform: `scale(${s})` }}>
              {l}
            </div>
          );
        })}
      </div>
    </AbsoluteFill>
  );
};

// 13. End card (62–68 s).
export const End: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const pop = useSpringAt(0, 9, 170);
  const word = useSpringAt(6, 13);
  const sub = useSpringAt(36, 14);
  const badge = useSpringAt(48, 12);
  return (
    <AbsoluteFill>
      <div style={{ position: 'absolute', left: 400, top: 300, transform: `scale(${pop}) rotate(${(1 - pop) * -20}deg)` }}>
        <Mochi size={420} />
      </div>
      <div style={{ position: 'absolute', left: 920, top: 300, opacity: word, transform: `translateY(${(1 - word) * 60}px)` }}>
        <Wordmark
          height={210}
          letter={(i) => spring({ frame: frame - 6 - i * 2, fps, config: { damping: 12, stiffness: 200 } })}
          dot={spring({ frame: frame - 24, fps, config: { damping: 8, stiffness: 190 } })}
        />
      </div>
      <div style={{ position: 'absolute', left: 930, top: 540, fontSize: 52, fontWeight: 500, color: C.ink2, opacity: sub, transform: `translateY(${(1 - sub) * 40}px)` }}>
        Baby Tracker &amp; Feeding
      </div>
      <div style={{ position: 'absolute', left: 930, top: 640, display: 'flex', gap: 20, opacity: sub }}>
        {[C.feed, C.sleep, C.diaper, C.pump, C.growth].map((k, i) => (
          <div key={i} style={{ width: 76, height: 76, borderRadius: 24, background: k.tint, display: 'grid', placeItems: 'center' }}>
            <Icon d={[ICON.feed, ICON.sleep, ICON.diaper, ICON.pump, ICON.growth][i]} color={k.ink} size={44} />
          </div>
        ))}
      </div>
      {/* A plain line, not a home-made badge: Google's own artwork goes here if it is ever added. */}
      <div style={{ position: 'absolute', left: 930, top: 770, opacity: badge, transform: `translateY(${(1 - badge) * 30}px)` }}>
        <div style={{ fontSize: 52, fontWeight: 600, color: C.accent }}>On Google Play</div>
        <div style={{ marginTop: 8, fontSize: 32, color: C.ink2 }}>lechdigital.nl/mochi</div>
      </div>
    </AbsoluteFill>
  );
};

/** The cuts, in seconds: 4 words, 4 question, 6 timer, 4 quick log, 6 log, 6 pattern, 6 next up, 4 trends, 6 night,
 * 6 together, 6 more, 4 yours, 6 end card = 68. */
const SCENES: [React.FC, number][] = [
  [Words, 4],
  [Question, 4],
  [Timer, 6],
  [QuickLog, 4],
  [Day, 6],
  [Pattern, 6],
  [NextUp, 6],
  [Trends, 4],
  [Night, 6],
  [Together, 6],
  [More, 6],
  [Yours, 4],
  [End, 6],
];
export const YOUTUBE_FRAMES = SCENES.reduce((n, [, s]) => n + s * 30, 0);

export const YouTube: React.FC = () => {
  const frame = useCurrentFrame();
  // The dark wash: in for the night scene (40–46 s), out again for the family.
  const night = interpolate(frame, [1194, 1212, 1374, 1390], [0, 1, 1, 0], { ...clamp, easing: Easing.inOut(Easing.cubic) });
  let at = 0;
  return (
    <AbsoluteFill style={{ fontFamily: 'Figtree, sans-serif', overflow: 'hidden' }}>
      <Fonts />
      <Audio src={staticFile('music-yt.wav')} />
      <Wash night={night} />
      {SCENES.map(([Scene, s], i) => {
        const from = at;
        at += s * 30;
        return (
          <Sequence key={i} from={from} durationInFrames={s * 30}>
            <Scene />
          </Sequence>
        );
      })}
    </AbsoluteFill>
  );
};
