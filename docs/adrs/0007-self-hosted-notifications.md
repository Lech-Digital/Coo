# 7. Self-hosted notifications and background sync

Date: 2026-09-29

Status: Accepted

## Context

Coo needs notifications, and must deliver them without Firebase Cloud Messaging (FCM). Everything
runs on the Hetzner host (ADR 0003). The notifications Coo actually needs fall into three groups,
and the groups need very different delivery:

| Group | Examples | Where the trigger comes from |
|---|---|---|
| **Reminders** | "Nap window opens in ~15 min" (ADR 0008); "3 h since the last feeding"; "Feeding timer still running (45 min)"; a pumping reminder | A rule applied to the care log, which is already on the phone |
| **Family activity** | "Anna logged a feeding"; "Nanny started a nap" | Another caregiver's device |
| **Account events** | Payment problem (Play grace period); invitation accepted; removed from a household | The server |

Two facts about Android shape everything:

- **No app keeps a connection open in the background.** Doze and App Standby suspend network access
  for background apps. SignalR (ADR 0002 §8), server-sent events and raw WebSockets therefore all
  stop when the app is backgrounded. A foreground service could hold one open, but Android 14+
  requires a declared service type, limits `dataSync` services to 6 hours a day (Android 15), and
  shows a permanent notification. It is not a viable basis for a baby tracker. The only OS-approved
  way to wake an app instantly is a push distributor holding **one** connection on behalf of all
  apps. On most phones that distributor is FCM. **UnifiedPush** is an open standard for the same
  role, with self-hostable servers such as ntfy.
- **The OS schedules local work reliably.** `AlarmManager` and WorkManager can fire notifications
  and background syncs without any server.

Reminders are the most valuable group. They are also the group that needs no push at all, because
the phone already has the data and the sleep predictor runs on the device (ADR 0001).

A baby tracker also has a notification failure mode most apps do not: **waking a sleeping parent.**
A partner's phone buzzing at 03:00 because the other partner logged a feeding is a bug, not a
feature.

## Decision

### 1. Three tiers, all self-hosted

**Tier 1 — Local notifications (v1).** Reminders are computed and scheduled on the phone, and need
no network at all. This is the default, and it covers the reminders group completely.

**Tier 2 — Periodic background sync (v1).** A WorkManager periodic job, with a network constraint,
runs the ADR 0002 sync cycle about every 15 minutes, the minimum WorkManager allows. Doze may
stretch that interval. After each background sync, Tier 1 reschedules.

So "3 h since the last feeding" stays correct when the *partner* logged that feeding, at worst
about 15 minutes late. Family-activity notifications in this tier are composed from what the sync
brought in, and are equally delayed.

**Tier 3 — Instant family alerts through UnifiedPush (opt-in, after launch).** For households that
want family activity immediately, the app registers with a UnifiedPush distributor installed on
the phone, such as the ntfy app.

- The server sends a **doorbell** to the distributor's endpoint: `{ householdId, seq }`, the same
  payload as the SignalR doorbell (ADR 0002 §8). It is encrypted with Web Push message encryption
  (RFC 8291).
- The phone wakes, syncs, and composes the notification from its local database.
- Coo runs its own ntfy server as a `coo-ntfy` container, on the same stack and behind the same
  Caddy (ADR 0001 §5). A distributor configured to use a different server works too, because it
  only ever carries an encrypted doorbell.

This tier needs an extra app installed, so it is an opt-in setting ("Instant family alerts"), never
a requirement.

Tier 3 is designed now and built after launch. The server's doorbell port (`IHouseholdDoorbell`)
has a SignalR adapter from v1. The UnifiedPush adapter is added later without changing anything
else.

### 2. Push carries no data, ever

Every tier composes notification text **on the device, from the local database**, in the device's
language (ADR 0006). A push is only ever a doorbell. As a result:

- no care data or names pass through any push server, ours or anyone else's;
- a lost, duplicated or delayed doorbell cannot produce a wrong notification, because it only
  triggers a sync.

### 3. Reminders are a pure plan, reconciled with the OS

- `ReminderPlanner.Plan(careLog, activeSessions, prediction, settings, now)` returns the complete set
  of notifications that *should* be scheduled. It lives in `Coo.Mobile.Application/Notifications`,
  is pure, and is unit-tested with an injected `now` (ADR 0004 §10).
- The platform adapter diffs that set against what is currently scheduled, then schedules, updates
  or cancels. Each planned notification has a stable ID derived from its rule and subject, so
  planning twice changes nothing.
- The plan is recomputed after every local write, after every sync cycle (foreground or
  background), and after each of these OS events:
  - boot (`RECEIVE_BOOT_COMPLETED`);
  - a time or time-zone change;
  - an app update;
  - a language change.
