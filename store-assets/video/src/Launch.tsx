import React from 'react';
import { AbsoluteFill, Audio, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig, Easing } from 'remotion';
import { C, Caption, Fonts, Glass, Icon, ICON, Mochi, Phone, useSpring, Wash } from './parts';

/** 120 BPM at 30 fps: a beat is 15 frames, a bar 60. Every cut lands on one. */
export const BEAT = 15;
const clamp = { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' } as const;

// 1. Hook: the five things it logs, one per beat, then they collapse into the icon's square.
const HOOK = [
  { word: 'Feeds.', k: C.feed, icon: ICON.feed },
  { word: 'Sleep.', k: C.sleep, icon: ICON.sleep },
  { word: 'Diapers.', k: C.diaper, icon: ICON.diaper },
  { word: 'Pumping.', k: C.pump, icon: ICON.pump },
  { word: 'Growth.', k: C.growth, icon: ICON.growth },
];

const Hook: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const collapse = interpolate(frame, [88, 108], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const square = spring({ frame: frame - 100, fps, config: { damping: 11, stiffness: 160 } });
  return (
    <AbsoluteFill style={{ justifyContent: 'center', alignItems: 'center' }}>
      {HOOK.map((h, i) => {
        const s = spring({ frame: frame - i * BEAT, fps, config: { damping: 11, mass: 0.6, stiffness: 170 } });
        const y = (i - 2) * 200;
        return (
          <div
            key={h.word}
            style={{
              position: 'absolute',
              top: 960 - 90 + y * (1 - collapse),
              left: 150,
              display: 'flex',
              alignItems: 'center',
              gap: 44,
              opacity: s * (1 - collapse),
              transform: `translateX(${(1 - s) * -160}px) scale(${(0.6 + 0.4 * s) * (1 - collapse * 0.7)})`,
              transformOrigin: '50% 50%',
            }}
          >
            <div style={{ width: 172, height: 172, borderRadius: 48, background: h.k.tint, display: 'grid', placeItems: 'center' }}>
              <Icon d={h.icon} color={h.k.ink} size={96} stroke={2} />
            </div>
            <div style={{ fontSize: 150, fontWeight: 600, letterSpacing: -5, color: h.k.ink }}>{h.word}</div>
          </div>
        );
      })}
      <div
        style={{
          position: 'absolute',
          width: 460,
          height: 460,
          borderRadius: 230,
          background: C.accent,
          transform: `scale(${square})`,
          boxShadow: '0 40px 100px rgba(63,107,74,0.35)',
        }}
      />
    </AbsoluteFill>
  );
};

// 2. The name: the square is Mochi; it lands, flops its ears, and the wordmark writes itself.
const Name: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const pop = spring({ frame, fps, config: { damping: 8, stiffness: 180, mass: 0.7 } });
  const squash = Math.max(0, Math.sin((frame - 4) / 3)) * Math.exp(-frame / 10) * 2;
  const sleep = interpolate(frame, [42, 50, 64, 70], [0, 1, 1, 0], clamp);
  const letters = 'mochi'.split('');
  const tag = spring({ frame: frame - 60, fps, config: { damping: 14 } });
  const exit = interpolate(frame, [112, 120], [0, 1], clamp);
  return (
    <AbsoluteFill style={{ alignItems: 'center', opacity: 1 - exit, transform: `translateY(${-exit * 200}px)` }}>
      <div style={{ position: 'absolute', top: 960 - 230 - 160, transform: `scale(${0.75 + 0.25 * pop})` }}>
        <Mochi size={460} sleep={sleep} squash={Math.min(1, squash)} />
      </div>
      <div style={{ position: 'absolute', top: 1110, display: 'flex', fontSize: 200, fontWeight: 700, letterSpacing: -6 }}>
        {letters.map((l, i) => {
          const s = spring({ frame: frame - 18 - i * 3, fps, config: { damping: 10, stiffness: 200 } });
          return (
            <span key={i} style={{ color: l === 'i' ? C.accent : C.ink, display: 'inline-block', opacity: s, transform: `translateY(${(1 - s) * 80}px)` }}>
              {l}
            </span>
          );
        })}
      </div>
      <div
        style={{
          position: 'absolute',
          top: 1400,
          fontSize: 58,
          fontWeight: 500,
          color: C.ink2,
          letterSpacing: -1,
          opacity: tag,
          transform: `translateY(${(1 - tag) * 40}px)`,
        }}
      >
        Baby tracker that learns your baby.
      </div>
    </AbsoluteFill>
  );
};

