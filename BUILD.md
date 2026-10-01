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
  -p:UseDefaultPublishRuntimeIdentifier=false \
  -p:AndroidSdkDirectory=$HOME/android-sdk -p:JavaSdkDirectory=$JAVA_HOME
```

The output is `src/Mobile/Coo.Mobile/bin/Debug/net10.0-android/com.lechdigital.coo-Signed.apk`.

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
- **A view that rebuilds every second swallows taps.** Keep interactive views stable, and bind the text that ticks.
- **Sheets and the keyboard:** a page with input sheets sets `SafeAreaEdges` bottom to `SafeAreaRegions.SoftInput`.
  On a `Grid` inside the page it has no effect.
- **A language change must not recreate the activity:** MAUI's Shell teardown then crashes. `MainActivity` declares
  `ConfigChanges.Locale | LayoutDirection` and rebuilds the pages itself.
- **Per-app language for testing:** `adb shell cmd locale set-app-locales com.lechdigital.coo --locales pl-PL`
  (and `--locales ""` to follow the phone again).
- **Inside the `Coo.Mobile` namespace, `Application` and `Platform` name projects,** not the MAUI types. Use
  `Microsoft.Maui.Controls.Application` and `Microsoft.Maui.ApplicationModel.Platform`.

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