- Background code resolves the language itself (PurePrep's `ResolveCulture` lesson, ADR 0006 §1).
- **Timing has two precision classes.**
  - **Standard** (feeding, pumping and timer reminders) uses `setAndAllowWhileIdle`. It fires during
    Doze, but inexactly. On a phone lying still in deep Doze, and more so on aggressive OEM builds,
    the delay can reach **15–30 minutes**. That is acceptable for "3 h since the last feeding".
  - **High precision** (the nap window, ADR 0008) uses `setExactAndAllowWhileIdle`, which needs the
    `SCHEDULE_EXACT_ALARM` special access. Android 14+ does not grant it to new installs by default.
    `USE_EXACT_ALARM` is reserved by Play policy for alarm-clock and calendar apps, so Coo does not
    declare it.
- **Asking for exact alarms.** When a Premium user turns on nap-window reminders, the app shows a
  one-screen explanation: "A nap reminder that arrives 20 minutes late arrives after the window has
  closed. Allow precise alarms so Coo can remind you on time." It then opens the system's
  exact-alarm settings. The app listens for
  `ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED`, and on every reschedule it checks
  `canScheduleExactAlarms()`.
- **Without the permission**, nap reminders fall back to standard precision, with two mitigations:
  - they are scheduled at the window start minus the lead time (default 15 minutes, ADR 0008 §7), so
    a delay eats into the lead time before it eats into the window;
  - their text is composed **when the notification fires**, not when it is scheduled. A late
    reminder therefore says "Nap window open since 14:10", and never shows a stale "opens in 15 min".

  The nap card in the app shows a small "Reminders may be late" hint, with a link to grant the
  permission.

### 4. Quiet by default

- **Permission.** `POST_NOTIFICATIONS` (Android 13+) is requested the first time the person turns on
  a reminder, never at launch. This is the same rule as the microphone permission (ADR 0005 §2).
- **Family activity is off by default** on every device, and can be turned on per caregiver ("Tell
  me when Nanny logs"). Its notifications respect **quiet hours**, a per-device setting that
  defaults to 22:00–07:00. During quiet hours, family activity is not delivered; it is summarised
  in the app instead.
- **Reminders the person turned on themselves** are delivered during quiet hours only if they
  enabled "during the night" for that reminder. A parent doing night feeds may want the 3-hour
  reminder at 02:00. Most do not want it by default.
- All notification settings are per device and are not synced. Two partners need different nights.
- **Channels:** `reminders`, `sleep`, `family`, `account`. Each can be muted from Android's own
  settings.
- **Lock screen.** Notifications use `VISIBILITY_PRIVATE`, with a public version ("Coo reminder").
  Details of a child's care do not appear on a locked screen unless the person chooses that in the
  system settings.

### 5. Account events go to an in-app inbox

Server-originated events are neither urgent nor time-sensitive. They are delivered through sync,
not through push. The ADR 0002 pull response gains an `inbox` section, as an additive contract
change. The app shows a banner or badge, and posts a local notification on the `account` channel
when the message arrives in a background sync.

| Inbox kind | Shown to |
|---|---|
| `payment-issue` (subscription in grace period or on hold, ADR 0004) | The payer. Google Play also emails them. |
| `invite-accepted` | The inviter |
| `member-removed` | Remaining Owner and Parents |
| `client-deprecated` (ADR 0002 §11) | The device concerned |

Inbox messages are codes plus parameters, localized on the device (ADR 0006 §2).

### 6. Where it lives

| Concern | Project |
|---|---|
| `ReminderPlanner`, reminder rules, quiet-hours logic, inbox handling; ports `ILocalNotificationScheduler`, `IBackgroundSyncScheduler`, `IPushRegistration` | `Coo.Mobile.Application/Notifications` |
| `AlarmManager` scheduling, notification channels, boot, time and update receivers, WorkManager job (`Xamarin.AndroidX.Work.Runtime`), UnifiedPush connector (later) | `Coo.Mobile.Platform/Notifications` |
| Inbox and push-registration DTOs, doorbell payload | `Coo.Contracts/Sync` |
| Inbox storage and projection into pull responses; port `IHouseholdDoorbell` | `Coo.Api.Application/Notifications` |
| SignalR doorbell adapter (v1); UnifiedPush / Web Push adapter (later) | `Coo.Api.Infrastructure/Push` |
| `coo-ntfy` container (later) | `deploy/docker-compose.prod.yml` |

## Consequences

### Positive

- There is no dependency on Firebase or any Google push service. The server side of every tier runs
  on the Hetzner host.
- The most valuable notifications, reminders and the nap window, work with no network at all,
  because they are computed on the phone.
- Push carries no data, so no push server ever sees a child's care data. A broken push channel can
  delay a notification but never make it wrong.
- Reminder scheduling is a pure, tested plan, with a thin OS adapter.
- Parents are not woken by default. Quiet hours and off-by-default family alerts encode the
  product's 3 AM promise.

### Negative

- Without Tier 3, family-activity notifications are late by the background sync interval, about
  15 minutes and longer under Doze. For most families that is fine; for some it is not what
  "real-time family sync" suggested.
- Tier 3 needs a separate distributor app. Few non-technical parents will install one, so instant
  family alerts will be a niche feature.
- Inexact alarms can fire a few minutes late in Doze. Precise reminders need a permission that many
  users will not grant.
- Periodic background sync costs some battery and data, even when nothing changed. That is bounded
  by WorkManager's own batching.
- Some manufacturers (Xiaomi, Huawei, Samsung's battery optimisation) kill background work
  aggressively beyond what stock Android does. Reminders may then be late, or missed until the app
  is opened, and the app needs a help page on exempting Coo from battery optimisation.
- Running `coo-ntfy` later adds another public endpoint to secure and keep updated on the shared
  host.
