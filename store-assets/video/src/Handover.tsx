import React from 'react';
import { AbsoluteFill, Audio, interpolate, OffthreadVideo, Sequence, staticFile, useCurrentFrame } from 'remotion';
import { C, Fonts, Mochi, Phone, Wash, Wordmark } from './parts';
import { End, Side, useSpringAt } from './YouTube';

/** "The handover" (store-assets/youtube/*-mochi-handover): seven Gemini (Veo) shots of Tom and Anna, each followed by a
 * full-screen Mochi card with the real app, then the end card, 73.1 s. The music is the owner's Gemini track
 * (soundtrack.mp3 in that folder, ~124 BPM, a beat every 0.484 s, bars from 17.488 s). prepare.sh repeats two bars of
 * its day groove (40.72–44.59 s) so the film has room: every card lands on a beat and lasts six beats, the day shot
 * keeps its yoga, and the whisper "Mochi" (71.77 s in the longer track) comes 2.7 s into the end card, once
 * "On Google Play" is up (owner: the whisper was too early).
 * Until the clips are in public/handover/1.mp4 … 7.mp4, pass { clips: false } for placeholders. */
export type HandoverProps = { clips: boolean };

type Card =
  | { kind: 'brand'; dark?: boolean }
  | { kind: 'screen'; src: string; kicker: string; text: string; dark?: boolean; family?: boolean };

// from: where in the 10 s Gemini clip the part we use starts (picked from the takes, 2026-11-06).
const PAIRS: { shot: string; from: number; card: Card }[] = [
  { shot: 'Night, the nursery', from: 2.0, card: { kind: 'brand', dark: true } },
  { shot: 'One hand, in the dark', from: 2.0, card: { kind: 'screen', src: 's7-night.png', kicker: 'Night mode', text: 'Logged with one hand.', dark: true } },
  { shot: 'Morning', from: 1.5, card: { kind: 'screen', src: 's1-today.png', kicker: 'Today', text: 'The whole night, at a glance.' } },
  { shot: 'She takes over', from: 2.0, card: { kind: 'screen', src: 's8-timer.png', kicker: 'Breastfeeding timer', text: 'Right side. One tap.' } },
  { shot: 'The day goes by', from: 1.0, card: { kind: 'screen', src: 's2-log.png', kicker: 'Baby log', text: 'Every feed, sleep and diaper.' } },
  { shot: 'Grandma checks in', from: 0.5, card: { kind: 'screen', src: 's2-log.png', kicker: 'Shared baby log', text: 'Grandparents can follow along.', family: true } },
  { shot: 'Evening, together', from: 1.0, card: { kind: 'screen', src: 's4-stats.png', kicker: 'Sleep pattern', text: 'It learns her rhythm.' } },
];

// [shot start, card start] in frames, from the beat grid above; the end card starts at END.
const CUTS: [number, number][] = [
  [0, 205],
  [292, 496],
  [583, 786],
  [873, 1076],
  [1164, 1425], // the day: four beats longer, so the yoga at its end stays in
  [1512, 1701],
  [1788, 1991],
];
const END = 2071;

/** The Mochi logo in the corner over every live-action shot, so a frame anywhere in the film is branded. */
const Bug: React.FC = () => (
  <div style={{ position: 'absolute', right: 48, bottom: 40, display: 'flex', alignItems: 'center', gap: 14, opacity: 0.92, filter: 'drop-shadow(0 4px 14px rgba(0,0,0,0.35))' }}>
    <Mochi size={64} />
  </div>
);

