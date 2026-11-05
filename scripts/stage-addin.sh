#!/usr/bin/env bash
#
# stage-addin.sh [--configuration Release] [--output <dir>]
#
# Builds the add-in and produces a clean staging directory that contains exactly what
# has to be shipped:
#
#   <stage>/TheMorbidArk.PintaExtension.BezierCurve.dll
#
# The add-in is a single file: its icons are embedded in the assembly and unpacked
# into the user's cache directory at startup. That is what keeps uninstalling clean.
# The build output directory is not used directly because referencing Pinta.Core
# drags Pinta's own icon set into it.
#
# Prints the staging directory on success.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"
stage="${root}/dist/stage"

while [ $# -gt 0 ]; do
	case "$1" in
		--configuration|-c) configuration="$2"; shift ;;
		--output|-o) stage="$2"; shift ;;
		--help|-h) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "Unknown argument: $1" >&2; exit 2 ;;
	esac
	shift
done

project="${root}/src/TheMorbidArk.PintaExtension.BezierCurve"

echo "==> building the add-in ($configuration)" >&2
dotnet build "$project/TheMorbidArk.PintaExtension.BezierCurve.csproj" -c "$configuration" --nologo -v quiet >&2

output="${project}/bin/${configuration}/net8.0"

if [ ! -f "${output}/TheMorbidArk.PintaExtension.BezierCurve.dll" ]; then
	echo "Build did not produce TheMorbidArk.PintaExtension.BezierCurve.dll" >&2
	exit 1
fi

rm -rf "${stage}"
mkdir -p "${stage}"

cp "${output}/TheMorbidArk.PintaExtension.BezierCurve.dll" "${stage}/"

echo "${stage}"
