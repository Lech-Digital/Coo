# Spec 010a — Spike: Sign in with Google through Credential Manager

Date: 2026-10-02

Status: Done (2026-10-02): **passed** — Credential Manager is the sign-in path for Increment 10

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

Run on 2026-10-02 on the owner's Pixel 10, in a trimmed Release build (`-c Release -p:CooDebugScreen=true`, signed
with the debug key, SHA-1 `21:BE:45:…:83:4D`), against the Google Cloud project `coo-app-510411`.

**Result: passed.** Increment 10 uses Credential Manager; the `WebAuthenticator` fallback is not needed.

| Check | Result |
|---|---|
| The native account sheet ("Choose an account for Coo") | Appears, with `GetGoogleIdOption` (`filterByAuthorizedAccounts = false`) |
| ID token after picking the account | `iss = https://accounts.google.com`, `aud` = the Web client ID, `email_verified = true`, **our nonce in the token** |
| Again after a forced stop | Passes (second token, new nonce) |
| Cancelling the sheet (Back) | `androidx.credentials.exceptions.GetCredentialCancellationException` ("User cancelled the selector") through `OnError`; no crash |
| Trimming | Built and ran with **no** trim warnings and **no** linker settings at all |

**Package versions.** The newest bindings do not work with MAUI 10.0.0. `Xamarin.AndroidX.Credentials` 1.6.0.1 and
the 1.5.0.2+ line pull AndroidX libraries newer than the ones MAUI 10.0.0 pins (`Activity`, `Fragment`,
`Lifecycle.LiveData.Core`, `Collection.Jvm`), and NuGet's `NU1608` (an error here, with warnings as errors) refuses
the mix. Each `PlayServicesAuth` version also needs a minimum `GoogleId` version (`NU1605`). The set that resolves:

| Package | Version |
|---|---|
| `Xamarin.AndroidX.Credentials` | **1.5.0** |
| `Xamarin.AndroidX.Credentials.PlayServicesAuth` | **1.5.0** (Google's account sheet needs it) |
| `Xamarin.GoogleAndroid.Libraries.Identity.GoogleId` | **1.1.0.8** |

Revisit them when Coo moves past MAUI 10.0.0 (the MAUI upgrade also fixes dotnet/maui#32750, see CoreChoice).

**The C# surface**, for the real adapter: `CredentialManager.Create(activity)`;
`GetCredentialAsync(activity, request, cancellationSignal, ContextCompat.GetMainExecutor(activity)!, callback)` with a
`Java.Lang.Object, ICredentialManagerCallback` callback (`OnResult(Java.Lang.Object?)`, `OnError(Java.Lang.Object?)`;
the error is a Java exception object, read through `Class.Name` and `ToString()`, not castable to `Throwable`);
`GetGoogleIdOption.Builder().SetServerClientId(…).SetFilterByAuthorizedAccounts(false).SetNonce(…)`; the result is a
`CustomCredential` whose `Type` is `GoogleIdTokenCredential.TypeGoogleIdTokenCredential`, read with
`GoogleIdTokenCredential.CreateFrom(custom.Data).IdToken`. Namespaces: `AndroidX.Credentials` and
`Xamarin.GoogleAndroid.Libraries.Identity.GoogleId`. `Context.MainExecutor` needs API 28, so `ContextCompat` is used
(Coo's minimum is 26).

**Also found.** The debug screen's entry (three quick taps on the "coo" wordmark, Spec 006 Q5) did not open the debug
screen on the Pixel 10, neither from adb nor by hand. The probe ran from two buttons put on Settings instead.
Increment 10's plan checks the entry, or replaces it, since the sign-in deliverables use the debug screen.

The probe code is removed: no spike code is in the repository.