const Shot: React.FC<{ i: number; title: string; from: number; length: number; clips: boolean }> = ({ i, title, from, length, clips }) => {
  const frame = useCurrentFrame();
  const fade = interpolate(frame, [0, 6], [0, 1], { extrapolateRight: 'clamp' });
  return (
    <AbsoluteFill style={{ background: '#111', opacity: fade }}>
      {clips ? (
        <OffthreadVideo
          src={staticFile(`handover/${i + 1}.mp4`)}
          startFrom={Math.round(from * 30)}
          volume={(f) => 0.3 * interpolate(f, [0, 8, length - 8, length], [0, 1, 1, 0], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' })}
          style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
      ) : (
        <AbsoluteFill style={{ justifyContent: 'center', alignItems: 'center', background: 'linear-gradient(135deg, #2A2E26, #3A3328)' }}>
          <div style={{ fontSize: 30, letterSpacing: 4, color: '#97A486', fontWeight: 700 }}>GEMINI SHOT {i + 1}</div>
          <div style={{ fontSize: 72, color: '#E8E4D8', fontWeight: 600, marginTop: 12 }}>{title}</div>
        </AbsoluteFill>
      )}
      <Bug />
    </AbsoluteFill>
  );
};

const BrandCard: React.FC<{ dark?: boolean }> = ({ dark }) => {
  const pop = useSpringAt(0, 9, 170);
  const word = useSpringAt(8, 13);
  return (
    <AbsoluteFill>
      <Wash night={dark ? 1 : 0} />
      <AbsoluteFill style={{ justifyContent: 'center', alignItems: 'center', flexDirection: 'row', gap: 70 }}>
        <div style={{ transform: `scale(${pop})` }}>
          <Mochi size={360} sleep={dark ? 1 : 0} />
        </div>
        <div style={{ opacity: word, transform: `translateY(${(1 - word) * 50}px)` }}>
          <Wordmark height={190} ink={dark ? C.night.ink : C.ink} accent={dark ? C.night.accent : C.accent} />
        </div>
      </AbsoluteFill>
    </AbsoluteFill>
  );
};

const ScreenCard: React.FC<{ card: Extract<Card, { kind: 'screen' }> }> = ({ card }) => {
  const rise = useSpringAt(0, 15, 140);
  const chips = useSpringAt(24, 12);
  return (
    <AbsoluteFill>
      <Wash night={card.dark ? 1 : 0} />
      <Side kicker={card.kicker} text={card.text} from={4} dark={card.dark} />
      {card.family && (
        <div style={{ position: 'absolute', left: 130, top: 700, display: 'flex', gap: 16, opacity: chips, transform: `translateY(${(1 - chips) * 30}px)` }}>
          {[
            ['Anna', C.feed.mark],
            ['Tom', C.pump.mark],
            ['Grandma', C.sleep.mark],
          ].map(([n, c]) => (
            <div key={n} style={{ display: 'flex', alignItems: 'center', gap: 12, background: 'rgba(250,250,245,0.85)', borderRadius: 40, padding: '10px 22px 10px 10px' }}>
              <div style={{ width: 48, height: 48, borderRadius: 24, background: c, color: '#fff', display: 'grid', placeItems: 'center', fontSize: 24, fontWeight: 700 }}>{n[0]}</div>
              <div style={{ fontSize: 30, fontWeight: 600, color: C.ink }}>{n}</div>
            </div>
          ))}
        </div>
      )}
      <div style={{ position: 'absolute', left: 1240, top: 70 + (1 - rise) * 1100 }}>
        <Phone src={card.src} width={420} />
      </div>
      <div style={{ position: 'absolute', right: 48, bottom: 40, opacity: 0.9 }}>
        <Mochi size={64} sleep={card.dark ? 1 : 0} />
      </div>
    </AbsoluteFill>
  );
};

export const HANDOVER_FRAMES = END + 180;

export const Handover: React.FC<HandoverProps> = ({ clips }) => (
  <AbsoluteFill style={{ fontFamily: 'Figtree, sans-serif', overflow: 'hidden', background: '#111' }}>
    <Fonts />
    <Audio src={staticFile('soundtrack-handover.wav')} />
    {PAIRS.map((p, i) => {
      const [shot, card] = CUTS[i];
      const next = i + 1 < CUTS.length ? CUTS[i + 1][0] : END;
      return (
        <React.Fragment key={i}>
          <Sequence from={shot} durationInFrames={card - shot}>
            <Shot i={i} title={p.shot} from={p.from} length={card - shot} clips={clips} />
          </Sequence>
          <Sequence from={card} durationInFrames={next - card}>
            {p.card.kind === 'brand' ? <BrandCard dark={p.card.dark} /> : <ScreenCard card={p.card} />}
          </Sequence>
        </React.Fragment>
      );
    })}
    <Sequence from={END} durationInFrames={180}>
      <Wash />
      <End />
    </Sequence>
  </AbsoluteFill>
);
