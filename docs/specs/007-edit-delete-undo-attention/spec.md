# Spec 007 — Edit, delete, undo, earlier entries, and Needs attention

Date: 2026-10-01

Status: **Draft — for review**

Depends on: Spec 001 (Revise, Retime, tombstones, plausibility, duplicates), Spec 004 (storage), Spec 006
(Today, sheets, Log), ADR 0002 §9 (duplicates, "keep both"), ADR 0010 §3 (read-only entries).

Delivered by: **Increment 7** (`docs/ROADMAP.md`). After it, Coo is a complete single-phone baby log.

## 1. Purpose

Parents make mistakes at 3 AM, and log things late. This increment lets them:

- **correct** any entry: its time, its details and its note;
- **delete** an entry, with **undo** instead of a confirmation dialog;
- **undo** a quick log they did not mean;
- **log something that already happened**, at a chosen time ("fed at 14:00, I forgot");
- see and resolve what **needs attention**: probable duplicates, unreadable entries, and entries from a newer app;
- see **plausibility warnings** where they help, without ever being blocked.

**Out of scope:**

| Not in this increment | Where |
|---|---|
| Sync, and its rejected entries | Increment 10; "Needs attention" is ready for them |
| Editing the baby, the units, the language, or the quiet hours | A settings increment |

## 2. Opening an entry

- **A tap on a timeline row**, on Today or on Log, opens that entry's **edit sheet** (§3).
- **A tap on a running timer's card** opens the same sheet, so a timer started late can have its start moved back.
- **Read-only and quarantined entries** open an **info sheet** instead. It shows the time, the kind and why Coo
  cannot show it ("From a newer version of Coo: update the app", or "This entry could not be read"), with
  **Delete**.

## 3. The edit sheet, one form per kind

Every form has a **time** (a date and a time picker), the kind's own fields, an optional **note**, then
**Save** and **Delete**. The kind and "who logged it" cannot be changed (Spec 001 §6.1). Correcting a kind is a
delete and a new entry.

| Kind | Fields |
|---|---|
| Breast feed | The start time; minutes on each side, in the order fed (Spec 001 `FromSideDurations`); for a running feed, only the start |
| Bottle | The time; the milk (formula, breast milk, both) and the amounts, in the household unit |
| Diaper | The time; wet, dirty or mixed |
| Sleep | The start and the end (the end is absent while running); nap or night, or "decide for me" |
| Pumping | The start and the end; left and right amounts, or a total |

- **Save** goes through the existing use cases (`ReviseEntry`, `Retime`): one revision with one stamp from the clock
  per change. A Domain error appears inline, under the field it concerns, in the user's language. Nothing is saved
  until every rule passes.
- **Plausibility warnings** (Spec 001 §8) appear inline under the field, as quiet text. Examples: "That's more than
  most bottles (over 400 ml)", "This is in the future", "Before Maja's birth". They never block Save. One-tap quick
  logs show none, because they are always "now" with sensible values.

## 4. Logging something earlier

Each quick-log sheet (feed, diaper, sleep, pumping) gets a quiet link, **"Earlier…"**. It opens the same form as §3,
empty, with the time set to now minus 15 minutes. **Save** creates the entry, with `Source = Manual`.

- A breast feed logged this way uses minutes per side (Spec 006 Q2: there is no untimed breast feed).
- A sleep logged this way needs a start, and an end unless it is still running.
- The asleep question (Spec 006 §5a) applies only to logs made **now**. An earlier entry is a record of the past,
  not a change to a running sleep.

## 5. Delete and undo

- **Delete never asks for confirmation.** It shows the toast **"Deleted · Diaper · wet"** with **Undo** for
  **5 seconds**.
- **Undo is honest about what was written:**

  | What happened | What Undo does | Why |
  |---|---|---|
  | **Delete** | **The tombstone is not written until the 5 seconds pass.** Undo cancels it, and nothing was ever written. | Spec 001 has no undelete, by design: a tombstone is final. The watch already works this way (ADR 0009). |
  | **A quick log** (diaper, bottle, a started timer) | Writes a tombstone for the new entry | The entry was already saved; it must be, because an entry is never held only in memory (ADR 0002) |
  | **An edit** | Writes the previous values back, as a new revision | Last-writer-wins needs a newer version, never an older one |

- If the app is killed during the 5 seconds of a pending delete, the delete is lost and **the entry stays**. That
  is the safe direction: no data is lost, and the parent deletes it again.
- Quick logs get the same **Undo** in their toast, for 5 seconds.

## 6. Needs attention

**A quiet badge on Today**, below the hero: **"2 need attention"**, in `Ink2` with a dot, and only when there is
something. Tapping it opens the **Needs attention** page (pushed, not modal), with up to three groups:

| Group | What | Actions |
|---|---|---|
| **Probably the same** | Pairs from `DuplicateDetector` (Spec 001 §9): two diapers 4 minutes apart, two overlapping sleeps… Both entries are shown side by side, with who logged them. | **Keep both** writes a `DuplicateDismissal`, so the pair is never shown again, on any phone. **Keep this one** and **Keep that one** delete the other, with undo. |
| **Could not be read** | Quarantined entries (Spec 004 §6) | **Delete** |
| **From a newer version** | Read-only entries (ADR 0010 §3) | The info sheet, and **Delete** |

