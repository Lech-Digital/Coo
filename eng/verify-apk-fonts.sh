#!/bin/sh
# Fails unless every Figtree weight is inside the APK (Spec 005 §4.1). Guards against the stale
# ProcessMauiFonts stamp that once shipped CoreChoice with no fonts. Usage: eng/verify-apk-fonts.sh <path.apk|.aab>
set -eu
package="${1:?usage: eng/verify-apk-fonts.sh <path.apk|.aab>}"
listing=$(unzip -l "$package")
missing=0
for weight in Light Regular Medium SemiBold Bold; do
  if ! printf '%s\n' "$listing" | grep -q "Figtree-$weight.ttf"; then
    echo "MISSING: Figtree-$weight.ttf" >&2
    missing=1
  fi
done
if [ "$missing" -ne 0 ]; then
  echo "Fonts are missing from $package. Clean obj/ and rebuild." >&2
  exit 1
fi
echo "All five Figtree weights are in $package."
