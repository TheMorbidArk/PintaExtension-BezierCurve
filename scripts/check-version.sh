#!/usr/bin/env bash
#
# check-version.sh [--for-release] [--allow-released]
#
# Guards against building a package that Pinta will refuse to install.
#
# Mono.Addins treats "same id + same version" as already installed, so a package whose
# version was not bumped looks like it installed, but the old files stay in place - the
# user sees no change and there is nothing to tell it apart from a broken build. That is
# exactly what happened to 0.5.2: it was built, published, and did nothing.
#
# The version lives in src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml and nowhere else. This script reads it
# and refuses to continue when
#
#   * the version is not a plain MAJOR.MINOR.PATCH number,
#   * a tag for it already exists (the version was reused),
#   * --for-release is given and the version was not bumped since the last tag.
#
# --allow-released (or ALLOW_RELEASED_VERSION=1) skips the tag check. It exists for
# scripts/package-test.sh: the delivery chain has to be testable on a tree that was
# already released, and a test run never publishes anything. Never use it for a release.
#
# Exit code 0 when everything is in order, 1 otherwise.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
for_release=no
allow_released="${ALLOW_RELEASED_VERSION:-no}"

while [ $# -gt 0 ]; do
	case "$1" in
		--for-release) for_release=yes ;;
		--allow-released) allow_released=yes ;;
		--help|-h) sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "Unknown argument: $1" >&2; exit 2 ;;
	esac
	shift
done

manifest="${root}/src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml"
version="$(sed -n 's/.*<Addin id="TheMorbidArk.PintaExtension.BezierCurve" version="\([^"]*\)".*/\1/p' "$manifest" | head -1)"

fail () {
	echo "VERSION CHECK FAILED: $1" >&2
	exit 1
}

[ -n "$version" ] || fail "no version in ${manifest#$root/}"

# --- The version has to be a plain three part number ---------------------------

if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
	fail "'$version' is not a MAJOR.MINOR.PATCH number"
fi

# --- The version must not have been released before ----------------------------

tag="v${version}"

cd "$root"

if git rev-parse -q --verify "refs/tags/${tag}" >/dev/null; then
	if [ "$allow_released" != no ]; then
		echo "note: '${tag}' is already released; continuing because this is a test run" >&2
	else
		fail "'${tag}' already exists - bump the version in src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml"
	fi
fi

# --- For a release, the version has to be newer than the last tag --------------

last_tag="$(git tag --list 'v*' --sort=-v:refname | head -1)"

if [ "$for_release" = yes ] && [ -n "$last_tag" ]; then
	last="${last_tag#v}"

	# Compare the two numbers part by part; the version has to be greater.
	newest="$(printf '%s\n%s\n' "$last" "$version" | sort -V | tail -1)"

	if [ "$newest" != "$version" ] || [ "$last" = "$version" ]; then
		fail "'$version' is not newer than the last tag '${last_tag}' - bump the version in src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml"
	fi
fi

echo "version $version is fine (last tag: ${last_tag:-none})"
