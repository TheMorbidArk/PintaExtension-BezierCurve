#!/usr/bin/env bash
#
# package-test.sh
#
# Verifies the whole delivery path in a throw-away Pinta profile (your real settings are
# never touched):
#
#   1. builds dist/PintaExtension-BezierCurve-<version>.mpack
#   2. installs it through Pinta's own add-in registry - the same call the
#      "Add-ins -> Install from file" dialog makes
#   3. restarts Pinta with the add-in absent from its bin directory, so it can only be
#      loaded from the registry, and runs the full automated check suite
#   4. uninstalls it again through Pinta's registry
#   5. restarts Pinta and verifies that the uninstall left no files, no registry entry
#      and no crash behind
#
# Every Pinta run is wrapped in `timeout`, so a failure can never hang the script.
#
# The delivery chain has to be testable on a tree whose version was already released, so
# the version guard is told that this is a test run. Nothing is published here.
#
# Results: dist/.pkgtest/
#
set -euo pipefail

export ALLOW_RELEASED_VERSION=1

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pinta_dir="${PINTA_SRC:-$root/.pinta/Pinta}"
pinta_bin="${PINTA_BIN:-$pinta_dir/build/bin}"

work="${root}/dist/.pkgtest"
config_home="${work}/config"
cache_home="${work}/cache"
addins_dir="${config_home}/Pinta/addins"
smoke_dir="${work}/smoke"

bootstrap_timeout="${BOOTSTRAP_TIMEOUT:-25}"
run_timeout="${RUN_TIMEOUT:-120}"

if [ ! -f "$pinta_bin/Pinta.dll" ]; then
	echo "Pinta is not built; run: \"$root/scripts/bootstrap-pinta.sh\"" >&2
	exit 1
fi

fail () {
	echo >&2
	echo "RESULT: FAIL - $1" >&2
	[ -f "${work}/pinta.log" ] && grep -v "Failed to load icon" "${work}/pinta.log" | tail -20 >&2
	exit 1
}

run_pinta () {
	local log="$1"
	shift
	rm -rf "$smoke_dir"
	(
		cd "$pinta_bin"
		XDG_CONFIG_HOME="$config_home" \
		XDG_CACHE_HOME="$cache_home" \
		GDK_BACKEND="${GDK_BACKEND:-x11}" \
		KP_CURVE_SMOKE_DIR="$smoke_dir" \
		"$@" timeout "$run_timeout" xvfb-run -a dotnet Pinta.dll
	) >"$log" 2>&1 || true
}

logs () { cat "${smoke_dir}/smoke-test.log" 2>/dev/null || true; }

# --- 1. package --------------------------------------------------------------

echo "==> building the package"
"$root/scripts/make-mpack.sh" >/dev/null
plugin_package="$(ls "$root"/dist/PintaExtension-BezierCurve-*.mpack | head -1)"
echo "    $(basename "$plugin_package")"

rm -rf "$work"
mkdir -p "$config_home"

# The smoke test add-in is the observer/installer harness: it is deployed to Pinta's bin
# directory and never installed from the package.
dotnet build "$root/src/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest.csproj" --nologo -v quiet
rm -f "$pinta_bin/TheMorbidArk.PintaExtension.BezierCurve.dll"
cp "$root/src/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest/bin/Debug/net8.0/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest.dll" "$pinta_bin/"

cleanup () {
	rm -f "$pinta_bin/TheMorbidArk.PintaExtension.BezierCurve.SmokeTest.dll"
}
trap cleanup EXIT

# --- 2. let Pinta create the registry, then install through it ---------------

echo "==> creating a sandboxed Pinta profile"
run_pinta "${work}/pinta-bootstrap.log"

echo "==> installing through Pinta's add-in registry"
run_pinta "${work}/pinta.log" env KP_CURVE_TEST_INSTALL_MPACKS="$plugin_package"
logs | sed 's/^/  /'

grep -q "INSTALL PASSED" "${smoke_dir}/smoke-test.log" 2>/dev/null || fail "installation failed"

# --- 3. load it from the registry only --------------------------------------

echo "==> checking that the add-in is gone from Pinta's bin directory"
if [ -f "$pinta_bin/TheMorbidArk.PintaExtension.BezierCurve.dll" ]; then
	fail "TheMorbidArk.PintaExtension.BezierCurve.dll is still in $pinta_bin"
fi

echo "==> running Pinta (the add-in can only come from the registry)"
run_pinta "${work}/pinta.log"

if [ ! -f "${smoke_dir}/smoke-test.log" ]; then
	fail "the smoke test add-in did not run"
fi

logs | sed 's/^/  /'

