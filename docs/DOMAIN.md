# Domain Model

This document outlines the core domain model rules, entry kinds, and validation pipelines for CooApp, based on the original Care-log domain model specifications.

## 1. Principles
* **Pure Domain**: The `Coo.Domain` project has no external dependencies. It relies on `Result<T>` and custom error codes (e.g., `sleep.too-long`) instead of throwing exceptions for rule violations.
* **Immutability**: Value objects (`Volume`, `TimeRange`, `EntryTiming`) are immutable and valid by construction.
* **Time as a Parameter**: The domain never reads the system clock directly. Time is always passed into functions (`now`) to make testing and deterministic behavior possible.

## 2. CareEntry Core
Every event logged in the app is a `CareEntry`. It consists of:
* `Id` (UUIDv7)
* `HouseholdId`
* `Kind` (e.g., `feeding`, `diaper`, `sleep`, `pump`)
* `Subject` (`Baby` or `Caregiver`)
* `Timing` (`Point` for instant events, `Session` for events with a duration)
* `LoggedBy` (The person who logged it)
* `Source` (`Manual`, `Timer`, `Watch`, `AiParsed`)
* `Note` (Optional text)
* `Details` (Kind-specific JSON payload)

## 3. Entry Kinds

### Feeding (`feeding`)
A single kind that handles both breast and bottle feeds. The method cannot be changed once created.
* **Breast Feed**: Contains ordered segments (`Left` or `Right` side) with start and end times. Total duration cannot exceed 6 hours.
* **Bottle Feed**: Contains milk components (Expressed Breast Milk, Formula) and volumes (max 1000 ml).

### Diaper (`diaper`)
Instant events (`PointOnly` timing).
* **Contents**: `Wet`, `Dirty`, or `Mixed`.

### Sleep (`sleep`)
Events with a duration (`SessionOnly` timing).
* **Kind Override**: Can be manually tagged as a `Nap` or `Night` sleep, overriding automatic classification.
* **Limit**: Cannot exceed 24 hours.

### Pumping (`pump`)
Events with a duration (`SessionOnly` timing).
* **Subject**: The `Caregiver` who pumped.
* **Yield**: Can track total volume or volume per breast (max 4 hours duration).

## 4. Duplicates & Plausibility
* **Plausibility Warnings**: Non-blocking warnings for unusual entries (e.g., `warn.in-future` if logged > 5 minutes ahead, or `warn.bottle-amount` if > 400 ml). These flag entries in the UI but don't prevent saving.
* **Duplicate Detection**: The domain compares local entries against new synced entries to flag overlaps. Rules vary by kind (e.g., feeds within 15 mins of each other, overlapping sleep sessions).
* **Dismissal**: Users can choose to "Keep both" duplicates, which writes a synced `DuplicateDismissal` record to prevent prompting again.

## 5. Baby Profiles & Age
* `BabyProfile` records the name, nicknames (for AI pseudonymization), and birth/due dates.
* `BabyAge` computes actual age and corrected age (for premature babies, adjusted up to 24 months).
