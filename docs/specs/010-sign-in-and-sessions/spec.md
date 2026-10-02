# Spec 010 — Sign-in and sessions

Date: 2026-10-02

Status: Draft

Depends on: ADR 0003 (identity, sessions, households), ADR 0002 §10 (the outbox waits until there is an account),
Spec 004 (local storage, the outbox), Spec 008 (the backend, `households`), Spec 009 (Settings), Spec 010a (the
Credential Manager spike), `docs/google-sign-in-setup.md` (the Google Cloud clients).

Delivered by: **Increment 10** (`docs/ROADMAP.md`). The app is version **0.10.0** from this increment.

## 1. Purpose

Coo works fully without an account (ADR 0003 §1). Sharing with family needs one: the owner's goal is to log the same
baby with his girlfriend, each on their own phone with their own Google account. That takes three increments:

| Increment | What it adds | What you can test |
|---|---|---|
| **10 (this spec)** | Sign in with Google, sessions per device, the server takes over the phone's household, the same account on a second phone, sign-out, account deletion | Sign in on two phones with one account: the server shows one household and two device sessions |
| 11 | Sync (ADR 0002) | Two phones on one account converge, including offline edits |
| 11b | Members and invitations (ADR 0003 §6) | Your girlfriend joins with her own account; you both see each other's entries |

**Out of scope:**

