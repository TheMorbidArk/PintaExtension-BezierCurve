# Changelog

All notable changes to this project are documented in this file.
The format loosely follows [Keep a Changelog](https://keepachangelog.com/), and the
project uses [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-05

First public release.

### Added

- **A Bezier curve tool for Pinta.** Drag out a line, then bend it with one or two
  control points. The curve stays adjustable until it is committed, and the status bar
  shows the point being placed, the size and the snapped angle.
- **Two behaviours.** In the KolourPaint behaviour a finished curve is paint; in the
  MS Paint behaviour it stays an object with a dashed frame that can be moved, scaled,
  turned and flipped, and restyled while the frame is up.
- **Stroke styles.** End caps, joins, gradient, taper, dashes, closed curves and fill.
- **Symmetry and snapping.** Mirror the curve, snap the line to 0/30/45/60/90 degrees,
  snap control points to construction points or to the end tangent.
- **Keyboard editing.** Enter finishes, Backspace/Delete takes a point back, the arrow
  keys nudge, Ctrl+E picks the last committed curve up again.
- **English and Simplified Chinese** UI strings.
- **Tests.** 124 NUnit tests, a headless smoke test that drives the real tool, and a
  package test that installs, upgrades, uninstalls and restarts Pinta.
