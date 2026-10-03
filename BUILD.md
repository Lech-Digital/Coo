# Building Coo locally

Every command here was run on the maintainer machine before it was written down (Spec 002 §8).
The recipe follows the sibling apps (PurePrep, CoreChoice); the traps they hit are designed out of
this solution rather than worked around.

## Tests and the backend: the global SDK

Nothing below needs the MAUI workload.

```sh
dotnet test Coo.Tests.slnf                                   # all tests
dotnet format Coo.Tests.slnf --verify-no-changes             # formatting gate (CI runs it too)
dotnet run --project src/Backend/Coo.Api --urls http://localhost:5310
curl localhost:5310/health                                   # {"status":"ok","version":"0.1.0"}
```

`Coo.Tests.slnf` is every project except the two Android ones (`Coo.Mobile`, `Coo.Mobile.Platform`).
An architecture test keeps it complete. Do not run `dotnet test` on `Coo.slnx`: that builds the
Android projects on the global SDK and fails.

## The Android app: the user-local MAUI SDK

The `maui-android` workload is installed only in a user-local SDK:

```sh
export DOTNET_ROOT=$HOME/dotnet-maui
export PATH=$HOME/dotnet-maui:$PATH
export JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
# Android SDK: ~/android-sdk (platform android-36)
```

Restore, then build a Debug APK:

```sh
dotnet restore src/Mobile/Coo.Mobile/Coo.Mobile.csproj
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Debug -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:EmbedAssembliesIntoApk=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
```

The output is `src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk`.
`EmbedAssembliesIntoApk` puts the assemblies inside the APK. Without it a Debug build relies on Fast Deployment,
which only `dotnet build -t:Install` provides: installed with `adb install`, the app aborts at launch with
"No assemblies found … Assuming this is part of Fast Deployment" (found on the Pixel 10, 2026-10-02).

Install it on a phone with USB debugging enabled:

```sh
~/android-sdk/platform-tools/adb devices
~/android-sdk/platform-tools/adb install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
```

## A trimmed Release build with the debug screen (Spec 004 §9)

This is the build the store gets, trimmed, plus the debug screen, so the storage can be checked on a phone. It is
signed with the debug key and is never uploaded.

```sh
dotnet build src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Release -f net10.0-android --no-restore \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:AndroidPackageFormat=apk -p:CooDebugScreen=true \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
~/android-sdk/platform-tools/adb install -r src/Mobile/Coo.Mobile/bin/Release/net10.0-android/com.lechdigital.coo-Signed.apk
```

Verified on 2026-10-01, on a Pixel 10:

- The trimmer runs (`obj/Release/net10.0-android/android-arm64/linked/`).
- It reports no trimming (IL) warnings.
- The migration script survives inside the trimmed `Coo.Mobile.Infrastructure.dll`.
- The app installs, starts, and logs no exceptions.
- No `TrimmerRootAssembly` is needed, because the persistence code uses no reflection (Spec 004 §13).

Measured on the same Pixel 10 with the screen unlocked: the first launch after install, including the migrations,
took about 860 ms to the first frame (`TotalTime`). Later cold starts took 530–850 ms. Build 2 installed over build 1
kept all data, and its integrity scan ran once.

Measure the cold start with the phone **unlocked**: a locked screen distorts it (it read about 3 s).

```sh
~/android-sdk/platform-tools/adb shell am force-stop com.lechdigital.coo
~/android-sdk/platform-tools/adb shell am start -W -n com.lechdigital.coo/crc64f635aa2239ac8606.MainActivity
```

## Fonts

After every Android build, check that the fonts reached the package:

```sh
eng/verify-apk-fonts.sh src/Mobile/Coo.Mobile/bin/Release/net10.0-android/com.lechdigital.coo-Signed.apk
```

When font files are added or removed, delete `src/Mobile/Coo.Mobile/obj` and `bin` first (run
`dotnet build-server shutdown` if the delete fails). A stale `ProcessMauiFonts` stamp once shipped CoreChoice with no
fonts.

## MAUI lessons recorded while building the styles (Increment 5)

- **`CharacterSpacing` is not in pixels.** Android applies it as 0.0624 em per unit, so the design system's tracking
  (px at each size) is converted to px / size / 0.0624. Using the px value directly made the timer digits overlap.
- **The XAML source generator cannot set `StyleClass` in a style `Setter`** (`MAUIG1001: The method or operation is
  not implemented`). Number styles get the "Tabular" class in code instead (`Components/Components.cs`).
