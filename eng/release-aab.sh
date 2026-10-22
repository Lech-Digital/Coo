#!/usr/bin/env bash
# Builds the Play upload: a signed, trimmed Release AAB of Coo (Increment 18).
# The upload key is ~/keystores/coo-upload.jks; its password is read from the macOS Keychain item "coo-upload-keystore"
# and handed to MSBuild through an environment variable, so it never appears in a command line or log.
# Before running: bump ApplicationDisplayVersion and ApplicationVersion and write CHANGELOG.md (see its header).
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_ROOT="$HOME/dotnet-maui" PATH="$HOME/dotnet-maui:$PATH"
export JAVA_HOME="$HOME/Library/Java/JavaVirtualMachines/jdk-17.0.20+8/Contents/Home"
COO_UPLOAD_PASS="$(security find-generic-password -s coo-upload-keystore -w)"
export COO_UPLOAD_PASS

dotnet publish src/Mobile/Coo.Mobile/Coo.Mobile.csproj -c Release -f net10.0-android \
  -p:UseDefaultPublishRuntimeIdentifier=false -p:AndroidPackageFormat=aab \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$HOME/keystores/coo-upload.jks" -p:AndroidSigningKeyAlias=coo-upload \
  -p:AndroidSigningStorePass=env:COO_UPLOAD_PASS -p:AndroidSigningKeyPass=env:COO_UPLOAD_PASS \
  -p:AndroidSdkDirectory="$HOME/android-sdk" -p:JavaSdkDirectory="$JAVA_HOME"

version=$(sed -n 's:.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*:\1:p' src/Mobile/Coo.Mobile/Coo.Mobile.csproj)
code=$(sed -n 's:.*<ApplicationVersion>\(.*\)</ApplicationVersion>.*:\1:p' src/Mobile/Coo.Mobile/Coo.Mobile.csproj)
mkdir -p artifacts
out="artifacts/coo-$version-$code.aab"
cp src/Mobile/Coo.Mobile/bin/Release/net10.0-android/publish/com.lechdigital.coo-Signed.aab "$out"
"$JAVA_HOME/bin/jarsigner" -verify "$out" >/dev/null && echo "Signed: $out"