// 3. One tap: Today rises, a tap on Feed, the timer starts.
const OneTap: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const rise = spring({ frame, fps, config: { damping: 15, stiffness: 120 } });
  const exit = interpolate(frame, [112, 122], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const tap = interpolate(frame, [40, 58], [0, 1], clamp);
  const chip = spring({ frame: frame - 50, fps, config: { damping: 11 } });
  const seconds = 88 + Math.max(0, Math.floor((frame - 50) / 30));
  const width = 620;
  const screenH = (width * 2424) / 1080;
  return (
    <AbsoluteFill>
      <Caption kicker="Breastfeeding timer" text="One tap starts a feed, a nap or pumping." from={4} />
      <div style={{ position: 'absolute', left: (1080 - width - 36) / 2, top: 600 + (1 - rise) * 1400, transform: `translateX(${-exit * 1300}px) rotate(${-exit * 8}deg)` }}>
        <Phone src="s1-today.png" width={width}>
          <div
            style={{
              position: 'absolute',
              left: (175 / 1080) * width - 90,
              top: (1552 / 2424) * screenH - 90,
              width: 180,
              height: 180,
              borderRadius: 90,
              background: 'rgba(63,107,74,0.35)',
              transform: `scale(${tap * 2.2})`,
              opacity: tap > 0 ? 1 - tap : 0,
            }}
          />
        </Phone>
      </div>
      <div style={{ position: 'absolute', right: 60, top: 1240, transform: `scale(${chip}) translateX(${-exit * 1300}px)`, transformOrigin: '100% 50%' }}>
        <Glass style={{ padding: '30px 40px', display: 'flex', alignItems: 'center', gap: 26 }}>
          <div style={{ width: 22, height: 22, borderRadius: 11, background: C.feed.mark }} />
          <div>
            <div style={{ fontSize: 28, fontWeight: 700, letterSpacing: 3, color: C.ink2 }}>FEEDING · RIGHT</div>
            <div style={{ fontSize: 84, fontWeight: 300, color: C.ink, fontVariantNumeric: 'tabular-nums' }}>
              {Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}
            </div>
          </div>
        </Glass>
      </div>
    </AbsoluteFill>
  );
};

// 4. The whole day: the Log slides in and the camera eases into the day's band.
const Day: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const enter = spring({ frame, fps, config: { damping: 16, stiffness: 120 } });
  const zoom = interpolate(frame, [20, 110], [1, 1.18], { ...clamp, easing: Easing.inOut(Easing.cubic) });
  const exit = interpolate(frame, [110, 120], [0, 1], clamp);
  const width = 620;
  return (
    <AbsoluteFill>
      <Caption kicker="Baby log" text="Every feed, nap and diaper on one timeline." from={2} />
      <div
        style={{
          position: 'absolute',
          left: (1080 - width - 36) / 2,
          top: 600,
          transform: `translateX(${(1 - enter) * 1300}px) scale(${zoom}) translateY(${-exit * 1500}px)`,
          transformOrigin: '50% 0%',
          opacity: 1 - exit,
        }}
      >
        <Phone src="s2-log.png" width={width} />
      </div>
    </AbsoluteFill>
  );
};

// 5a. It learns: a fortnight of days side by side, noon to noon, until the nights line up.
const DAYS = Array.from({ length: 14 }, (_, d) => {
  const r = (n: number) => Math.abs(Math.sin(d * 12.9898 + n * 78.233) * 43758.5453) % 1;
  const nightStart = 21.6 - d * 0.075 + (r(1) - 0.5) * 0.5; // hours, drifting earlier as she settles
  const firstEnd = 1.2 + d * 0.06 + (r(2) - 0.5) * 0.6 + 24;
  const naps = [
    [13.2 + r(3) * 0.4, 14.4 + r(4) * 0.5],
    [16.1 + r(5) * 0.4, 16.9 + r(6) * 0.4],
    [8.7 + 24 + r(7) * 0.3, 9.9 + 24 + r(8) * 0.4],
  ];
  return { nightStart, firstEnd, secondStart: firstEnd + 0.35, secondEnd: 30.5 + r(9) * 0.4, naps, feeds: [12.5, 15.4, 18.2, 20.9, firstEnd + 0.05, 30.6, 33.6].map((f, i) => f + (r(10 + i) - 0.5) * 0.4) };
});

