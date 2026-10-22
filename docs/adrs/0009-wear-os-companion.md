# 9. Wear OS companion

Date: 2026-09-29

Status: Accepted — amended 2026-10-02 (see below)

> **Amendment, 2026-10-02 (owner): logging from the watch is free.** Coo charges only for AI-assisted logging (ADR
> 0004, amended). §4's Premium gating is superseded: the watch shows its logging surfaces to every household. The rule
> that an entry from the watch is never dropped stays.


## Context

Smartwatch quick-logging is a Premium feature. Logging a diaper change or starting a feed with one
tap on the wrist, while holding a baby, is the purest form of "reducing friction at 3 AM".

ADR 0001 placed the watch app outside the .NET solution. MAUI cannot target watchOS and supports
Wear OS poorly, and Android ships first, so Wear OS comes first and watchOS later.

The design questions are:

- Does the watch talk to the Coo API itself (**standalone**), or through the phone (**tethered**)?
- How does it stay consistent with the offline-first sync protocol (ADR 0002)?
- How is it gated as Premium without ever losing an entry?

The constraints are:

- **Self-hosted, no Firebase** (ADR 0003, ADR 0007).
- **The phone is the source of truth** for the person holding it (ADR 0001).
- **The seven languages** (ADR 0006).
- A second implementation of sync, authentication and conflict handling in Kotlin would double the
  riskiest code in the system.

## Decision

### 1. Native Kotlin, tethered to the phone

The watch app is written in **Kotlin with Jetpack Compose for Wear OS**, using Horologist for Wear
UI patterns, in `wearables/wearos/`. It is built with Gradle and Android Studio, outside the .NET
solution.

It is **tethered**: it talks only to the Coo phone app, over the **Wearable Data Layer API**.
The Data Layer is part of Google Play services on the phone and the watch. It is not Firebase, and
it has no server of ours or anyone else's in the path. The watch never calls the Coo API, holds no
tokens, and implements no sync.

Standalone was rejected. It would need its own authentication (a token handed over from the phone,
with its own refresh), a second implementation of ADR 0002 in Kotlin, and its own conflict
handling, all for the rare case of a watch on LTE away from the phone. At 3 AM, the phone is on the
nightstand.

### 2. Watch to phone: every log is a durable, idempotent item

- Each tap on the watch creates a log request with a **UUIDv7 generated on the watch**, and the
  moment of the tap. That becomes the entry's ID and time (ADR 0002 §1).
- Requests are sent as Data Layer `DataItem`s. Play services persists them and delivers them when
  the devices reconnect, so a watch out of Bluetooth range queues its logs safely.
- **One `DataItem` path per entry: `/entries/{entryId}`.** The item holds the entry's *latest state*
  (upsert or delete), not a history of operations. This is the same state-based model as ADR 0002
  §3. The Data Layer keeps the latest item per path, so a create followed by a delete while
  disconnected usually arrives as the delete alone.
- **The watch stamps a version on every state.** Each state carries a hybrid logical clock stamp
  generated on the watch, using the same format as ADR 0002 §4, with the watch's own `deviceId`.
  The phone uses that stamp as the entry's version, and does not stamp it again. Arrival order
  therefore never matters. A delete that overtakes its create writes a tombstone. The late create
  has a lower stamp, so it is `superseded` and cannot bring the entry back. The phone does not need
  to process watch messages in order, which the Data Layer does not guarantee anyway.
- The phone's `WearableListenerService` receives each item. It writes the entry through the
  **ordinary local write path**: record plus outbox, in one transaction (ADR 0002 §2), with
  `source = Watch` and last-writer-wins against any local state. It then acknowledges by deleting
  the `DataItem`. A duplicate delivery hits the same entry ID and stamp, so it is harmless.
- **An entry from the watch is never dropped**, including when the household is not Premium
  (section 4).

### 3. Phone to watch: a small state snapshot

The phone publishes a `DataItem` with the state the watch displays. It republishes after every
local write and every sync cycle, and only when the content changed. The snapshot contains:

- the babies, as ID plus a display name, and the selected baby;
- sessions in progress (a feed or sleep timer, with its start time);
- the last feeding (time and side), the last diaper change, and time awake;
- the next nap window, if Premium (ADR 0008);
- the entitlement tier and the household's units;
- the language to display, following the phone's app language (ADR 0006).

### 4. Premium gating without losing entries

- The watch shows its logging surfaces when the snapshot says Premium. Otherwise it shows "Coo
  Premium unlocks watch logging" with a button that opens the Premium screen on the phone.
