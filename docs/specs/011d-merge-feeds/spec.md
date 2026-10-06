# Spec 011d — Merge feeds

Date: 2026-10-06

Status: Accepted (2026-10-06: the owner's request during the 11b device walk — "when we have multiple feedings close to
each other … we should be able to merge them into one (also more than 2!), and then the time should show the actual
feeding time (sum of all smaller feedings, without adding the breaks in between), and this should be also clear to the
user in the logs." The design choices below are the agent's rulings, listed in §9 for the owner to overturn.)

Depends on: Spec 001 (`CareEntry.Revise`, tombstones, `BreastFeed` segments, `FeedingThresholds`, duplicates), Spec 007
(Needs attention, "Keep both", undo), Spec 009b (the Log day chart), Spec 011 (sync: a merge is ordinary revisions and
tombstones).

Delivered by: **Increment 11d** (`docs/ROADMAP.md`), version **0.13.0** (versionCode 16).

## 1. Purpose

A baby often feeds in bursts: ten minutes, a pause to burp or change, five more minutes, a pause, a few more. Logged with
the timer, that is three feeds; the Log shows three rows, Today counts three, and Needs attention asks which one is
"correct" — though all of them are. The parent wants **one feed** whose length is the time spent feeding, not the time
from the first start to the last end, and wants to see in the Log that it was one feed in parts.

## 2. What a feed in parts is (no new data)

A breast feed is already a list of segments (`BreastSegment(Side, Start, End)`), and its duration is already the sum of
the closed segments (`BreastFeed.TotalDuration`): **gaps between segments are already excluded.** So a merged breast
feed is simply one `BreastFeed` whose segments are all the parts' segments, in order. No new field, no codec version, no
migration, no server change.

**Parts.** A *part* is a maximal run of segments in which each segment starts at most **60 seconds** after the previous one
ended (a side switch is not a new part). A feed with two or more parts is a **feed in parts**. This is derived, so a feed
that was paused and resumed with the timer, or one that was merged, reads the same.

## 3. Which feeds can be merged

Two or more feeds can be merged when all of these hold:

1. Same baby, same variant: all breast feeds, or all bottle feeds (Spec 001 forbids changing a breast feed into a bottle
   and back, so mixed feeds are never merged).
2. All completed (no running timer).
3. **Close together:** sorted by start, each feed starts within **15 minutes** (`FeedingThresholds.DuplicateTolerance`) of
   the previous one's **end** (breast) or the previous one's time (bottle).
4. The result passes Spec 001's rules: breast segments in order and **not overlapping** (overlapping feeds are two
   different feeds, or a mistake — never merged silently), the whole span at most 6 h (`BreastMaxSpan`); bottle total at
   most 1000 ml.
5. None is read-only, quarantined or rejected (ADR 0010 §3).

**The merged entry.**
- **Breast:** the earliest feed is revised (`CareEntry.Revise`) to hold every part's segments in order; its timing follows
  (first start → last end, derived as today). The other feeds are deleted (tombstones). Notes: the non-empty notes,
  earliest first, joined with " · ". `LoggedBy` and `Source` stay those of the earliest feed.
- **Bottle:** the earliest feed is revised to the sum per milk (`Components` added per `Milk`), at the earliest time; the
  others are deleted. A bottle drunk in sips is one bottle: no "parts" are shown for bottles (§9, ruling 4).

**Undo.** A merge is one undoable action: Undo restores the earliest feed's previous details and note and brings the
deleted feeds back (the existing undo mechanism of Spec 007). It syncs as ordinary changes (Spec 011): the other phones
see one feed.

## 4. Where it is offered: Needs attention

Today, Needs attention pairs feeds whose starts are within 15 minutes and offers "Keep this one" / "Keep both". This
increment turns those pairs into **groups** and adds the merge:

- **Groups.** Feeds of the same baby and variant that are close together (§3 rule 3) form a group, chaining: three feeds
  12 minutes apart are one group of three. The existing duplicate pairs (start within 15 minutes) also join a group. A
  "Keep both"/"Keep all" dismissal still hides the pairs it names (Spec 007, ADR 0002 §9); a group whose pairs are all
  dismissed is not shown.
  - Breast feeds that are close only by the end→start rule (a pause, not a double log) are new candidates: they count in
    the Needs-attention badge like duplicates do.