- **`{StaticResource}` across merged dictionaries** is not resolvable by the source generator when `Styles.xaml` is
  compiled on its own, so the touch-target sizes in `Styles.xaml` are literal values (48 and 64).
- **A view cannot be added to a second parent.** Rebuilding a page that re-adds a view it kept in a field crashes
  with `IllegalStateException: The specified child already has a parent`. Create views fresh on each rebuild.
- **Modal pages open in their own Android window,** so they don't get the palette's system-bar colours, and UI
  automation does not see their views. Real screens are not modal; the Increment 6 spec decides how sheets work.
- **The .NET `.gitignore` template ignores every folder named `Log/`**, so source folders with that name need the
  `!src/**/Log/` negation (it hid the Log screen's view model from CI). After adding a folder, check
  `git status --ignored`.
- **A view that rebuilds every second swallows taps.** Keep interactive views stable, and bind the text that ticks.
- **Sheets and the keyboard:** a page with input sheets sets `SafeAreaEdges` bottom to `SafeAreaRegions.SoftInput`.
  On a `Grid` inside the page it has no effect.
- **A language change must not recreate the activity:** MAUI's Shell teardown then crashes. `MainActivity` declares
  `ConfigChanges.Locale | LayoutDirection` and rebuilds the pages itself.
- **Per-app language for testing:** `adb shell cmd locale set-app-locales com.lechdigital.coo --locales pl-PL`
  (and `--locales ""` to follow the phone again).
- **Inside the `Coo.Mobile` namespace, `Application` and `Platform` name projects,** not the MAUI types. Use
  `Microsoft.Maui.Controls.Application` and `Microsoft.Maui.ApplicationModel.Platform`.

## MAUI lessons from Increment 7

- **Android 16 finishes the activity on Back without asking MAUI,** and the Shell teardown then crashes
  (`ObjectDisposedException` in `ShellFragmentContainer.OnDestroy`). `MainActivity` registers an
  `OnBackPressedCallback` that sends Back to the page tree, and moves Coo to the background when nothing is left to
  close. Pushed pages pop themselves in `OnBackButtonPressed`.
- **A view model that reacts to its own property changes runs while it loads.** Guard it, or a half-filled form
  (a `DateTime` of 0001-01-01) throws east of UTC. Tests that touch local time run in a +02:00 zone.
- **Local time comes from the `TimeProvider`'s zone only,** never `DateTimeOffset.LocalDateTime` (the machine's
  zone). The Mac is at +02:00 and CI at UTC, so a mix passes here and fails there. Before pushing, also run
  `TZ=UTC dotnet test Coo.Tests.slnf`.
- **Android's own dialogs (date and time pickers) take `colorPrimary` and `colorAccent`** from
  `Platforms/Android/Resources/values/colors.xml`, not the MAUI resources.
- **`uiautomator dump` fails while a view ticks every second,** so the tap helper finds nothing on Today with a timer
  running. Tap by coordinates there.

## The second phone: an emulator with Google Play

The Android emulator is the second phone for sync tests (Spec 011 §11). AVD `coo-second`: Pixel 9 profile,
Android 16 (API 36), Google Play image, arm64.

```sh
export JAVA_HOME=$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home
SDK=$HOME/android-sdk
yes | $SDK/cmdline-tools/latest/bin/sdkmanager --sdk_root=$SDK --licenses >/dev/null
$SDK/cmdline-tools/latest/bin/sdkmanager --sdk_root=$SDK "emulator" "system-images;android-36;google_apis_playstore;arm64-v8a"
echo no | $SDK/cmdline-tools/latest/bin/avdmanager create avd -n coo-second \
  -k "system-images;android-36;google_apis_playstore;arm64-v8a" -d pixel_9
$SDK/emulator/emulator -avd coo-second -netdelay none -netspeed full -no-snapshot-save >/tmp/coo-emulator.log 2>&1 &
$SDK/platform-tools/adb wait-for-device
until [ "$($SDK/platform-tools/adb -e shell getprop sys.boot_completed | tr -d '\r')" = "1" ]; do sleep 2; done
```

With both phones attached, `adb devices` lists the Pixel's serial and `emulator-5554`. Address one with
`adb -s emulator-5554 …` or `adb -s <serial> …` (plain `adb` refuses to guess).

The owner signs the Google account in once, in the emulator window: Settings → Passwords, passkeys & accounts → Add
account → Google (adzius.lech@gmail.com), then opens the Play Store once and accepts its terms. The agent must not
handle the password or 2-step verification. Check with `adb -s emulator-5554 shell dumpsys account | grep -c com.google`
(1 or more). After that the emulator's Credential Manager sheet offers the account like the Pixel's.