// When the longest sleep usually starts: the median of the nights drawn, and its label.
const USUAL = [...DAYS.map((d) => d.nightStart)].sort((a, b) => a - b)[DAYS.length >> 1];
const USUAL_LABEL = `${Math.floor(USUAL) - 12}:${String(Math.round((USUAL % 1) * 60)).padStart(2, '0')} PM`;

const Pattern: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const card = spring({ frame, fps, config: { damping: 15 } });
  const exit = interpolate(frame, [112, 122], [0, 1], { ...clamp, easing: Easing.in(Easing.cubic) });
  const chartTop = 110;
  const chartH = 820;
  const left = 150;
  const width = 940 - left - 40;
  const slot = width / DAYS.length;
  const y = (h: number) => chartTop + ((h - 12) / 24) * chartH;
  const usual = USUAL;
  const line = spring({ frame: frame - 74, fps, config: { damping: 13 } });
  return (
    <AbsoluteFill>
      <Caption kicker="Sleep pattern" text="Mochi learns your baby’s rhythm." from={2} />
      <div style={{ position: 'absolute', left: 70, top: 560, transform: `translateY(${(1 - card) * 900 - exit * 1600}px)` }}>
        <Glass style={{ width: 940, height: 1060, position: 'relative', overflow: 'hidden' }}>
          <div style={{ position: 'absolute', left: 40, top: 34, fontSize: 34, fontWeight: 600, color: C.ink }}>Last 14 days · noon to noon</div>
          {['12 PM', '6 PM', '12 AM', '6 AM', '12 PM'].map((t, i) => (
            <React.Fragment key={i}>
              <div style={{ position: 'absolute', left: 20, width: 110, textAlign: 'right', top: y(12 + i * 6) - 16, fontSize: 24, color: C.ink2 }}>{t}</div>
              <div style={{ position: 'absolute', left, right: 40, top: y(12 + i * 6), height: 2, background: 'rgba(216,218,203,0.7)' }} />
            </React.Fragment>
          ))}
          {DAYS.map((d, i) => {
            const grow = spring({ frame: frame - 14 - i * 3, fps, config: { damping: 14, stiffness: 160 } });
            const x = left + slot * i + slot * 0.18;
            const w = slot * 0.64;
            const bar = (from: number, to: number, solid: boolean, key: string) => (
              <div
                key={key}
                style={{
                  position: 'absolute',
                  left: x,
                  top: y(from),
                  width: w,
                  height: (y(to) - y(from)) * grow,
                  borderRadius: 10,
                  background: C.sleep.mark,
                  opacity: solid ? 1 : 0.45,
                }}
              />
            );
            return (
              <React.Fragment key={i}>
                <div style={{ position: 'absolute', left: x, top: chartTop, width: w, height: chartH * grow, borderRadius: 10, background: 'rgba(226,220,239,0.45)' }} />
                {d.naps.map((n, j) => bar(n[0], n[1], false, `n${j}`))}
                {bar(d.nightStart, d.firstEnd, true, 'night')}
                {bar(d.secondStart, d.secondEnd, false, 'second')}
                {d.feeds.map((f, j) => (
                  <div key={`f${j}`} style={{ position: 'absolute', left: x - 6, top: y(f) - 2, width: w + 12, height: 5, borderRadius: 3, background: C.feed.mark, opacity: grow }} />
                ))}
              </React.Fragment>
            );
          })}
          <div
            style={{
              position: 'absolute',
              left,
              right: 40,
              top: y(usual) - 2,
              height: 0,
              borderTop: `5px dashed ${C.sleep.ink}`,
              opacity: line,
              transform: `scaleX(${line})`,
              transformOrigin: '0 50%',
            }}
          />
          <div
            style={{
              position: 'absolute',
              right: 50,
              top: y(usual) - 96,
              transform: `scale(${line})`,
              transformOrigin: '100% 100%',
              background: C.sleep.ink,
              color: '#fff',
              borderRadius: 24,
              padding: '14px 26px',
              fontSize: 34,
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

// 5b. Then it tells you what comes next, one card per beat.
const CARDS = [
  { k: C.sleep, icon: ICON.sleep, label: 'Next nap', value: 'likely 1:10 – 1:35 PM' },
  { k: C.sleep, icon: ICON.sleep, label: 'Back to sleep', value: 'likely by 3:40 AM' },
  { k: C.feed, icon: ICON.feed, label: 'Next feed', value: 'around 7:10 AM' },
  { k: C.growth, icon: ICON.sparkle, label: 'This week', value: 'Moving from 4 naps to 3' },
];

const Insights: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const exit = interpolate(frame, [110, 120], [0, 1], clamp);
  const peek = spring({ frame: frame - 70, fps, config: { damping: 10 } });
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Caption kicker="Smart insights" text="Then it tells you what comes next." from={0} />
      {CARDS.map((c, i) => {
        const s = spring({ frame: frame - i * 30, fps, config: { damping: 10, stiffness: 170, mass: 0.6 } });
        return (
          <div key={i} style={{ position: 'absolute', left: 70, right: 70, top: 600 + i * 250, transform: `translateX(${(1 - s) * (i % 2 ? 1100 : -1100)}px) rotate(${(1 - s) * (i % 2 ? 6 : -6)}deg)` }}>
            <Glass style={{ padding: '34px 40px', display: 'flex', alignItems: 'center', gap: 36 }}>
              <div style={{ width: 130, height: 130, borderRadius: 38, background: c.k.tint, display: 'grid', placeItems: 'center', flexShrink: 0 }}>
                <Icon d={c.icon} color={c.k.ink} size={72} />
              </div>
              <div>
                <div style={{ fontSize: 30, fontWeight: 700, letterSpacing: 3, color: C.ink2, textTransform: 'uppercase' }}>{c.label}</div>
                <div style={{ fontSize: 60, fontWeight: 600, letterSpacing: -1.5, color: c.k.ink }}>{c.value}</div>
              </div>
            </Glass>
          </div>
        );
      })}
      <div style={{ position: 'absolute', right: 60, bottom: 90, transform: `translateY(${(1 - peek) * 300}px) rotate(${Math.sin(frame / 5) * 4}deg)` }}>
        <Mochi size={200} sleep={interpolate(frame, [88, 94, 102, 108], [0, 1, 1, 0], clamp)} />
      </div>
    </AbsoluteFill>
  );
};

// 6. Night colours: the dark wash wipes up from below and the night screen rises with it.
const Night: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const rise = spring({ frame: frame - 6, fps, config: { damping: 15, stiffness: 120 } });
  const exit = interpolate(frame, [82, 90], [0, 1], clamp);
  const width = 580;
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Caption kicker="Night colours" text="Big buttons for one hand, in the dark." from={4} dark />
      <div style={{ position: 'absolute', left: (1080 - width - 32) / 2, top: 620 + (1 - rise) * 1400 }}>
        <Phone src="s7-night.png" width={width} />
      </div>
      <div style={{ position: 'absolute', left: 780, top: 1560, transform: `scale(${rise}) rotate(${Math.sin(frame / 8) * 3}deg)` }}>
        <Mochi size={210} sleep={1} />
      </div>
    </AbsoluteFill>
  );
};

