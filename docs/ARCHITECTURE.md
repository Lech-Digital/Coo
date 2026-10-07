# Architecture

CooApp is built with a focus on privacy, offline capabilities, and cross-platform extensibility (primarily Android and Wear OS for now). This document summarizes the core architectural decisions that shape the application.

## 1. Solution Structure and Layering
The solution is split into distinct projects to enforce layering:
* `Coo.Domain`: Pure domain logic, entities, value objects, and invariants. No dependencies on external frameworks or databases.
* `Coo.Mobile` & `Coo.Mobile.Infrastructure`: .NET MAUI application and its local persistence layer (SQLite).
* `Coo.Api` & `Coo.Api.Infrastructure`: ASP.NET Core backend and its persistence layer (PostgreSQL).
* `Coo.Codecs`: Shared JSON parsing logic and DTOs to serialize/deserialize entry details for both local and remote storage.

## 2. Offline-First Sync Protocol
To allow users to log entries in low-connectivity environments (e.g., a dark nursery), the app is entirely offline-first.
* **Sync Strategy**: Last-writer-wins per record. Every record acts as its own aggregate.
* **Clocks**: The system uses a **Hybrid Logical Clock (HLC)** to version mutations. Wall-clock time is combined with a monotonic counter and the device ID to ensure deterministic ordering.
* **Tombstones**: Deletions write a tombstone rather than wiping the record, ensuring the deletion propagates to other devices.
* **Transport**: A SignalR "doorbell" alerts connected devices to pull changes. Pushing and pulling happen via REST.

## 3. Extensibility & Entry Kinds
To avoid expensive, rigid database migrations every time a new logging feature is added (e.g., tracking medication or temperature), the domain leverages an extensible JSON model.
* **Single Table**: There is only one `CareEntry` entity and table.
* **JSON Details**: The specific properties of an entry (e.g., breast side duration, bottle milk volume) are serialized as JSON in a `Details` column.
* **Forward Compatibility**: Older clients that receive entries with unknown kinds keep the data opaque and untouched. If they edit the entry, the unknown JSON properties are preserved and merged back.

## 4. Identity & Household Membership
* **No Passwords**: The app uses Google Credential Manager for sign-in.
* **Households**: Entries belong to a `HouseholdId`, not a user. Users are invited to households via short-lived codes. 
* **Guest to Member**: Entries logged before signing in belong to `ThisDevicesPerson`. Upon first sign-in, these entries are rewritten to the new user's ID.

## 5. Billing & Premium Features (Future)
* Core logging features are free. Premium features (like AI-assisted logging and sleep predictions) will be monetized via Google Play Billing.
* Subscriptions will grant household-wide access to premium features (managed by the payer's store platform).

## 6. AI & Assistants (Future)
* The app will support logging via voice and free text.
* To protect privacy, voice parsing will remain on-device where possible, and text sent to the Gemini API will be heavily pseudonymized (e.g., names swapped out) before transmission.