The same debug-signed APK works on both phones, because the debug key's SHA-1 is registered with the Android OAuth
client:

```sh
~/android-sdk/platform-tools/adb -s emulator-5554 install -r src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk
```

## MAUI lessons from Increment 10

- **`TapGestureRecognizer.NumberOfTapsRequired = 3` never fires on Android:** MAUI reports only single and double
  taps there. Count single taps (`TapBurst`).
- **The Credential Manager bindings are pinned** to 1.5.0 / 1.5.0 / 1.1.0.8 (Spec 010a §5); newer ones fail restore
  with MAUI 10.0.0.
- **A Debug APK installed with `adb install` needs `-p:EmbedAssembliesIntoApk=true`** (see "The Android app" above).
- **.NET for Android signs Debug builds with `~/Library/Application Support/Xamarin/Mono for Android/debug.keystore`,**
  not `~/.android/debug.keystore`. Register that key's SHA-1 with the Google Android client.

## MAUI lessons from Increment 11

- **The Pixel 10 reports a USB-C PD charger as AC,** so "Stay awake" with USB only does not apply. Use
  `adb shell svc power stayon true` and restore with `adb shell settings put global stay_on_while_plugged_in 0`.
- **`adb shell cmd connectivity airplane-mode enable|disable`** works on the Pixel 10 and on the emulator; use it for
  offline tests.
- **`adb install -r` does not relaunch the app,** and `monkey` can fail silently on Android 16. Launch with
  `adb shell am start -n com.lechdigital.coo/<activity>`; resolve the activity with
  `adb shell cmd package resolve-activity --brief com.lechdigital.coo`.
- **The modal consent and bring-over pages are visible to uiautomator** (unlike the Increment 5 note above), so `uiautomator dump` works and taps by bounds
  hit them.
- **The debug screen opens with three taps on the "coo" wordmark** (day) **or the title** (night).

## The backend (Increment 8)

- **Deploying:** `DEPLOY.md`. Its tests and the deploy interlock need **Docker Desktop** running
  (Testcontainers, PostgreSQL 17).
- **A new migration:**

  ```sh
  dotnet tool restore
  dotnet tool run dotnet-ef migrations add <Name> --project src/Backend/Coo.Api.Infrastructure \
    --startup-project src/Backend/Coo.Api.Infrastructure --output-dir Persistence/Migrations \
    --namespace Coo.Api.Infrastructure.Persistence.Migrations
  ```

  - Then make the generated classes `internal`: the architecture test requires it, and EF finds internal migrations.
  - With `--namespace`, EF writes the model snapshot into a folder named after the namespace (`Coo/Api/...`). Move it
    into `Persistence/Migrations`.
- **One EF Core patch everywhere.** Npgsql's provider brings an older EF Core (10.0.4) than the design package
  (10.0.12), and the mismatch is warning MSB3277, which is an error here. `Microsoft.EntityFrameworkCore.Relational` is
  pinned in `Directory.Packages.props`.
- **Npgsql tries Kerberos** and logs `libgssapi_krb5.so.2` in the slim ASP.NET image. The connection string sets
  `Gss Encryption Mode=Disable`.
- **Test projects** are those whose names end in `.Tests` or `.IntegrationTests` (`Directory.Build.props`).

## Traps that are designed out

| Sibling-app failure | Why it cannot happen here |
|---|---|
| `NETSDK1147` (the iOS workload is missing) | Every project single-targets. An architecture test fails on any `TargetFrameworks` element or any non-Android platform TFM. |
| `NETSDK1005` (`-p:TargetFrameworks=` leaking into libraries) | Nothing needs that property, because the host already targets only `net10.0-android`. Never pass it. |
| `XA0035` (Release appends the host RID) | Always pass `-p:UseDefaultPublishRuntimeIdentifier=false`, as above. |
| An unreachable corporate NuGet feed | `NuGet.config` clears every source except nuget.org. |
| `dotnet test` at the root building the MAUI project | Tests run on `Coo.Tests.slnf`, never on the full solution. |

## Build guards

- Warnings are errors, in every project.
- The banned APIs in `BannedSymbols.txt` are build errors: the real clock, `Guid.NewGuid`,
  blocking on tasks, and `Random`.
- `TimeProvider.System` is banned everywhere except the two hosts, through
  `eng/banned-non-host/BannedSymbols.txt`. The analyzer reads only files named exactly
  `BannedSymbols.txt`, which is why that file sits in its own folder.
- `MauiVersion` is not used for `Microsoft.Maui.Controls`. Central package management pins it to
  `10.0.0`, the installed workload's version. Bump both together.
