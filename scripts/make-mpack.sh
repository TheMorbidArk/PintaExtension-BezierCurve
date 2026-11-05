#!/usr/bin/env bash
#
# make-mpack.sh [--configuration Release]
#
# Builds the add-in and creates a Mono.Addins package (.mpack) that can be installed
# with Pinta's own "Add-ins -> Install from file" dialog.
#
# The package contains a single file (the add-in assembly) so that Mono.Addins can
# remove everything again on uninstall. Icons are embedded in the assembly.
#
# Output: dist/PintaExtension-BezierCurve-<version>.mpack
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"

while [ $# -gt 0 ]; do
	case "$1" in
		--configuration|-c) configuration="$2"; shift ;;
		--help|-h) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "Unknown argument: $1" >&2; exit 2 ;;
	esac
	shift
done

version="$(sed -n 's/.*<Addin id="TheMorbidArk.PintaExtension.BezierCurve" version="\([^"]*\)".*/\1/p' "${root}/src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml" | head -1)"

# A package whose version was not bumped looks installed but changes nothing, because
# Mono.Addins treats "same id + same version" as already installed. Refuse to build one.
"${root}/scripts/check-version.sh"

stage="$("${root}/scripts/stage-addin.sh" --configuration "$configuration")"

dist="${root}/dist"
work="${dist}/.work"

# Drop packages from earlier versions so that only the current one is left around.
rm -f "${dist}"/PintaExtension-BezierCurve-*.mpack
rm -rf "$work"
mkdir -p "$work/dll" "$work/packages"

# Mono.Addins generates the package manifest from the assembly.
cp "${stage}/TheMorbidArk.PintaExtension.BezierCurve.dll" "${work}/dll/"

dotnet build "${root}/tools/mpack/mpack.csproj" --nologo -v quiet
mapfile -t created < <(dotnet "${root}/tools/mpack/bin/Debug/net8.0/mpack.dll" build "${work}/packages" "${work}/dll" 2>/dev/null | sed -n 's/^PACKAGE: //p')

if [ ${#created[@]} -eq 0 ]; then
	echo "Packaging failed (no .mpack was produced)" >&2
	exit 1
fi

target="${dist}/PintaExtension-BezierCurve-${version}.mpack"
cp "${created[0]}" "$target"

rm -rf "$work"

echo
echo "package : $target"
echo "files   :"
unzip -Z1 "$target" | sed 's/^/          /'
echo
echo "Install with Pinta: A_dd-ins -> Add-ins -> Install from file"
echo
echo "Note: Pinta will only replace an older version, so this package carries $version."
echo "      Bump the version in src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml before building the next one."