grep -q "registry: TheMorbidArk.PintaExtension.BezierCurve,.*path=.*addins/addins/" "${smoke_dir}/smoke-test.log" ||
	fail "the add-in was not loaded from the registry"
grep -q "\[PASS\] curve tool becomes current" "${smoke_dir}/smoke-test.log" ||
	fail "the curve tool did not become the current tool"
grep -q "SMOKE TEST PASSED" "${smoke_dir}/smoke-test.log" ||
	fail "the automated checks failed"

# --- 4. upgrade while Pinta is running --------------------------------------

echo
echo "==> upgrading while Pinta is running"

version="$(sed -n 's/.*<Addin id="TheMorbidArk.PintaExtension.BezierCurve" version="\([^"]*\)".*/\1/p' "$root/src/TheMorbidArk.PintaExtension.BezierCurve/.addin.xml" | head -1)"
next_version="$(awk -F. '{ printf "%s.%s.%s", $1, $2, $3 + 1 }' <<< "$version")"
next_src="${work}/next-addin"

rm -rf "$next_src"
cp -r "$root/src/TheMorbidArk.PintaExtension.BezierCurve" "$next_src"
rm -rf "${next_src}/bin" "${next_src}/obj"
sed -i "s|version=\"${version}\"|version=\"${next_version}\"|" "${next_src}/.addin.xml"

dotnet build "${next_src}/TheMorbidArk.PintaExtension.BezierCurve.csproj" -c Release --nologo -v quiet \
	-p:AddinVersion="$next_version" -p:PintaSourceDir="$pinta_dir" >/dev/null

mapfile -t next_packages < <(
	dotnet "$root/tools/mpack/bin/Debug/net8.0/mpack.dll" build "${work}/next-packages" \
		"${next_src}/bin/Release/net8.0/TheMorbidArk.PintaExtension.BezierCurve.dll" 2>/dev/null | sed -n 's/^PACKAGE: //p'
)
next_package="${next_packages[0]}"
echo "    $version -> $next_version"

run_pinta "${work}/pinta.log" env \
	KP_CURVE_TEST_INSTALL_MPACKS="$next_package" \
	KP_CURVE_TEST_EXPECT_TOOL=1 \
	KP_CURVE_TEST_DRAW_AFTER_INSTALL=1
logs | sed 's/^/  /'

grep -q "\[PASS\] the tool survives an in-place install" "${smoke_dir}/smoke-test.log" 2>/dev/null ||
	fail "the tool disappeared when the add-in was replaced while Pinta was running"
grep -q "\[PASS\] the tool still draws after an in-place install" "${smoke_dir}/smoke-test.log" 2>/dev/null ||
	fail "the tool stopped working after an in-place upgrade"

# --- 5. uninstall through the registry --------------------------------------

echo
echo "==> uninstalling through Pinta's add-in registry"
run_pinta "${work}/pinta.log" env KP_CURVE_TEST_UNINSTALL_ADDIN=TheMorbidArk.PintaExtension.BezierCurve KP_CURVE_TEST_EXPECT_NO_TOOL=1
logs | sed 's/^/  /'

grep -q "UNINSTALL PASSED" "${smoke_dir}/smoke-test.log" 2>/dev/null || fail "uninstall failed"
grep -q "\[PASS\] the tool is gone after an uninstall" "${smoke_dir}/smoke-test.log" 2>/dev/null ||
	fail "the tool stayed in the toolbox after the add-in was uninstalled"

# Mono.Addins cannot delete an assembly that is loaded in the running process, so the
# uninstall is completed on the next start. What matters is the state after that.
pending="$(find "${addins_dir}/addins" -type f 2>/dev/null | wc -l)"
echo "    $pending file(s) pending removal on the next start"

# --- 6. the next start must not crash ---------------------------------------

echo "==> restarting Pinta after the uninstall"
run_pinta "${work}/pinta.log" env KP_CURVE_SMOKE_SKIP_DRAWING=1

if grep -q "FileNotFoundException\|Could not load file or assembly" "${work}/pinta.log"; then
	fail "Pinta failed to start after uninstalling the add-in"
fi

grep -q "registry: TheMorbidArk.PintaExtension.BezierCurve," "${smoke_dir}/smoke-test.log" 2>/dev/null &&
	fail "a stale registry entry survived the uninstall"

leftovers="$(find "${addins_dir}/addins" -type f 2>/dev/null | wc -l)"
if [ "$leftovers" -ne 0 ]; then
	echo "Leftover files after uninstall:" >&2
	find "${addins_dir}/addins" -type f >&2
	fail "$leftovers file(s) were left behind by the uninstall"
fi

echo "    clean start, no stale registry entry, no leftover files"

echo
echo "RESULT: PASS   (package installs, loads from the registry, uninstalls cleanly)"