| Not in this increment | Where |
|---|---|
| Sending or receiving care entries, the baby profile or settings | Increment 11 (sync) |
| Bringing a second phone's entries into the account's household, or keeping them separate | Increment 11 (it needs the household's babies from the server; Q1) |
| Invitations, roles in the UI, removing members, the member list | Increment 11b |
| Sign in with Apple, email magic links | iOS; later (ADR 0003 §2) |
| Transferring ownership | Later (ADR 0003 §6) |
| Premium | Increment 12 |

## 2. What the person sees

**An Account section at the top of Settings** (Spec 009 §2 left room for it).

- **Signed out:** one row, **Sign in with Google**, with the line "Keep your log safe and share it with family." Tapping
  it opens Android's own Google account sheet. On success the section shows the signed-in state; on cancel nothing
  changes; on failure a short message ("Sign-in didn't work. Check your connection and try again.").
- **Signed in:** the account's name and email, then two rows:
  - **Sign out**: asks "Sign out on this phone? Your log stays on this phone." On yes, the session is revoked and the
    section shows the signed-out state. **Nothing local is deleted** (ADR 0003 §4).
  - **Delete account**: opens §5.

Sign-in is offered **only here** in this increment (Q3). Invitations (11b) and Premium (12) will ask for it where they
need it.

Being signed out, offline, or having an expired session **never blocks logging** (ADR 0003 §4).

## 3. Signing in

```mermaid
sequenceDiagram
    participant App as Coo (phone)
    participant API as Coo.Api /api/auth
    participant G as Google (public keys)
    participant DB as PostgreSQL

    App->>API: POST /api/auth/nonce
    API->>DB: store nonce (hash), issued now
    API-->>App: nonce (single use, 5 min)
    App->>App: Credential Manager → Google ID token (aud = Web client, nonce)
    App->>API: POST /api/auth/google { idToken, deviceName, deviceId, provisionalHouseholdId }
    API->>G: verify the signature (cached keys)
    API->>DB: consume the nonce; find or create the user by sub; create the household if needed; create the device session
    API-->>App: { accessToken, refreshToken, user, household, outcome }
    App->>App: SecureStorage
```

### 3.1 Verification fails closed (ADR 0003 §5)

`POST /api/auth/google` issues a session only when the signature verifies against Google's keys, `iss` is Google,
`aud` is the configured Web client ID, the token has not expired, `email_verified` is true, and the nonce matches an
unused server nonce issued at most 5 minutes ago (it is consumed). Any failure, including Google's keys being
unreachable, returns `401` with the code `auth.google-token-invalid`, and nothing is created.

### 3.2 The outcomes

The response's `outcome` tells the phone which case happened:

| Case | What the server does | `outcome` | What the phone does |
|---|---|---|---|
| **New user**, the phone's household ID is unused | Creates the user and a household **with the phone's provisional household ID**, with the user as `Owner` (ADR 0003 §7) | `adopted` | Records that its household is now the account's. No local record is re-keyed. |
| **New user**, the ID is already taken (practically never: UUIDv7) | Creates the household with a new ID | `created` | Records the new household ID; Increment 11 moves the local records over before the first sync |
| **Existing user**, signing in again on the same phone | Finds the user's household, no change | `existing` | As `adopted` |
| **Existing user, another phone** (the same account on a second device) | Creates only the device session | `joined` | Keeps its own provisional household and all its entries untouched, and remembers the account's household as **to be joined at the first sync** (Increment 11, Q1). Settings shows "Signed in. Your entries on this phone join your family's log when sharing arrives." |

A user has **one household** in this increment. The tables already allow several (ADR 0003 §6), for 11b.

### 3.3 Sessions (ADR 0003 §4)

- **Access token:** a JWT, HMAC-SHA256, 15 minutes. Claims: the user ID, the device session ID, and the signing key
  ID (`kid`). No household claim: membership is checked per request.
- **Refresh token:** 256 random bits, stored only as a SHA-256 hash, bound to one device session. **It rotates on
  every use.** Presenting the previous token again is treated as theft: the device session is revoked. Its lifetime
  slides: 180 days since the session was last used.
- **On the phone:** both tokens in MAUI `SecureStorage`.
- **Refreshing:** one HTTP handler refreshes when a call gets `401` or the access token is about to expire, **one
  refresh at a time** (concurrent calls wait for it). A network failure means "try later", never "signed out". A
  rejected refresh (revoked, reused, expired) keeps all local data and shows the signed-out state with "Sign in again
  to keep sharing."
- **Signing keys:** two active key IDs are allowed, so a key can be rotated without signing anyone out.

## 4. The server

### 4.1 Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /api/auth/nonce` | none | A fresh nonce |
| `POST /api/auth/google` | none | §3 |
| `POST /api/auth/refresh` | none (the refresh token) | A new access and refresh token |
| `POST /api/auth/sign-out` | access token | Revokes this device session |
| `GET /api/me` | access token | The user, their household and role, and their device sessions (name, last used). The phone's Account section and the deliverables use it. |
| `DELETE /api/account` | access token | §5 |

Errors are codes, never server text (ADR 0006): `auth.google-token-invalid`, `auth.refresh-invalid`,
`auth.rate-limited`, `account.confirmation-required`.

**Rate limits** on `/api/auth/*`, per client address (forwarded headers are already trusted, Spec 008): 20 requests
per minute. Over it: `429` with `auth.rate-limited`.

### 4.2 Data

ASP.NET Core Identity with EF Core and Npgsql, `Guid` keys, **no passwords** (ADR 0003 §3): `AspNetUsers`,
`AspNetUserLogins` (provider `Google`, key = `sub`), and the tables Identity needs. Stored about a person: `sub`,
email, display name. No photo, no Google access token.

New tables, in a new EF migration:

| Table | Columns | Notes |
|---|---|---|
| `auth_nonces` | `nonce_hash`, `issued_at`, `consumed_at` | Rows older than a day are deleted by a daily clean-up |
| `device_sessions` | `id`, `user_id`, `device_id`, `device_name`, `refresh_hash`, `previous_refresh_hash`, `created_at`, `last_used_at`, `revoked_at` | One per phone per user. Signing in again on the same `device_id` replaces that phone's session. |
| `memberships` | `household_id`, `user_id`, `role` (`owner`, `parent`, `caregiver`), `joined_at` | Primary key (household, user) |
| `households` (existing) | adds `created_by` (the Owner's user ID) | |

`Coo.Domain/Households` gains `UserId`, `Role` and `Membership` with the role rules of ADR 0003 §6, though only
`Owner` is used until 11b.

### 4.3 Configuration and start-up

`Auth:GoogleWebClientId` and `Auth:SigningKeys` (one or two `kid` → base64 key of at least 32 bytes). **Production
refuses to start** if either is missing, as `Coo.Api` already does for the connection string (Spec 008). The choice is
a pure function with a test for the dangerous case. The values live in the server's `.env` on `coldstart-prod`, never
in the repository. The deploy (owner, with `!`) adds them.

## 5. Deleting the account

ADR 0003 §8 and Google Play's rule (an in-app path and a web path).

- **In the app:** Settings → Account → Delete account. The confirmation names what goes: "This deletes your Coo
  account and, because you are the only parent, your family's shared log for Maja. The entries on this phone stay
  here." Two buttons: **Delete account**, **Cancel**. The app calls `DELETE /api/account` with `{ "confirm": true }`.
- **On the server:** the user, their Google login, their device sessions and their memberships go. If they were the
  last parent, the household and all its care data go too. Until 11b nobody else can be a member, so the household
  always goes.
- **On the phone:** the tokens are cleared and the section shows the signed-out state. **The local log stays** on the
  phone as it was before signing in (Q2). Signing in again later starts a new account.
- **On the web:** a page at `https://lechdigital.nl/projects/coo/delete-account` (the Coo website, Spec 008) explains
  the in-app path, and gives an address to ask for deletion without the app. Its text is in English for now; the
  site's other languages come with the release. Its URL goes into the Play listing at the release.

## 6. On the phone

| Concern | Project |
|---|---|
| `SignIn`, `SignOut`, `DeleteAccount` use cases; ports `ISignInProvider` (an ID token for a nonce), `ISessionStore`, `IAuthApi`; the account state (`SignedOut`, `SignedIn`, `JoinPending`) | `Coo.Mobile.Application/Auth` |
| The HTTP client for `/api/auth` and `/api/me`; the token-refreshing `DelegatingHandler` (single flight) | `Coo.Mobile.Infrastructure/Auth` |
| The Credential Manager adapter (or the `WebAuthenticator` fallback, per Spec 010a); the `SecureStorage` session store; the device name ("Pixel 10") | `Coo.Mobile.Platform/Auth` |
| The Account section, its view model, the confirmations | `Coo.Mobile.Presentation/Settings`, `Coo.Mobile/Pages/SettingsPage.cs` |
| Strings | `eng/strings/strings.py`, seven languages |

The Web client ID and the API base URL (`https://api.coo.lechdigital.nl`) are build constants. A client ID is not a
secret (`docs/google-sign-in-setup.md` §6).

The outbox keeps filling as before. Nothing is sent until Increment 11.

## 7. Tests

| Where | What |
|---|---|
| Domain | `Membership` and the role rules (ADR 0003 §6 table). |
| Api.Application | Sign-in: each outcome of §3.2; a used, expired or unknown nonce is refused and creates nothing; refresh rotates; **a reused refresh token revokes the session**; a refresh after 180 days idle is refused; sign-out revokes; account deletion removes the user, logins, sessions, membership and the household with its entries. The Google verifier is a fake of the outbound port. |
| Api.Infrastructure (Testcontainers) | The migration applies; the Identity tables, `device_sessions`, `memberships`, `auth_nonces`; the real JWT issuer and validator round-trip with two key IDs; a token signed with an unknown key is refused. |
| Api integration (`WebApplicationFactory`) | The endpoints end to end with a fake Google verifier; `401` without a token; `429` over the rate limit; **Production refuses to start** without the client ID or the keys. |
| Mobile.Application / Infrastructure | The refreshing handler: one refresh for many concurrent `401`s; a network error keeps the session; a rejected refresh signs out and keeps local data; the outcomes of §3.2 change the account state as specified. |
| Presentation | The Account section's states and texts; the deletion confirmation names the baby. |
| Localization | Every new key in seven languages. |
| On the phone | The deliverables. |

## 8. Increment 10 — deliverables

| # | Deliverable | How you check it |
|---|---|---|
| 1 | Sign in | Settings → Sign in with Google → pick your account: the section shows your name and email |
| 2 | The household is taken over | `GET /api/me` (curl with the access token, or the debug screen) shows a household with the phone's household ID and you as Owner |
| 3 | A second phone, same account | Sign in on your girlfriend's phone with **your** account: `/api/me` shows one household and two device sessions; her phone's entries are untouched and it says they join when sharing arrives |
| 4 | Sessions refresh | Leave the app 20 minutes, come back, open Settings: still signed in, with no sign-in sheet |
| 5 | Offline | Airplane mode: logging works, Settings still shows the account |
| 6 | Sign out | Sign out: signed-out state, the log is unchanged; `/api/me` with the old token is refused |
| 7 | Delete account | Delete on one phone: the server forgets the user and the household; the phone's log is still there |
| 8 | The web page | `https://lechdigital.nl/projects/coo/delete-account` opens |
| 9 | Deployed | `https://api.coo.lechdigital.nl/health` shows 0.10.0; the server's `.env` has the client ID and keys |
| 10 | CI green | GitHub Actions |

## 9. Decisions for the owner

| # | Question | Answer or recommendation |
|---|---|---|
| Q1 | The same account on a second phone that already has entries: bring them into the account's household now, or with sync? | **With sync (Increment 11).** Bringing entries over means mapping that phone's baby to the household's baby, and the household's babies only reach the phone through sync. Until then the second phone keeps its entries untouched and says so. |
| Q2 | After deleting the account, what happens to the log on the phone? | **It stays on the phone,** as before signing in. Deleting the account is about the server's copy; wiping the phone too would destroy the only other copy without a second, separate confirmation. |
| Q3 | Where is sign-in offered in this increment? | **Only in Settings.** Invitations and Premium will ask for it where they need it. A "Sign in instead" on the first-launch setup page waits for sync, because without sync a new phone would have nothing to show. |
| Q4 | Website addresses | **`lechdigital.nl/projects/coo/…`** for the deletion page, as Spec 008 decided for the website. Spec 009's privacy link points at `coo.lechdigital.nl/privacy`, a host that does not exist (only `api.coo` has DNS): it should move to `https://lechdigital.nl/projects/coo/privacy` in this increment. |
| Q5 | Token lifetimes | **As ADR 0003:** 15 minutes for the access token, 180 days sliding for the refresh token. |
| Q6 | Spike first | **Yes, Spec 010a,** as ADR 0003 requires. If Credential Manager fails in a trimmed build, this spec still holds: only the adapter behind `ISignInProvider` changes. |