// 7. Together: two phones, two parents, one log; and in seven languages.
const LANGS = ['English', 'Polski', 'Deutsch', 'Français', 'Español', 'Italiano', 'Nederlands'];

const Together: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const a = spring({ frame: frame - 4, fps, config: { damping: 13 } });
  const b = spring({ frame: frame - 12, fps, config: { damping: 13 } });
  const exit = interpolate(frame, [82, 90], [0, 1], clamp);
  const width = 430;
  const parent = (name: string, s: number, color: string) => (
    <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 22, opacity: s, transform: `scale(${s})` }}>
      <div style={{ width: 70, height: 70, borderRadius: 35, background: color, color: '#fff', display: 'grid', placeItems: 'center', fontSize: 36, fontWeight: 700 }}>{name[0]}</div>
      <div style={{ fontSize: 42, fontWeight: 600, color: C.ink }}>{name}</div>
    </div>
  );
  return (
    <AbsoluteFill style={{ opacity: 1 - exit }}>
      <Caption kicker="Shared baby log" text="One log for the whole family." from={2} />
      <div style={{ position: 'absolute', left: 70, top: 560, transform: `translateX(${(1 - a) * -900}px) rotate(${-5 * a}deg)` }}>
        {parent('Anna', a, C.feed.mark)}
        <Phone src="s2-log.png" width={width} />
      </div>
      <div style={{ position: 'absolute', right: 70, top: 640, transform: `translateX(${(1 - b) * 900}px) rotate(${5 * b}deg)` }}>
        {parent('Tom', b, C.pump.mark)}
        <Phone src="s1-today.png" width={width} />
      </div>
      <div style={{ position: 'absolute', left: 0, right: 0, bottom: 70, overflow: 'hidden' }}>
        <div style={{ display: 'flex', gap: 22, transform: `translateX(${60 - frame * 9}px)`, whiteSpace: 'nowrap' }}>
          {[...LANGS, ...LANGS].map((l, i) => (
            <Glass key={i} style={{ padding: '18px 34px', borderRadius: 999, fontSize: 40, fontWeight: 600, color: C.accent }}>
              {l}
            </Glass>
          ))}
        </div>
      </div>
    </AbsoluteFill>
  );
};

