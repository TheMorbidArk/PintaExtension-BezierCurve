# What has to be checked by hand

The smoke test drives the tool through Pinta's own API and renders the canvas, so it
covers the geometry, the drawing and the screen. What it cannot cover is the part that
needs a real display, a real mouse and a real installation. This is the list for that
part.

Run it before a release, on the same Pinta the users have - for most of them that is the
Flatpak build.

---

## Installing

- [ ] The `.mpack` installs from **Add-ins → Add-ins → Install from file…**.
- [ ] The **Bezier** tool appears in the tool box right after *Line/Curve*, with its own
      icon and not a broken one.
- [ ] The tool bar appears under the menu bar when the tool is picked.
- [ ] Restarting Pinta keeps the tool installed.
- [ ] Installing a newer `.mpack` over the old one keeps the tool in the tool box, and
      the tool still draws afterwards.
- [ ] Uninstalling takes the tool out of the tool box, and the next start does not crash.

## Drawing a curve

- [ ] A left drag draws a line in the primary colour; a right drag in the secondary one.
- [ ] The second drag places the first control point and the curve bends towards it.
- [ ] The third drag places the second control point and the curve is finished.
- [ ] The curve follows the mouse while the button is down, without flicker.
- [ ] **Enter** finishes the curve; the markers go away.
- [ ] A right click finishes a three point curve.
- [ ] A drag somewhere else finishes the current curve and starts the next one.
- [ ] **Escape** cancels the curve in progress and leaves the image untouched.
- [ ] **Backspace** takes back the last point, and cancels the line when only the two end
      points are left.
- [ ] The arrow keys nudge the highlighted point by one pixel, **Shift** by ten.
- [ ] Switching to another tool commits the curve in progress.
- [ ] Switching to another document commits the curve into the document it was started
      in, and leaves the new one alone.

## The markers

- [ ] The end points are blue dots and the control points magenta dots, joined by dashed
      leashes.
- [ ] The markers keep the same size at 25%, 100% and 400% zoom.
- [ ] Hovering a marker highlights it and turns the cursor into a grab cursor.
- [ ] The markers are visible on a light and on a dark image.

## Snapping

- [ ] **Shift** while dragging the line snaps to 0°, 45° and 90°, and the status bar
      shows the angle.
- [ ] **Ctrl** snaps to 0°, 30°, 60° and 90°.
- [ ] **Shift + Ctrl** snaps to 0°, 30°, 45°, 60° and 90°.
- [ ] **Shift** while placing a control point snaps it to the construction points: the
      canvas edges and centre lines, the corners, the middle and thirds of the line, the
      points already placed, and the perpendicular bisector.
- [ ] **Ctrl** while dragging a control point snaps the tangent at the end point.
- [ ] The snapping feels like it picks the nearest allowed angle, not the next one up.

## The tool bar

- [ ] The controls have room between them and are grouped: brush width, dash, behaviour,
      commit, the gear, the pick-up button.
- [ ] Every box shows the choice that is in force.
- [ ] The tooltips are readable and in the language of the rest of the window.
- [ ] Switching to another tool and back **keeps** the choices.
- [ ] **KolourPaint** behaviour hides the two flip buttons and shows the commit box.
- [ ] **MS Paint** behaviour shows the flip buttons and hides the commit box.
- [ ] The flip buttons are greyed out while no object is being edited.
- [ ] The pick-up button is greyed out before anything was committed.

## The style panel

- [ ] The gear opens the panel, and it closes again when the tool is switched.
- [ ] **Start / End** change the two ends independently, and the preview shows it.
- [ ] **Join** changes the corners of a sharp curve.
- [ ] **Gradient** runs the stroke from the primary to the secondary colour.
- [ ] **Start width / End width** taper the stroke; 0 means the brush width.
- [ ] **Closed curve** leads the curve back to its start point.
- [ ] **Smooth seam** removes the corner where a closed curve meets itself.
- [ ] **Fill** fills the inside of a closed curve.
- [ ] **Symmetry: Mirror** draws a symmetric leaf; **Center** draws a symmetric S.
- [ ] Grabbing the derived control point breaks the symmetry.
- [ ] **Whole pixels** puts every point on a whole pixel.
- [ ] **C** and **F** switch the closed curve and the fill while drawing.

## The object in MS Paint mode

- [ ] A finished curve wears a dashed frame with eight handles and a round handle.
- [ ] Dragging inside the frame moves the object, and the cursor turns into four arrows.
- [ ] Dragging a corner handle scales it, and the opposite corner stays put.
- [ ] Dragging an edge handle changes only one dimension.
- [ ] **Shift** while dragging a corner keeps the proportions.
- [ ] Dragging the round handle turns the object around its middle.
- [ ] **Shift** while turning snaps to 15° steps.
- [ ] The flip buttons mirror the object.
- [ ] The frame turns with the object, and the handles work in the object's own
      directions.
- [ ] Clicking outside the frame drops the object onto the layer.
- [ ] **Escape** throws the object away.
- [ ] Dropping the object is one undo step.

## Picking a curve up again

- [ ] The button in the tool bar and **Ctrl + E** pick the last committed curve up.
- [ ] The curve is taken off the layer while it is edited, and drawn again when the edit
      is finished.
- [ ] **Escape** puts the old curve back.
- [ ] **Ctrl + C** copies the last committed curve to the clipboard as an image.
- [ ] Without a curve, **Ctrl + C** copies the layer as usual.

## The things that are easy to get wrong

- [ ] Nothing is left on the canvas after a curve is committed: no frame, no markers, no
      preview.
- [ ] A very large image (3000x2000 and up) still feels responsive while dragging.
- [ ] The result is the same at 100% and at a high zoom.
- [ ] A two point curve looks like the line Pinta's own *Line/Curve* tool draws.
- [ ] Undo and redo work one curve at a time, and do not merge two curves into one step.
- [ ] The tool does not interfere with Pinta's own tools, and Pinta's own tools still
      work after the add-in is uninstalled.
- [ ] The status bar hint changes with the state: idle, placing a point, adjusting a
      finished curve.

## The look

- [ ] The icon of the tool is legible on a light and on a dark theme.
- [ ] The markers and the frame are visible on a light and on a dark image.
- [ ] The tool bar and the style panel look right in both themes.
