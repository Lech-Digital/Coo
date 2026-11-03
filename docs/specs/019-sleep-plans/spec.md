# Spec 019 — Sleep plans

Status: Accepted (owner's decisions of 2026-11-03 in §11)

Date: 2026-11-03

## 1. Why

Huckleberry's Premium ($119.99/yr) sells on one thing above the nap predictor: a personal sleep plan, written for
the baby, with check-ins. Coo has the parts (the age prior and personal predictor of ADR 0008, the Advisor of ADR 0005
and Spec 015c, reminders of ADR 0007) but no plan. This spec adds one, as a Premium feature, at a quarter of the
price, and done properly: grounded in the baby's own log, safe, explainable, and honest about what it is.

## 2. What a parent gets

1. **A short intake** (2 minutes): the baby's age (known), what she wants to change (one or more of: shorter time to
   fall asleep, fewer night wakings, longer naps, an earlier or later bedtime, a predictable day, dropping a nap),
   when the day should start, how she likes to settle the baby (§5), and what she will not do (for example "no
   crying it out").
2. **The plan**: a day schedule for this baby now (wake-up, each nap window, bedtime) with the reasoning ("her wake
   windows have been 1 h 50 m this week, typical for 4 months is 1 h 15 m – 2 h"), a bedtime routine, a settling
   approach for the chosen style, and what to expect in the first week.
3. **Daily follow-through**: Today shows "Plan: day 3 of 14", with the next step and the next window; the nap
   reminder (ADR 0007) follows the plan's windows; the morning brings a two-line note on last night against the
   goal.
4. **Check-ins**: a first one on day 3, then weekly for as long as the plan runs (it is open-ended, and adapts as the
   baby grows until the parent stops it): what changed in the log (time to fall asleep, night wakings, nap length,
   total sleep), and a suggested adjustment ("move bedtime 15 minutes earlier", or a new age band's schedule) that she
   accepts or not.
5. **A history**: past plans and how they went, in Stats.

## 3. What a plan is not

- Not medical advice, and not for a baby who is unwell, under 2 weeks old, born more than 4 weeks early without the
  corrected age, or with reflux, a feeding problem or poor weight gain that the parent reports. Then the plan is not
  offered and the parent is pointed to her doctor or health visitor (§6).
- Never sleep training under 4 months corrected age: then the plan is only rhythm and routine (§5.1).
- Never advice against safe sleep (on the back, own flat surface, nothing loose in the cot, room-sharing as local
  guidance says). Every plan carries the safe-sleep line.

## 4. How the plan is built

**The schedule is computed, not generated.** A new `Coo.Domain/Insights/SleepPlanner` takes the baby's corrected age,
the last 14 days of sleep (ADR 0008's input), the intake, and `now`, and returns the plan's numbers: target wake-up,
nap count, each window, target bedtime, and the daily adjustment rule. Pure, deterministic, tested with the synthetic
babies of the predictor's backtest, versioned (`plan-v1`). The age bands come from `WakeWindowPrior` (extended with
typical total sleep and night length per band, reviewed against current paediatric sources, versioned).

**The words are written by the Advisor.** The routine, the settling steps for the chosen style, what to expect, and
the check-in notes are written by the model from the computed numbers and a fixed library of approaches (§5), under
ADR 0005's rules (names pseudonymised, no free invention of numbers: the prompt carries the numbers, and the reply
is checked to quote only those). Offline or out of allowance, the plan still works: the numbers and a stored text
for each approach are shown without the personal wording.

**Check-ins measure, then suggest.** The planner computes the measures from the log (the same day boundaries as
Stats) and a bounded adjustment (bedtime ±15 minutes, one window ±15 minutes, or "drop a nap" from the band's age
range); the Advisor words it.

## 5. Approaches

Each is a fixed, reviewed text with its age limits; the model adapts tone, not substance.

### 5.1 Under 4 months: rhythm and routine only
Wake windows, a consistent bedtime routine, day/night cues (light, noise), feeding before sleep, drowsy-but-awake
practice. No sleep training.

### 5.2 From 4 months, chosen by the parent
- **Gentle / fading**: parent present, gradually less help (rocking → patting → sitting by the cot → leaving).
- **Chair method**: parent sits by the cot, moves the chair further every few nights.
- **Check and console** (timed checks, intervals the parent chooses).
- **"No crying"**: only routine, timing and fading; the plan says honestly that progress may be slower.

Extinction ("cry it out") is not offered (owner, 2026-11-03).

## 6. Safety checks

Before a plan, and at each check-in, the planner asks or reads:
- age and prematurity (from the baby profile);
- recent fever or medicine entries in the log (then: "pause the plan while she is unwell");
- the parent's answers to three yes/no questions (feeding worries, weight worries, breathing or snoring); any yes
  → no plan, a kind message and the doctor's report button.
The Advisor's existing guardrails apply to every generated text, with the safe-sleep line always appended.

## 7. Where it lives

| Concern | Where |
|---|---|
| `SleepPlanner`, plan numbers, check-in measures, adjustment rule | `Coo.Domain/Insights` (device and server) |
| The plan as a record | A care-log kind `sleepplan` (ADR 0010): started, intake, numbers, accepted adjustments, ended. It syncs, every caregiver sees it, and no new sync entity is needed |
| Plan texts | `Coo.Api` Advisor endpoint `POST /api/advisor/sleep-plan` (Premium, its own daily allowance) |
| Approach library | Versioned resources, 7 languages, reviewed |
| UI | Stats → "Sleep plan" card; a plan page (intake, plan, check-ins); Today's plan line; reminders |

## 8. Free and Premium

The owner's rule (2026-11-03): **Coo is almost free; Premium covers only what costs money to run (AI calls) and keeping
Coo independent and ad-free.** So:

- **Free**: the whole plan. The computed schedule for this baby, the routine and settling steps (the reviewed stored
  texts of §5), Today's plan line, reminders that follow the plan, every check-in with its measures and the bounded
  adjustment. Nothing of this costs anything to run: it is computed on the phone.
- **Premium**: the AI layer. The plan and check-ins written for this baby by the Advisor (§4), and asking the
  Advisor about the plan (its existing allowance).

Follow-up, not in this spec: by the same rule the personal nap times of ADR 0008, computed on the phone, would be free
too. The owner decides that separately.

## 9. Privacy

ADR 0005: what is sent is the computed numbers, the intake answers and pseudonymised names; never the raw log. Plans
are stored like any entry (EU, the household's). The privacy policy gets one paragraph.

## 10. Testing and the deliverable

- Planner unit tests per age band; property tests (windows within band limits, bedtime within limits, adjustments
  bounded); backtest on the synthetic babies: a plan followed for 14 days moves the measures toward the goal.
- Prompt evals: numbers quoted exactly, approach text not contradicted, safe-sleep line present, refusals for the
  §3/§6 cases.
- **Deliverable you can test:** start a plan for a baby at 2 months and at 6 months (seeded logs) without Premium, see
  the schedule and the routine, the reminders follow it, the day-3 check-in shows a measured change and one bounded
  suggestion; with Premium, the same plan in the Advisor's words.

## 11. Decisions (owner, 2026-11-03)

1. From 4 months: gentle/fading, chair, check-and-console, no-crying. **No extinction.**
2. **Open-ended**, first check-in on day 3, then weekly; it adapts as the baby grows until the parent stops it.
3. Owner and parents start, change and stop a plan; caregivers and viewers see it and follow it.
4. The approach texts ship as **general guidance** from mainstream paediatric sources (AAP, NHS), marked "general
   guidance, not medical advice"; an expert review can follow.
5. **Free**: the whole plan (§8). Premium: the AI-written personal wording and Advisor questions.