- If a log arrives on the phone from a non-Premium household, for example because the entitlement
  lapsed while the watch was offline, **the phone saves it anyway**. Losing data is worse than
  giving away one entry, and care data is never gated (ADR 0004 §3).

### 5. Watch surfaces

- **Tile.** Quick-log buttons: feed left, feed right, bottle; diaper wet, dirty or mixed; sleep
  start or stop. Pressing the button of a running session stops it. A bottle amount uses the last
  amount, adjustable on the confirmation screen.
- **Complications**: "time since last feed" and "next nap window".
- **App**: the same actions, plus a short list of the latest entries and a baby switcher for twins.
- **Undo.** Every log shows a 5-second undo, and **the watch does not send the entry until the undo
  window has passed.** Undo within the window cancels the entry on the watch, and nothing reaches
  the phone. A deletion after the window, from the recent-entries list, is sent as a delete state on
  the entry's path. That delete becomes a tombstone on the phone, ordered by its stamp (section 2).
  If the watch app is killed during the undo window, the pending entry is written to the Data Layer
  on the next start, so it is delayed but never lost.
- **Voice logging** on the watch sends the recognised text to the phone, which runs the ADR 0005
  parse. The confirmation happens on the watch as a compact card. Voice is deferred to after the
  first watch release.

### 6. One shared contract, checked from both sides

- The watch messages (log request, delete request, state snapshot) are defined in
  `Coo.Contracts/Wearables`, with a `schemaVersion`.
- **Golden JSON fixtures** in `wearables/contract-fixtures/` are the single source of truth. .NET
  tests deserialise and re-serialise them against `Coo.Contracts`, and Kotlin tests do the same
  against the watch's data classes. A contract change that breaks either side fails that side's
  build.
- The phone accepts the current and the previous watch `schemaVersion`, because a watch app and a
  phone app update independently.

### 7. Packaging and signing

- **The Data Layer only connects apps that share a package name and signing certificate.** The
  watch app is therefore `com.lechdigital.coo`, signed with the same upload key as the phone app,
  and released through the Wear OS form factor in the same Play listing.
- The `BUILD.md` recipe gains a Gradle section. It follows the sibling apps' signing rules: the
  keystore stays outside the repo, and signing is enabled only when its password is set.
- Wear OS strings live in `values-*/strings.xml` in the seven languages. The key-parity check
  (ADR 0006 §3) covers them.

### 8. Where it lives

| Concern | Project |
|---|---|
| Watch message DTOs | `Coo.Contracts/Wearables` |
| Handling watch log and delete requests, building the snapshot; port `IWearableBridge` | `Coo.Mobile.Application/Wearables` |
| `WearableListenerService`, `DataClient` publishing (`Xamarin.GooglePlayServices.Wearable`) | `Coo.Mobile.Platform/Wearables` |
| Watch app, tile, complications | `wearables/wearos/` (Kotlin, Gradle) |
| Shared golden fixtures | `wearables/contract-fixtures/` |

**watchOS** will follow the same design with WatchConnectivity (`transferUserInfo` for durable
logs, `updateApplicationContext` for the snapshot), against the same contract and fixtures, in
`wearables/watchos/` (Swift and SwiftUI).

## Consequences

### Positive

- There is one sync implementation, one auth implementation and one write path. The watch is a
  remote control for the phone's local database, not a third client.
- Logs are durable end to end: queued by Play services on the watch, and written through the outbox
  on the phone. An out-of-range watch loses nothing.
- There are no tokens or secrets on the watch.
- Premium gating on the watch can never cost an entry.
- The golden fixtures keep a Kotlin app and a .NET app honest about one contract.

### Negative

- The watch needs the phone nearby. A parent who leaves the phone in another room and walks away
  with only the watch queues logs until they are back in range. The logs are delayed, not lost.
- A second language and toolchain (Kotlin, Gradle, Android Studio) to maintain. `Coo.Domain` rules
  cannot be shared with the watch, so it validates only trivially. The phone applies the real
  rules on receipt, and a rejected watch entry appears in the phone's "Needs attention" list
  (ADR 0002 §11).
- Sharing the package name and signing key couples the two release trains to one keystore and one
  Play listing.
- Tiles and complications refresh on the system's schedule, so "time since last feed" can lag by a
  minute or so.
- Wear OS device testing needs a physical watch, or the emulator's paired-phone setup, which the
  maintainer machine does not have today.
