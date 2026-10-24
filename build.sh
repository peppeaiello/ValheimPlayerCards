#!/usr/bin/env bash
# Builds the plugin and the package for Gale/Thunderstore (dist/ValheimPlayerCards-<version>.zip).
# Usage: VALHEIM_DIR=/path/to/Valheim ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

dotnet build src/ValheimPlayerCards -c Release -nologo "$@"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ValheimPlayerCards/ValheimPlayerCards.csproj)
manifest_version=$(sed -n 's/.*"version_number": "\(.*\)".*/\1/p' thunderstore/manifest.json)
if [ "$version" != "$manifest_version" ]; then
  echo "Version mismatch: csproj $version, manifest.json $manifest_version" >&2
  exit 1
fi
if ! grep -q "public const string Version = \"$version\";" src/ValheimPlayerCards/Plugin.cs; then
  echo "Version mismatch: Plugin.cs does not say $version" >&2
  exit 1
fi

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
cp thunderstore/manifest.json thunderstore/icon.png CHANGELOG.md "$stage/"
# The package page is for players and admins: leave out the section for developers.
awk '/^## Building from source/{skip=1; next} /^## /{skip=0} !skip' README.md > "$stage/README.md"
cp src/ValheimPlayerCards/bin/Release/ValheimPlayerCards.dll "$stage/"

mkdir -p dist
out="dist/ValheimPlayerCards-$version.zip"
rm -f "$out"
(cd "$stage" && zip -q -X -r - .) > "$out"
echo "Package: $out"
unzip -l "$out"
