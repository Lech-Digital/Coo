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
