# Merge feeds (Spec 011d) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan
> task-by-task.

**Goal:** Feeds close together can be merged into one feed in parts from Needs attention; the Log reads "in N parts".

**Architecture:** Domain gets a pure merge operation and a parts count on `BreastFeed`; Application gets a `MergeFeeds`
use case (revise the earliest, tombstone the rest, one transaction, undoable) and feed groups in `GetAttention`;
Presentation gets the group card, titles with parts, the day-chart parts and part-preserving edit. No codec, migration or
server change (spec §2).

**Tech Stack:** .NET 10, MAUI, xUnit + NSubstitute + FluentAssertions, FsCheck where the domain already uses it.

**Spec:** `docs/specs/011d-merge-feeds/spec.md` (binding; §9 lists the rulings).

## Global Constraints

- Hexagonal: Domain has no dependencies; ports in Application; DI in the composition root.
- No new details field, codec version, SQLite migration or server change (spec §2, ruling 2). If a task finds this
  impossible, it stops and reports (BLOCKED) instead of adding one.
- Thresholds: part gap **60 s**; close together **15 min** (`FeedingThresholds.DuplicateTolerance`), end → start for
  breast, time → time for bottle; `BreastMaxSpan` 6 h; bottle cap 1000 ml (spec §3).
- Copy release-ready, seven languages (en default + de, es, fr, it, nl, pl), plurals via `Strings.Plural`.
- Tests: xUnit + NSubstitute + FluentAssertions; `Method_State_Expected`; AAA comments; mock only outbound ports; never
  mock domain types.
- Commits as adziusmaster <adzius.lech@gmail.com>; no Co-Authored-By / Generated trailers; stage by path; check
  `git diff --cached --stat`.
- `dotnet format --verify-no-changes` clean; zero warnings.

## Review Focus

1. A merge that half-applies (earliest revised, a tombstone fails) — must be one transaction; undo must restore all.
2. Editing a feed in parts flattens its segments (spec §5) — the edit path must keep every segment.
3. Group chaining vs dismissals: a group partly dismissed must still offer the right actions; a fully dismissed one hides.
4. Overlapping or running feeds offered for merge — never.
5. The other phone (sync): the tombstones and the revision arrive in any order; the Log never shows a feed twice for long.

---

### Task 1: Domain — merge and parts

**Files:** `src/Shared/Coo.Domain/CareLog/Kinds/Feeding/` (BreastFeed, BottleFeed, a new `FeedMerge` or equivalent),
tests in the Domain test project.

- `BreastFeed.Parts` (int, derived: runs of segments with gaps ≤ 60 s).
- A pure operation that, given ≥ 2 feeds (`CareEntry`, same baby, same variant), returns either the merged details (+
  joined note) for the earliest entry and the ids to delete, or a refusal reason (`Overlap`, `Running`, `MixedVariant`,
  `NotClose`, `TooLong`, `TooMuch`, `ReadOnly`, `DifferentBaby`). Rules exactly as spec §3.
- A pure "close together" predicate per spec §3 rule 3 (used by Task 2's groups).
- Tests: spec §7 Domain list.

### Task 2: Application — MergeFeeds and feed groups

**Files:** `src/Mobile/Coo.Mobile.Application/CareLog/` (UseCases, Attention), the store port if a transactional
multi-write is missing (`Ports.cs`, `CareLogStore.cs`), Presentation `Today/Undo.cs` for the undoable action.

- `MergeFeeds(ids)`: loads, calls Task 1, then in **one transaction** revises the earliest (`CareEntry.Revise` + `SetNote`
  with new HLC versions) and tombstones the others, writing the outbox rows. Returns the merged id or the refusal.
- Undo: restores the earliest's previous details/note and un-deletes the others, in one transaction (new versions).
- `GetAttention`: duplicate pairs become groups (union of duplicate pairs and close-together breast/bottle neighbours of
  the same baby and variant, within the existing 7-day window), dismissals applied per pair; each group carries whether
  it can merge and why not.
- Tests: spec §7 Application list.

### Task 3: Presentation and pages

**Files:** `Coo.Mobile.Presentation/Attention/AttentionViewModel.cs`, `Coo.Mobile/Pages/AttentionPage.cs`,
`Presentation/Formatting/Formats.cs`, the day-chart drawing (Spec 009b code), the edit view model/page for breast feeds,
`Resources/AppResources*.resx`.

- Group card per spec §4 (Merge into one feed / Keep only this one / Keep all / overlap line), toast with Undo.
- Titles "· in N parts" (spec §5) wherever a breast feed's title is built (Today, Log, chart labels).
- Day chart draws parts separately.
- Edit keeps parts (spec §5): read-only part list if the form cannot edit parts; shifting moves all segments.
- Strings per spec §6 in seven languages.
- Tests: spec §7 Presentation list, chart parts.

### Task 4: Device check (controller)

Version 0.13.0 (versionCode 16). Release build with the debug screen, installed on the Pixel and the emulator; spec §8.