- The duplicate check runs on this phone **after every write** and **at start-up**, over the entries within
  ±24 hours of the new one. After sync (Increment 10), it also runs after every pull (ADR 0002 §9).
- The group for **sync rejections** (ADR 0002 §11) is added with sync. The page already lists groups in order,
  as an extension point.
- Nothing here interrupts. There are no dialogs. The badge is the only signal.

## 7. Storage

- **`duplicate_dismissals`**, in **migration 0003**: `id`, `household_id`, `entry_a`, `entry_b` (canonical order,
  Spec 001 §9.2), the version columns and `is_deleted`. Saving a dismissal appends its outbox row.
- **The outbox's `entity_type` CHECK becomes "not empty".** Today it lists the three types by name. Adding
  `DuplicateDismissal` would need a rebuild of the outbox table, and so would every future synced type (ADR 0010 §5).
  The list of allowed types moves into code, in one closed `SyncedEntityType` list in Infrastructure, covered by a
  test. The CHECK's value was low, and the rebuild cost would recur forever.
- Migration 0003 rebuilds the outbox table once for that, keeping every row and its order (`seq`). A test migrates
  a 0002 database with outbox rows.

## 8. Where it lives

| Concern | Project |
|---|---|
| `GetAttention`: duplicate pairs (with dismissals applied), quarantined and read-only entries | `Coo.Mobile.Application/CareLog` |
| `DismissDuplicate`, `KeepOne` (delete the other) | `Coo.Mobile.Application/CareLog` |
| `LogEarlier`: build a past entry from a form, through the Domain factories | `Coo.Mobile.Application/CareLog` |
| `IDuplicateDismissalStore` port; SQLite implementation and migration 0003 | Application / Infrastructure |
| The pending-delete timer and the undo stack (one level, 5 s) | `Coo.Mobile.Presentation/Today/Undo.cs` |
| Edit-form view models, one per kind, with inline errors and warnings | `Coo.Mobile.Presentation/Edit/` |
| `AttentionViewModel` and the badge | `Coo.Mobile.Presentation/Attention/` |
| Sheets, the attention page, timeline-row taps | `Coo.Mobile` |
| Strings for every warning code and every error the forms can show, in seven languages, release-ready | `eng/strings/strings.py` |

## 9. Tests

| Where | What |
|---|---|
| Application | Duplicates are found after a write, and a dismissal hides a pair in both orders. `KeepOne` deletes the other entry. `LogEarlier` builds each kind; a breast feed without minutes is refused. |
| Presentation | Undo of a delete writes nothing; a delete completes after 5 s. Undo of a quick log writes a tombstone. Undo of an edit writes the old values with a newer version. Inline errors and warnings per field. The badge count. |
| Infrastructure | Migration 0003: outbox rows survive, in order; the dismissal round-trips; an unknown entity type is refused by code. |
| Localization | Every `warn.*` code and every error code a form can show has a string, in all seven languages |
| On the phone | The deliverables below |

## 10. Increment 7 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Edit any entry | Tap a timeline row; change the time and the details; Save; the timeline and the totals follow |
| 2 | Move a running timer's start | Tap a running card; set the start 10 minutes earlier |
| 3 | Delete and undo | Delete, tap Undo: it is back, and nothing was written. Delete and wait: it is gone. |
| 4 | Undo a quick log | Tap Wet by mistake, then Undo |
| 5 | Log something earlier | "Earlier…": a 14:00 bottle, a nap from 10:00 to 11:15 |
| 6 | Duplicates | Log two diapers a minute apart: "1 needs attention"; Keep both clears it for good |
| 7 | Warnings | A 500 ml bottle shows a warning, and still saves |
| 8 | CI green | GitHub Actions |

## 11. Decisions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Undo: a delete waits 5 s before it is written; undoing a quick log writes a tombstone; undoing an edit writes the old values back | **Yes.** Nothing is ever held only in memory except a pending delete, which fails safe. |
| Q2 | No confirmation dialog for delete; Undo instead | **Yes.** A dialog at 3 AM is friction, and an undo is safer than "Are you sure?". |
| Q3 | Duplicates are shown as a quiet "needs attention" badge on Today, never as a pop-up | **Yes.** A pop-up after logging would punish the parent who logged correctly. |
| Q4 | Plausibility warnings only in the forms (edit and earlier), never on one-tap quick logs | **Yes.** Quick logs are "now" with sensible values; warnings there would be noise. |
| Q5 | The outbox's list of entity types moves from a database CHECK into code, through migration 0003 | **Yes.** Otherwise every new synced type rebuilds the outbox table (ADR 0010 extensibility). |
| Q6 | "Earlier…" defaults to 15 minutes ago | **Yes.** It is the most common "I forgot" gap, and it is one tap to change. |