- **The group card** lists every feed (time, side/amount, length) and offers:
  - **Merge into one feed** — when §3 allows it; otherwise not shown (and, for overlapping breast feeds, the card says why:
    "These feeds overlap, so they can't be merged.").
  - **Keep only this one** beside each feed (today's "Keep this one", generalised: the others are deleted, with undo).
  - **Keep all** (today's "Keep both": dismisses every pair in the group).
- After a merge: the toast "Merged into one feed" with **Undo**.

## 5. How a feed in parts reads

- **Today timeline and Log rows:** the title gains the parts, e.g. **"Breast · left 12 m · right 8 m · in 3 parts"**; the
  time column shows the start as today. The length shown is the feeding time (the sum), never the span.
- **Today's Feed tile** ("last feed") shows the feed's start and its feeding time as today.
- **The Log day chart (Spec 009b):** a feed in parts is drawn as its parts — each part a bar of its real length, the gaps
  empty — so the chart shows the pauses.
- **The edit form:** a feed in parts keeps its parts. If the form cannot edit individual parts, it lists them read-only
  ("Part 1 · 14:02–14:12 · left" …) and its changes keep every segment (moving the whole feed shifts every segment;
  the note edits as today). Editing must never flatten the parts into one segment.

## 6. Copy (release-ready, seven languages)

New strings, with the existing plural mechanism (`Strings.Plural`, `.one`/`.other`):
- `Entry.Parts` — "in {0} parts" (plural-aware where the language needs it).
- `Attention.Merge` — "Merge into one feed".
- `Attention.KeepOnly` — "Keep only this one" (replaces "Keep this one" if the existing wording no longer fits a group).
- `Attention.KeepAll` — "Keep all".
- `Attention.Overlap` — "These feeds overlap, so they can't be merged."
- `Attention.Merged` — "Merged into one feed".
- `Edit.Part` — "Part {0}".
- The section heading for groups may stay "Possible duplicates" only if it is true for both kinds of group; otherwise
  "Feeds close together" for the feeding groups.
de, es, fr, it, nl, pl translations; native-speaker review stays owed (as for 11b).

## 7. Tests (what proves it)

- Domain: merging 2 and 3 breast feeds (segments concatenated in order, `TotalDuration` = sum, span = first start → last
  end); a side switch inside a part is one part; 60 s vs 61 s gap; overlapping feeds refused; span > 6 h refused; running
  feed refused; mixed breast/bottle refused; bottle sums per milk and the 1000 ml cap; notes joined.
- Application: the merge use case revises the earliest and tombstones the rest in one transaction, writes the outbox
  changes, and undo restores all; groups chain (12-min steps), dismissals hide pairs, close-by-end breast feeds form a
  group.
- Presentation: titles with "in N parts" in all languages (plural forms), the group card's actions per case, the toast.
- The day chart draws parts separately.
- Device (§8).

## 8. Deliverables (device)

On the Pixel (and the emulator in the same family when the invitation limit allows):
1. Log three breast feeds with the timer, each a few minutes long, with pauses of 1–3 minutes between them.
2. Needs attention shows one group of three; **Merge into one feed** → one feed; the Log row reads "Breast · … · in 3
   parts" with the summed length; the day chart shows three bars with gaps.
3. **Undo** brings the three feeds back.
4. Merge again; on the other phone of the family the three feeds become one within 30 s.
5. Two overlapping feeds: no merge offered, the overlap line shows.

## 9. Rulings (the agent's, for the owner to overturn)

1. **Merge lives in Needs attention, as groups** (where the owner met the "choose which is correct" choice), not as a
   multi-select in the Log — one place, no new selection mode. Cost if wrong: a Log multi-select later.
2. **No new data:** a merged breast feed is its segments; "in N parts" is derived (gap > 60 s). Cost if wrong: a feed
   paused and resumed with the timer also reads "in 2 parts" — which is true.
3. **Close = within 15 minutes, end → start for breast feeds** (the existing duplicate tolerance, measured across the
   pause). Cost if wrong: one constant.
4. **Bottles merge by summing per milk, without a "parts" label** (no field to remember them by without a codec change).
5. **Overlapping feeds are never merged.**
6. **The earliest feed survives** (its id, its logger, its source); the others become tombstones, so sync needs nothing new.
7. **Version 0.13.0 (versionCode 16)**, shipped together with the 11b walk fixes.
