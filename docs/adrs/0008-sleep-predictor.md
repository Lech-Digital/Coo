# 8. Sleep predictor

Date: 2026-09-29

Status: Accepted — amended 2026-10-02 (see below)

> **Amendment, 2026-10-02 (owner): the predictor stays Premium, and may use AI.** The split of §7 stands (age-typical
> guidance free, the personal prediction and its reminder Premium). The owner may want an LLM to help the prediction
> or its explanation; that is an open question for the predictor's spec (Increment 14), which must keep the backtest
> gate of §5 and the privacy rules of ADR 0005 if it does.


## Context

The sleep predictor is a Premium feature. It suggests when the next nap window opens, and when
bedtime is likely, so that parents can put the baby down before overtiredness sets in. It feeds
the "nap window" reminder (ADR 0007).

The constraints are:

- **Runs on the phone, offline.** It must work at 3 AM with no signal. ADR 0001 therefore places it
  in `Coo.Domain/Insights`, compiled once for device and server.
- **Little data per baby.** A new user has a few days of history. Even a heavy user has a few
  hundred sleeps, and a baby's rhythm changes every few weeks as they grow.
- **Explainable.** A parent must be able to see *why* it suggests 14:10 ("her wake windows have
  been about 2 h 10 min this week"). An unexplained prediction that turns out wrong once is never
  trusted again.
- **Not medical advice.** The predictor suggests timing. It does not diagnose, and it never tells a
  parent to keep a baby awake.
- **Testable and deterministic** (ADR 0004 §10). It is a pure function of the history, the baby's
  age and `now`.

The widely used concept is the **wake window**: the time a baby is awake between one sleep ending
and the next beginning. Typical wake windows grow with age. The first window of the day tends to be
the shortest, and the last one, before bedtime, the longest. A short nap tends to shorten the
following window.

## Decision

### 1. A deterministic, explainable estimator, not machine learning

v1 is a statistical estimator: an age-based prior, blended with the baby's own recent wake windows.
Machine learning was rejected for v1 for four reasons:

- there is too little data per baby;
- it needs training infrastructure we do not have;
- it is not explainable to a parent;
- it is harder to test than a pure function.

A learned model can replace the estimator later behind the same interface, once the backtesting
harness (section 6) exists to prove that it is better.

### 2. Inputs

- **Completed sleep sessions** for this baby from the last 14 days, plus any session in progress.
- **Age.** From the birth date, or **corrected age** when the optional due date is set and the baby
  is under 24 months. Preterm babies follow their corrected age.
- **`now`**, and the household's time zone (ADR 0006 §5).

### 3. Classifying sleeps

A pure `SleepClassifier` labels each session as a **night** sleep or a **nap**:

- A session is **night** if it starts inside the baby's night window and lasts at least 3 hours, or
  if it lasts at least 5 hours, whenever it starts.
- The night window is learned: the middle 80% of the last 14 days' night-sleep start times. Until
  there is enough data, it defaults to 18:30–23:00.
- Everything else is a **nap**.
- A person can override a session's label in the UI. The override is stored on the entry and always
  beats the classifier.

Wake windows are then indexed by **position in the day**: WW1 runs from the morning wake to the
first nap, WW2 from the end of the first nap to the second, and so on. The last position runs to
bedtime.

### 4. The estimate

For the baby's current position `k`:

1. **Prior.** The age-based typical range for position `k` (section 5). Its midpoint is the prior
   estimate.
2. **Personal.** A **weighted median** of this baby's observed WW*k* over the last 14 days. The
   weights decay exponentially, with a half-life of 4 days, so a baby who has just dropped a nap
   changes the estimate within a week. Observations outside 0.5–2 times the prior's range are
   treated as logging errors and excluded.
3. **Blend.** `estimate = (n_eff × personal + K × prior) / (n_eff + K)`, where `n_eff` is the sum
   of the weights and `K = 5`. A new baby starts at the prior. After about a week of consistent
   logging, the baby's own data dominates.
4. **Short-nap adjustment.** If the previous nap lasted less than 45 minutes, the estimate is
   multiplied by 0.85.
5. **Window.** `next window = last wake + estimate ± band`, where `band` is half the interquartile
   range of the personal observations, clamped to between 10 and 30 minutes.
6. **Confidence** is **High** when `n_eff >= 5` and the interquartile range is at most 30 minutes.
   Otherwise it is **Low**.

All the constants (`K`, the half-life, 0.85, 45 minutes, the band limits and the thresholds) live
in one `PredictorParameters` record. The constants are starting values. The backtesting harness
(section 6) tunes them, and never intuition.

**Output.** The predictor returns:

- the window's earliest start, centre and latest start;
- the confidence;
- the position (nap *n*, or bedtime);
- the inputs that explain it: the personal median, the number of days used, the prior range, and
  whether the short-nap adjustment applied;
- `PredictorVersion`.

**While the baby is asleep**, no next-window prediction is produced. v1 does not predict when a
sleep will end.

### 5. The age prior is versioned data

These are initial values taken from commonly published guidance. **They must be reviewed against
current pediatric sources before launch.** They live as a versioned data table in
`Coo.Domain/Insights`, not scattered through code:

| Age (corrected) | Typical wake window | Typical naps |
|---|---|---|
| 0–4 weeks | 35–60 min | 4–6+ |
| 4–12 weeks | 60–90 min | 4–5 |
| 3–4 months | 75–120 min | 3–4 |
| 5–7 months | 2–3 h | 3 |
| 7–10 months | 2.5–3.5 h | 2 |
| 11–14 months | 3–4 h | 1–2 |
| 14–24 months | 4–6 h | 1 |

Within an age band, the prior grows across the day. WW1 uses the low end of the range and the last
window before bedtime uses the high end, interpolated linearly between them.

### 6. The backtesting harness is the acceptance test

A `SleepPredictorBacktest` in `Coo.Domain.Tests` replays histories. For each real nap start it
predicts from only the data before that nap, and reports:

- **mean absolute error** of the window centre against the actual nap start, reported separately
  for High and Low confidence;
- **hit rate**: the share of actual starts that fall inside the window.

Release targets for High confidence: a mean absolute error of **20 minutes or less**, and a hit
rate of **60% or more**. A change to the algorithm or its parameters ships only if it does not
worsen either metric. Each change also bumps `PredictorVersion`.

The histories come from two sources:

- **synthetic generators** that model a growing baby, noisy logging, dropped naps and missed
  entries;
- **opt-in exported histories** from beta families, which are anonymised and contain sleep times
  only.

### 7. Free and Premium

- **Free.** The age-typical range ("Typical wake window at 5 months: 2–3 h") and "awake for
  1 h 20 min". This is honest, useful, and needs no personal model.
- **Premium.** The personal prediction, with its explanation and the nap-window reminder
  (ADR 0007). Gating uses the cached entitlement (ADR 0004 §9).
- **The reminder fires a lead time before the window's earliest start.** The lead time defaults to
  15 minutes, and can be set to 5, 10, 15 or 20 minutes. It is scheduled with high precision where
  the exact-alarm permission is granted (ADR 0007 §3). It is suppressed only if the window is at
  night and the person has not enabled night reminders (ADR 0007 §4).
- **Low confidence**, on either tier, shows the age-typical range with "Log a few more days for a
  personal suggestion". It never shows a precise time it does not believe.

### 8. Wording guardrails

- Always "suggested" or "likely", never "should". Never "keep her awake until …".
- The explanation is always one tap away.
- A one-time notice states that suggestions are based on logged patterns and general guidance, and
  are not medical advice. It also says to consult a pediatrician about concerns with sleep or
  development.
- Safe-sleep guidance is out of scope for the predictor, and is linked to reputable sources in
  Help.
- All wording is localized in the seven languages (ADR 0006), with a native-speaker review.

### 9. Where it lives

| Concern | Project |
|---|---|
| `SleepClassifier`, `WakeWindowPrior` table, `SleepPredictor`, `PredictorParameters`, `SleepPrediction` | `Coo.Domain/Insights` |
| `GetNextSleepWindow` use case, explanation view model data, feeding the prediction into `ReminderPlanner` | `Coo.Mobile.Application/Insights` |
| Backtest harness, synthetic history generators, fixtures | `tests/Coo.Domain.Tests/Insights` |
| Server-side use (reports and future cross-device features), with the same compiled code | `Coo.Api.Application` (later) |

Twins and siblings are predicted independently: one prediction per baby.

## Consequences

### Positive

- It works offline, on the phone, from the first day, starting from the age prior and becoming
  personal as data arrives.
- Every suggestion can be explained in one sentence a tired parent understands.
- It is deterministic and pure, so it is testable to the minute with a fake `now`.
- The backtest harness makes "better" measurable. Parameters are tuned on evidence, and every
  change is versioned.
- Free users get honest, age-based guidance. Premium adds personal prediction and reminders.

### Negative

- A statistical estimator will not capture everything: illness, teething, travel, daycare days
  with a different rhythm. When the pattern breaks, predictions degrade until the decay catches up.
- Nap transitions (for example 3 naps to 2) shift the position index. For several days the
  predictor mixes old and new patterns and shows Low confidence more often.
- Prediction quality depends on logging quality. A family that logs sleeps only sometimes stays at
  Low confidence, and sees age-typical ranges instead.
- The age prior needs an expert review before launch, and it is only as good as the guidance it is
  based on.
- The backtest needs real histories to be meaningful. Before a beta produces them, the targets are
  checked only against synthetic data, which encodes our own assumptions.
