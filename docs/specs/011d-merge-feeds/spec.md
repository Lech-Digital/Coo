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
deleted feeds back. A merge cannot be deferred like Spec 007's delete, so its undo writes the deleted feeds again, live, with newer versions: replacement, as ADR 0002 §3 allows (a tombstone loses to a newer version); still no Domain undelete method (Spec 001 §3.4, Spec 007 §5 amended). It syncs as ordinary changes (Spec 011): the other phones
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

## 10. Built and verified (2026-10-06 · Pixel 10 and an Android 16 emulator)

**Version.** The app is **0.13.0 (versionCode 16)**, with the 11b walk fixes (Spec 011b §16.3). Production runs the API as **0.12.0+86bc605**. Device build: `86bc605`, a trimmed Release build, installed on the Pixel (as andrzej.lech.andrzej@gmail.com) and on the emulator `coo-second` (as adzius.lech@gmail.com), the same family.

### 10.1 What was built

- **Domain.** `FeedMerge`: groups chain across every close pair (a long feed with two shorter ones inside it is one group); breast feeds merge by concatenating their segments in order, bottles by summing per milk (1000 ml cap), notes are joined. A merge is refused for running, read-only (quarantined, rejected, deleted), mixed-kind, overlapping, too-long or too-much feeds, and when the joined notes pass 500 characters.
- **Application.** One use case revises the earliest feed and deletes the rest in one transaction, with the outbox changes. Undo restores the deleted feeds by writing them again, live, with newer versions (Specs 001 §3.4, 007 §5 and this spec's §3 amended); it does not revert the survivor if that feed changed since the merge, and an undo that wrote nothing says so.
- **Presentation.** Needs attention shows **Feeds close together** groups with **Merge into one feed**, **Keep only this one** (any number of other feeds, under one undo), **Keep all**, and a reason when merging is not offered. Titles read "Breast · left 12 m · right 8 m · in 3 parts" in all seven languages (plural forms). The day chart draws each part as its own bar, labelled once. The edit form keeps every part on save: a note-only save writes no details revision, and a start move shifts all parts; a form opened before the other phone merged the feed keeps its parts.

### 10.2 The device demo

| # | Deliverable (§8) | Result |
|---|---|---|
| 1 | Three timer breast feeds with pauses | Logged on the Pixel (23:22 left 3 m, 23:29 right 1 m, 23:32 left 1 m). **They were all "Not accepted by the server"** (§10.3). |
| 2 | One group of three, Merge | **PASS after the fix.** Needs attention showed one group of 3. Merge gave "Breast · left 6 m · right 2 m · in 2 parts". It is 2 parts, not 3, because the Edit → Save that recovered the feeds rounded their times to the minute and left a 60 s gap between two of them; a gap over 60 s starts a part, and exactly 60 s is a side switch by rule (§9, ruling 2). The zoomed day chart showed 2 bars, labelled once. |
| 3 | Undo | **PASS.** Bottles: two of 120 ml merged, Undo restored both. |
| 4 | Merge again, the other phone | **PASS.** Merged again (240 ml); the emulator showed the same single feed. |
| 5 | Overlapping feeds | **Tests only.** No overlapping pair was made on the devices; the Domain and Application tests cover that no merge is offered and the overlap line shows. |

### 10.3 The timer-feed rejection (found by the demo)

**Symptom.** Timer breast feeds appeared under "Not accepted by the server" with the generic "That could not be saved."; bottles and diapers from the same phone were accepted.

**Root cause.** The breast timer reads the phone's clock, which has ticks below a millisecond. `BreastSegment` kept them, and the codec wrote them to the entry's details with all seven fractional digits. The phone stores and pushes the entry's start and end as milliseconds. The server derives a breast feed's timing from the details' segments and compares it with the pushed start and end; sub-millisecond ticks never match, so every timer feed was rejected with `entry.timing-shape-not-allowed`, which had no string. The tests missed it because every test clock used whole seconds or milliseconds.

**It dated from 0.11.0** (sync, 2026-10-02): no timer breast feed had ever synced. Feeds entered in the form, and timer feeds saved again from the edit form, synced, because the form rounds the start to the minute.

**Fix.**
- `8374eb3`: `BreastSegment` holds its start and end to the millisecond (in the `init` accessors, so the constructor, `with`, the codec's decode and every timer step are covered). This fixes both sides: the server accepts timer feeds from phones still on 0.11 to 0.13 once it is deployed, and updated phones send clean details. Stored rows need no migration. The precision check stays strict: a start a millisecond or more off its segments is still rejected.
- `86bc605`: `entry.timing-shape-not-allowed` has a string in seven languages: "The times on this entry don't fit together. Edit the entry to send it again."
- API deployed as **0.12.0+86bc605**. 2140 tests green, `dotnet format` clean.

**Recovery.** Nothing resends a rejected entry. Each of the three was recovered on the Pixel with Edit → Save, which writes a new version (and rounds the feed to whole minutes). Optional, not done: a one-time migration that puts rejected `entry.timing-shape-not-allowed` entries back into the outbox.

### 10.4 Rulings (the agent's, beyond §9)

- A note longer than 500 characters once joined refuses the merge (`NoteTooLong`), with the copy "The notes are too long to join into one."; refusing beats silently cutting notes.
- A partly dismissed group still offers Merge for all its feeds; the parent sees every feed close together and decides. "Keep all" hides it.
- The badge counts groups (one decision per group), not feeds.
- A merge's undo restores the deleted feeds by replacement (a newer live version, last writer wins per ADR 0002 §3); there is no Domain undelete. Specs 001 §3.4, 007 §5 and 011d §3 are amended. It converges on the server and on phones.

### 10.5 Deferred minors and known limits

- No Today view-model test for a multiple pending delete (M-b).
- The badge counts a group that is pending delete (M-c).
- Undoing an edit says "Undone" even when nothing differed (M-d).
- French wording ("Partie", "en N fois") goes to the native-speaker review with the other new strings (M-e).
- "Keep all" is not atomic across a group's pairs.
- The overlap case was not made on a device; tests only.
- Rejected timer feeds from before the fix are not recovered by themselves (§10.3).

**Final review (whole increment, with the 11b walk fixes and the timer fix).** No Critical findings. One Important, fixed:
an edit form left open while the other phone merged its feed could, on Save or Undo, drop the parts it did not know
about. Ruling: when the stored entry's version differs from the one the form loaded, Save writes nothing, says "This
entry changed on another phone. Have a look and save again." and shows the entry as stored; Undo after an edit puts back
the entry as stored right before the save (cd3e261). Also fixed: a rejected entry saved unchanged from Edit is written
again with a new version, so it is pushed again (2ab6152); a part that starts after midnight is clamped to the day's end
on the chart (195d69f). Deferred minors: a save that fails after a first revision makes the next Save report "changed on
another phone" once; the clamped after-midnight sliver is drawn at the 3 px minimum width. Rulings on the reviewer's
open questions: a deleted feed edited offline on the other phone comes back beside the survivor as an Overlap group
that "Keep only this one" resolves (last writer wins, ADR 0002); a feed can show twice for one pull between the revision
and the tombstones, and converges on the next pull. CI green: run 37539114412 (2ab6152). Final build 2ab6152 installed
on the Pixel and the emulator.
