#!/usr/bin/env bash
#
# dev-install.sh [--with-smoke-test] [--configuration Debug|Release]
#
# Builds the add-in and copies it next to Pinta's executable, which is the only place
# Pinta picks up debug add-ins from (the ~/.config/Pinta/addins folder is the
# Mono.Addins registry and is not scanned).
#
# Set PINTA_BIN to point at another Pinta installation.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Debug"
with_smoke_test="no"

while [ $# -gt 0 ]; do
	case "$1" in
		--with-smoke-test) with_smoke_test="yes" ;;
		--configuration|-c) configuration="$2"; shift ;;
		--help|-h) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "Unknown argument: $1" >&2; exit 2 ;;
	esac
	shift
done

pinta_src="${PINTA_SRC:-$root/.pinta/Pinta}"
pinta_bin="${PINTA_BIN:-$pinta_src/build/bin}"

if [ ! -f "$pinta_bin/Pinta.dll" ]; then
	echo "Pinta was not found in: $pinta_bin" >&2
	echo "Build it first:  $root/scripts/bootstrap-pinta.sh" >&2
	echo "or set PINTA_SRC / PINTA_BIN to an existing Pinta checkout / installation." >&2
	exit 1
fi

stage="$("$root/scripts/stage-addin.sh" --configuration "$configuration")"

echo "==> installing into $pinta_bin"
cp "$stage/TheMorbidArk.PintaExtension.BezierCurve.dll" "$pinta_bin/"

if [ "$with_smoke_test" = "yes" ]; then
	dotnet build "$root/src/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest.csproj" -c "$configuration" --nologo -v quiet
	cp "$root/src/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest/bin/$configuration/net8.0/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest.dll" "$pinta_bin/"
	echo "    + smoke test add-in"
fi

echo "==> done. Restart Pinta; the Curve tool is in the toolbox (shortcut: V)."
