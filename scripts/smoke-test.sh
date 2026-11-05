#!/usr/bin/env bash
#
# smoke-test.sh
#
# Builds everything, deploys the add-in plus the test add-in into Pinta's bin folder,
# runs Pinta (headless when needed) and reports the result of the automated checks.
#
# Results are written to dist/smoketest/:
#   smoke-test.log    the PASS/FAIL lines
#   curve-result.png  what the tool drew
#   canvas-vs-layer.png  the rendered canvas, for the render regression check
#   toolbar-icon.png  the toolbar icon as GTK resolves it
#
# The results go into the project's own dist folder. They must not go into TMPDIR:
# Xvfb puts its socket into /tmp/.X11-unix, and that stops working as soon as TMPDIR
# points somewhere else.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pinta_dir="${PINTA_SRC:-$root/.pinta/Pinta}"
pinta_bin="${PINTA_BIN:-$pinta_dir/build/bin}"
output_dir="${root}/dist/smoketest"
config_home="${root}/dist/smoketest-config"
# The icons are unpacked into the user's cache directory, so the run needs one it
# may write to.
cache_home="${root}/dist/smoketest-cache"
timeout_seconds="${SMOKE_TIMEOUT:-120}"

if [ ! -f "$pinta_bin/Pinta.dll" ]; then
	echo "==> building Pinta (first run only)"
	dotnet build "$pinta_dir/Pinta/Pinta.csproj" --nologo -v quiet
fi

"$root/scripts/dev-install.sh" --with-smoke-test

rm -rf "$output_dir"

echo "==> running Pinta"
run_pinta() {
	cd "$pinta_bin"
	# A throw-away config directory keeps the user's real Pinta settings untouched.
	XDG_CONFIG_HOME="$config_home" XDG_CACHE_HOME="$cache_home" KP_CURVE_SMOKE_DIR="$output_dir" \
		GDK_BACKEND="${GDK_BACKEND:-x11}" "$@" timeout "$timeout_seconds" dotnet Pinta.dll
}

# The run is headless by default: a display that happens to be around is not used,
# because Pinta can hang on one (a desktop session of the user's, for instance).
# Set SMOKE_USE_DISPLAY=yes to use it anyway.
if [ "${SMOKE_USE_DISPLAY:-no}" = "yes" ] && [ -n "${DISPLAY:-}" ]; then
	run_pinta
elif command -v xvfb-run >/dev/null 2>&1; then
	run_pinta xvfb-run -a
else
	echo "No xvfb-run available" >&2
	exit 1
fi

echo
if [ -f "$output_dir/smoke-test.log" ]; then
	sed 's/^/  /' "$output_dir/smoke-test.log"
else
	echo "No smoke test log was produced - the add-in probably did not load." >&2
	exit 1
fi

echo
if grep -q "SMOKE TEST PASSED" "$output_dir/smoke-test.log"; then
	echo "RESULT: PASS   (artifacts in $output_dir)"
else
	echo "RESULT: FAIL   (artifacts in $output_dir)" >&2
	exit 1
fi
