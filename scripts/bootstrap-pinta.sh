#!/usr/bin/env bash
#
# bootstrap-pinta.sh [--revision <git ref>]
#
# Clones the Pinta sources that the add-in is compiled against into .pinta/Pinta
# (git-ignored) and builds them. Only Pinta.Core is required to build the add-in, but
# the test scripts also start Pinta, so the whole application is built here.
#
# Override the location with PINTA_SRC=/path/to/Pinta.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pinta_src="${PINTA_SRC:-$root/.pinta/Pinta}"
revision="3.1"

while [ $# -gt 0 ]; do
	case "$1" in
		--revision|-r) revision="$2"; shift ;;
		--help|-h) sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "Unknown argument: $1" >&2; exit 2 ;;
	esac
	shift
done

if [ ! -f "$pinta_src/Pinta/Pinta.csproj" ]; then
	echo "==> cloning Pinta ($revision) into $pinta_src"
	mkdir -p "$(dirname "$pinta_src")"
	git clone --depth 1 --branch "$revision" https://github.com/PintaProject/Pinta.git "$pinta_src"
fi

echo "==> building Pinta"
dotnet build "$pinta_src/Pinta/Pinta.csproj" --nologo -v quiet

echo "==> Pinta is ready: $pinta_src/build/bin"
