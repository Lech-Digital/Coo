# Spec 010a — Spike: Sign in with Google through Credential Manager

Date: 2026-10-02

Status: Draft

Depends on: ADR 0003 §2 and §10 (the spike it requires before sign-in is built), `docs/google-sign-in-setup.md`
(the Google Cloud project and its two OAuth clients, done by the owner).

Delivered by: **Increment 10a** (`docs/ROADMAP.md`), before Increment 10.

## 1. The question

Do the .NET bindings for Android's Credential Manager and Google Identity
(`Xamarin.AndroidX.Credentials`, `Xamarin.GoogleAndroid.Libraries.Identity.GoogleId`) give Coo a Google **ID token
carrying our nonce**, in a **trimmed Release build**, on the owner's Pixel 10?

ADR 0003 names these bindings as the one unproven piece of sign-in. If they work, Increment 10 uses them. If they do
not, it uses the fallback: MAUI `WebAuthenticator` with the OAuth authorization-code flow and PKCE, in a Custom Tab.
Both sit behind the same `ISignInProvider` port, so Spec 010 does not change either way.

## 2. The probe

A **throwaway** button on the debug screen (Spec 004 §9, only in builds with `-p:CooDebugScreen=true`):

1. It makes a random nonce on the phone. The server's nonce endpoint comes in Increment 10; for the spike the phone's
   own nonce is enough to prove it travels into the token.
2. It asks Credential Manager for a Google ID token: `GetGoogleIdOption` with the **Web client ID** as the server
   client ID, the nonce, and `filterByAuthorizedAccounts = false`. If the native sheet is not offered,
   `GetSignInWithGoogleOption` is tried as well.
3. It decodes the token's payload, **without verifying it**, and shows: `iss`, `aud`, `sub` (shortened), `email`,
   `email_verified`, `nonce`, `exp`. A failure shows the exception type and message.

Nothing is sent to a server and nothing is stored. The token is not logged.

## 3. Pass and fail

**It passes** when, in a **trimmed Release** build (`-c Release -p:CooDebugScreen=true`, signed with the debug key)
installed on the Pixel 10:

- the native account sheet appears, and picking the owner's account returns an ID token;
- `aud` is the Web client ID, `nonce` is the one sent, and `email_verified` is true;
- it works a second time after a forced stop, and cancelling the sheet returns cleanly (no crash);
- the trimmed build needed **no** blanket `TrimmerRootAssembly` for the whole bindings. A few targeted
  `[DynamicDependency]` attributes or linker descriptors are fine, and get recorded.

**It fails** when any of those cannot be made to hold in about half a day of effort. Then the probe is repeated with
`WebAuthenticator` and PKCE, to confirm the fallback gets an ID token with the nonce instead.

## 4. What it leaves behind

- **Findings** in §5 below: the package versions, the trimming settings that were needed, the error codes seen and
  what caused them (for example a wrong SHA-1).
- **No code.** The debug-screen button and any spike code are removed before Increment 10 starts. Increment 10
  writes the real adapter, test-first, from the findings.

## 5. Findings

(Filled in by the spike.)
