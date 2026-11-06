# The handover — music prompt

The film is 76 s and its cuts sit on a 120 BPM grid: a 7 s live-action shot, then a 3 s Mochi card, seven times, then a
6 s end card. Generate the track, save it as `music.mp3` (or `.wav`) next to this file, and the edit lines the cards
up with its beats, trims it to 76 s and fades the end. It replaces the synthesised placeholder.

Gemini first (one prompt below); Suno as a fallback.

## Gemini (one prompt, copy all of it)

```
Create an instrumental soundtrack for a 76-second commercial for a baby tracker app called Mochi. Warm cinematic lullaby pop at 120 BPM with a half-time feel: felt piano, music box, soft fingerpicked acoustic guitar, marimba, light brushed percussion, a warm analog pad and gentle strings. Tender, hopeful and intimate, like a premium phone commercial. No singing.

Follow this timeline exactly:
0:00–0:20 Night. Very quiet and sparse: felt piano and a music box only, no drums.
0:20–0:30 Morning light. A soft fingerpicked acoustic guitar comes in, still no drums.
0:30–0:50 Daytime. A gentle groove: brushed percussion and marimba join, warm and light.
0:50–1:10 Evening. The warmest part: soft strings swell, fuller and content.
1:10–1:16 Ending. Everything drops to one soft, ringing piano chord. At 1:11, a single word is whispered close to the microphone, warm and smiling: "Mochi". Then the chord rings out to silence.

The whispered "Mochi" is the only voice in the whole track.
```

If Gemini makes shorter tracks than 76 s, ask for the ending on its own with the same prompt and "only the last 15 seconds", and the edit joins the two.

## Suno instead

### Style (Suno: "Style of music")

```
warm cinematic lullaby pop, instrumental, 120 BPM half-time feel, felt piano, music box, soft fingerpicked acoustic guitar, marimba, light brushed percussion, warm analog pad, gentle strings, hopeful, tender, intimate, premium phone commercial, no vocals except one whispered word at the very end
```

### Lyrics / structure (Suno: "Lyrics")

```
[Intro: night, very quiet, felt piano and music box only, no drums]
[Instrumental]

[Build: morning light, soft fingerpicked acoustic guitar enters, still no drums]
[Instrumental]

[Verse: daytime, gentle groove, brushed percussion and marimba join, warm and light]
[Instrumental]

[Bridge: evening, warm strings swell softly, fuller and content]
[Instrumental]

[Outro: everything drops to one soft piano chord, ringing out]
(whispered, close to the mic, warm and soft) Mochi

[End]
```

## Timeline (what the music should do)

| Time | Picture | Music |
|---|---|---|
| 0:00–0:20 | Night: dad with the baby, logging with one hand | Felt piano and music box, very sparse, no drums |
| 0:20–0:30 | Morning: mum wakes, gets the night at a glance | Light rises: acoustic guitar comes in |
| 0:30–0:50 | She takes over, the day goes by | Gentle groove: brushed percussion, marimba |
| 0:50–1:10 | Grandma checks in, the evening together | Warmest part: soft strings, fuller |
| 1:10–1:16 | End card: Mochi, On Google Play | Drops to one piano chord; a whispered "Mochi" at ~1:11 as the bunny lands on the "i" |

## Settings

- Instrumental: **off** in Suno (otherwise it drops the whisper), but the Style says no vocals except the whisper.
- Length: Suno makes 2–3 minutes; that's fine, the edit uses the first ~70 s and the outro. If the outro is far away,
  generate an extension or ask for "short song, about 80 seconds".
- Make 3–4 versions and keep the one whose first 20 s are the quietest.

## The whisper, if the generator can't do it

Record it yourself (or your partner): phone voice memo in a quiet room, 20 cm from the mouth, a soft, smiling whisper
of "Mochi", a few takes. Save as `whisper.m4a` here; the edit puts it on the bunny's landing on the end card.
