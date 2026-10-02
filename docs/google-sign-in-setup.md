# Setting up Sign in with Google

Coo signs people in with Google through Android's Credential Manager (ADR 0003 §2). The phone gets a Google ID token,
and Coo's server checks it (ADR 0003 §5). For that, Google needs to know about Coo: one Google Cloud project with two
OAuth clients.

You need to do this once. It takes about 20 minutes. New clients can take a few minutes, sometimes a few hours,
before Google accepts them.

**Use the Lech Digital organisation**, the publisher on Coo's Play developer profile, with the account you use for
it. Never an employer's organisation: Coo has nothing to do with any employer (see the handover's ground rules).
Keeping the project in the publisher's organisation helps at release, when Google checks Coo's brand against
`lechdigital.nl` (owner's decision, 2026-10-02).

---

## 1. Create the project

1. Open the [Google Cloud console](https://console.cloud.google.com/) signed in with your Lech Digital account.
2. In the project picker at the top, choose **New project**.
   - Project name: `Coo`
   - Organisation / location: **Lech Digital** (check the name: it must not be an employer's organisation)
3. Press **Create**, then select the new project in the picker. Check the picker says **Coo** before every step
   below.

No API needs to be enabled. Sign in with Google works without one.

## 2. Set up the Google Auth Platform (the consent screen)

1. Open **Google Auth Platform** from the menu (☰). Some consoles still call it **APIs & Services → OAuth consent
   screen**. If it offers **Get started**, press it.
2. **App information**
   - App name: `Coo`
   - User support email: an address of yours that users may see, e.g. andrzej@lechdigital.nl (Coo's feedback
     address, Spec 009)
3. **Audience:** choose **External**, even though the project is in an organisation. **Internal** would allow only
   accounts inside Lech Digital, so your girlfriend's Gmail account could not sign in.
4. **Contact information:** your Lech Digital address.
5. Accept the policy, then **Create**.
6. Open **Audience** in the left menu:
   - Publishing status stays **Testing**. Only test users can sign in, which is what we want until the release.
   - Under **Test users**, press **Add users** and add **your Gmail address and your girlfriend's**. Anyone not on
     this list gets an error at sign-in.
7. Open **Data access**. Add nothing. Coo asks only for the basics (`openid`, `email`, `profile`), and those need no
   review.
8. **Branding:** leave the logo and the app domain empty for now. A logo would trigger a review. Both get filled in
   before the release (Increment 18).

## 3. Create the Web client (what the server checks)

1. **Google Auth Platform → Clients → Create client.**
2. Application type: **Web application**.
3. Name: `Coo server`.
4. Leave **Authorised JavaScript origins** and **Authorised redirect URIs** empty.
5. Press **Create**. A dialog shows a **Client ID** (ends in `.apps.googleusercontent.com`) and a **Client secret**.
   - Copy the **Client ID** into section 6 below.
   - **Do not copy the client secret anywhere.** Coo never uses it. Close the dialog.

## 4. Create the Android client (proves the app is really Coo)

1. **Clients → Create client** again.
2. Application type: **Android**.
3. Name: `Coo Android (debug)`.
4. Package name: `com.lechdigital.coo`
5. SHA-1 certificate fingerprint, exactly:

   ```text
   21:BE:45:15:58:84:AF:13:31:FC:7A:9A:81:9E:DF:78:92:9A:83:4D
   ```

   This is the key that signs the Coo builds made on your Mac. It comes from
   `~/Library/Application Support/Xamarin/Mono for Android/debug.keystore`. It is **not** the one in
   `~/.android/debug.keystore`, which .NET for Android does not use. It was checked against the APK on
   2026-10-02 with `apksigner verify --print-certs`.
6. Leave **custom URI scheme** off. Press **Create**, and copy the **Client ID** into section 6.

The app does not use the Android client's ID in its code. Google matches the package name and the signing key
behind the scenes. A wrong SHA-1 shows on the phone as "Developer console is not set up correctly" or "No
credentials available".

## 5. Later: before the first Play build (not now)

Play signs released builds with its own key, so sign-in that works on your Mac would fail for everyone who installs
from Play (ADR 0003 §9). Before the first closed test:

1. Play Console → Coo → **Test and release → App integrity → App signing**. Copy the SHA-1 of the **app signing key**,
   and of the **upload key**.
2. Create one more **Android** client per key, as in section 4: `Coo Android (Play signing)` and
   `Coo Android (upload)`. Same package name, each with its own SHA-1.
3. Add both SHA-1 values to section 6.

## 6. Paste here

Client IDs are not secrets: they are built into the app, and this repository is private. **Never paste a client
secret, a password or a key file here.**

```text
Google Cloud project ID:         <paste, e.g. coo-123456>
Web client ID (Coo server):      <paste>.apps.googleusercontent.com
Android client ID (debug):       <paste>.apps.googleusercontent.com
Test users added:                <yes / no — the addresses themselves need not be written here>
Done on (date):                  <paste>
```

Later, before Play (section 5):

```text
Play app signing key SHA-1:      <paste>
Upload key SHA-1:                <paste>
Android client ID (Play signing): <paste>.apps.googleusercontent.com
Android client ID (upload):      <paste>.apps.googleusercontent.com
```

When section 6 is filled in, tell Claude. The Web client ID goes into the app (as the server client ID that
Credential Manager asks for) and into the server's configuration (as the `aud` it checks). The server refuses to
start in Production without it (ADR 0003 §5).