// 8. End card.
const End: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const pop = spring({ frame, fps, config: { damping: 9, stiffness: 170 } });
  const word = spring({ frame: frame - 6, fps, config: { damping: 13 } });
  const sub = spring({ frame: frame - 12, fps, config: { damping: 14 } });
  const badge = spring({ frame: frame - 18, fps, config: { damping: 12 } });
  return (
    <AbsoluteFill style={{ alignItems: 'center' }}>
      <div style={{ position: 'absolute', top: 470, transform: `scale(${pop}) rotate(${(1 - pop) * -20}deg)` }}>
        <Mochi size={360} sleep={0} />
      </div>
      <div style={{ position: 'absolute', top: 870, fontSize: 190, fontWeight: 700, letterSpacing: -6, color: C.ink, opacity: word, transform: `translateY(${(1 - word) * 60}px)` }}>
        moch<span style={{ color: C.accent }}>i</span>
      </div>
      <div style={{ position: 'absolute', top: 1120, fontSize: 60, fontWeight: 500, color: C.ink2, opacity: sub, transform: `translateY(${(1 - sub) * 40}px)` }}>
        Baby Tracker &amp; Feeding
      </div>
      <div style={{ position: 'absolute', top: 1280, display: 'flex', gap: 26, opacity: sub }}>
        {[C.feed, C.sleep, C.diaper, C.pump, C.growth].map((k, i) => (
          <div key={i} style={{ width: 92, height: 92, borderRadius: 28, background: k.tint, display: 'grid', placeItems: 'center' }}>
            <Icon d={[ICON.feed, ICON.sleep, ICON.diaper, ICON.pump, ICON.growth][i]} color={k.ink} size={52} />
          </div>
        ))}
      </div>
      <div
        style={{
          position: 'absolute',
          top: 1490,
          background: C.ink,
          color: '#FAFAF5',
          borderRadius: 999,
          padding: '34px 70px',
          fontSize: 50,
          fontWeight: 600,
          transform: `scale(${badge})`,
        }}
      >
        Get it on Google Play
      </div>
    </AbsoluteFill>
  );
};

export const Launch: React.FC = () => {
  const frame = useCurrentFrame();
  // The dark wash: in for the night scene, out again for the family.
  const night = interpolate(frame, [714, 730, 804, 818], [0, 1, 1, 0], { ...clamp, easing: Easing.inOut(Easing.cubic) });
  return (
    <AbsoluteFill style={{ fontFamily: 'Figtree, sans-serif', overflow: 'hidden' }}>
      <Fonts />
      <Audio src={staticFile('music.wav')} />
      <Wash night={night} />
      <Sequence from={0} durationInFrames={120}>
        <Hook />
      </Sequence>
      <Sequence from={120} durationInFrames={120}>
        <Name />
      </Sequence>
      <Sequence from={240} durationInFrames={122}>
        <OneTap />
      </Sequence>
      <Sequence from={360} durationInFrames={120}>
        <Day />
      </Sequence>
      <Sequence from={480} durationInFrames={122}>
        <Pattern />
      </Sequence>
      <Sequence from={600} durationInFrames={120}>
        <Insights />
      </Sequence>
      <Sequence from={720} durationInFrames={90}>
        <Night />
      </Sequence>
      <Sequence from={810} durationInFrames={90}>
        <Together />
      </Sequence>
      <Sequence from={900} durationInFrames={60}>
        <End />
      </Sequence>
    </AbsoluteFill>
  );
};
