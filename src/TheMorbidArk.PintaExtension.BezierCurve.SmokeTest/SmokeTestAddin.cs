//
// SmokeTestAddin.cs
//
// Test-only add-in. On startup it drives the curve tool with synthetic mouse events,
// checks the geometry / localization / icon plumbing and writes the rendered results
// to $TMPDIR/kpcurve-smoketest/ so that they can be inspected.
//
// This project is never shipped in the release .mpack.
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cairo;
using Gdk;
using Pinta.Core;
using Path = System.IO.Path;

namespace TheMorbidArk.PintaExtension.BezierCurve.SmokeTest;

[Mono.Addins.Extension]
public sealed class SmokeTestAddin : IExtension
{
	internal static readonly string OutputDirectory =
		Environment.GetEnvironmentVariable ("KP_CURVE_SMOKE_DIR") is { Length: > 0 } directory
			? directory
			: Path.Combine (Path.GetTempPath (), "kpcurve-smoketest");

	private static BaseTool? curve_tool;
	private static int failures;

	public void Initialize ()
	{
		Directory.CreateDirectory (OutputDirectory);
		Log ("add-in loaded");

		// Optional modes, used by scripts/package-test.sh. They go through Pinta's own
		// add-in registry, which is exactly what the Add-ins dialog does.
		string? uninstall = Environment.GetEnvironmentVariable ("KP_CURVE_TEST_UNINSTALL_ADDIN");

		if (!string.IsNullOrEmpty (uninstall)) {
			GLib.Functions.TimeoutAdd (0, 1500, () => {
				try {
					UninstallAddin (uninstall);
				} catch (Exception e) {
					Log ("UNINSTALL EXCEPTION: " + e);
					failures++;
				}

				GLib.Functions.TimeoutAdd (0, 800, () => {
					bool registered = PintaCore.Tools.Any (t => t.GetType ().Name == "CurveTool");
					Log ($"tool after uninstall: {registered}");

					if (Environment.GetEnvironmentVariable ("KP_CURVE_TEST_EXPECT_NO_TOOL") == "1")
						Check ("the tool is gone after an uninstall", !registered, registered ? "still registered" : "gone");

					Log (failures == 0 ? "UNINSTALL PASSED" : $"UNINSTALL FAILED ({failures})");
					PintaCore.Chrome.Application.Quit ();
					return false;
				});

				return false;
			});

			return;
		}

		string? packages = Environment.GetEnvironmentVariable ("KP_CURVE_TEST_INSTALL_MPACKS");

		if (!string.IsNullOrEmpty (packages)) {
			GLib.Functions.TimeoutAdd (0, 1500, () => {
				try {
					InstallPackages (packages.Split (Path.PathSeparator));
				} catch (Exception e) {
					Log ("INSTALL EXCEPTION: " + e);
					failures++;
				}

				// Installing over a running Pinta has to keep the tool in the toolbox: Mono.Addins
				// unloads the old add-in and loads the new one in the same session.
				GLib.Functions.TimeoutAdd (0, 800, () => {
					bool registered = PintaCore.Tools.Any (t => t.GetType ().Name == "CurveTool");
					Log ($"tool after install: {registered}");

					if (Environment.GetEnvironmentVariable ("KP_CURVE_TEST_EXPECT_TOOL") == "1")
						Check ("the tool survives an in-place install", registered, registered ? "registered" : "gone");

					if (registered && Environment.GetEnvironmentVariable ("KP_CURVE_TEST_DRAW_AFTER_INSTALL") == "1") {
						// Only Pinta.Core APIs here: the add-in that was just installed may be a
						// different build than the one this test add-in was compiled against.
						BaseTool tool = PintaCore.Tools.First (t => t.GetType ().Name == "CurveTool");

						Document document = PintaCore.Workspace.NewDocument (new Size (200, 120), new Color (1, 1, 1));
						ImageSurface blank = Snapshot (document);

						PintaCore.Tools.SetCurrentTool (tool);
						PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

						Drag (document, new PointD (20, 100), new PointD (180, 100));
						FinishWithEnter (document);

						Check ("the tool still draws after an in-place install", Differs (blank, document), "layer changed");
					}

					Log (failures == 0 ? "INSTALL PASSED" : $"INSTALL FAILED ({failures})");
					PintaCore.Chrome.Application.Quit ();
					return false;
				});

				return false;
			});

			return;
		}

		GLib.Functions.TimeoutAdd (0, 1500, () => {
			try {
				Run ();
			} catch (Exception e) {
				Log ("EXCEPTION: " + e);
				failures++;
			}

			Log (failures == 0 ? "SMOKE TEST PASSED" : $"SMOKE TEST FAILED ({failures} failure(s))");
			PintaCore.Chrome.Application.Quit ();
			return false;
		});
	}

	public void Uninitialize () { }

	#region Test driver

	private static void Run ()
	{
		DumpRegistry ();
		TestGeometry ();
		TestStrings ();
		TestIcon ();

		if (Environment.GetEnvironmentVariable ("KP_CURVE_SMOKE_SKIP_DRAWING") == "1") {
			Log ("-- drawing skipped");
			return;
		}

		TestDrawing ();
		TestAntialiasingOffPixelAlignment ();
		TestAdjustAfterFourPoints ();
		TestCommitOnRelease ();
		TestToolBarPerMode ();
		TestShapeObject ();
		TestUndoKeepsOneCurvePerStep ();
		TestConstructionSnapping ();
		TestTangentSnapping ();
		TestClosedAndFilled ();
		TestSymmetry ();
		TestEditLastCurve ();
		TestClipboardCopy ();
		TestStylePanel ();
		TestStylePanelSwitches ();
		TestHoverHighlight ();
		TestKeyboardEditing ();
		TestDashPattern ();
		TestStatusBar ();
		TestEdgeCases ();
		TestCanvasMatchesTheLayer ();
		TestMatchesPintasOwnTool ();
		TestZoomDoesNotChangeTheResult ();
		TestTranslucentRedrawIsStable ();
		TestPreviewPerformance ();
	}

	/// <summary>
	/// In the "commit now" mode the curve is committed as soon as the last control point
	/// is placed, the way MS Paint does it.
	/// </summary>
	private static void TestCommitOnRelease ()
	{
		Log ("-- commit on release");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		tool.CommitOnRelease = true;

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface blank = Snapshot (document);

		PlaceFourPoints (document);

		Check ("the curve is committed without pressing enter", Differs (blank, document), "layer changed");
		Check ("and the handles are gone", ActiveHandles () == 0, ActiveHandles ().ToString ());

		tool.CommitOnRelease = false;
	}

	/// <summary>
	/// Holding shift while a control point is placed snaps it to the construction points
	/// of the classic MS Paint workflow - the edges and the centre lines of the canvas
	/// and the points that are already placed.
	/// </summary>
	private static void TestConstructionSnapping ()
	{
		Log ("-- construction snapping");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		// --- without shift the point stays where it was dropped ----------------

		Document plain_document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		Drag (plain_document, new PointD (40, 160), new PointD (200, 170));

		PointD plain = new (163, 97);
		PintaCore.Tools.DoMouseDown (plain_document, Args (plain, MouseButton.Left));
		PintaCore.Tools.DoMouseMove (plain_document, Args (plain, MouseButton.None));
		PintaCore.Tools.DoMouseUp (plain_document, Args (plain, MouseButton.Left));

		Check ("without shift the point is not snapped",
			FirstControlPointPosition (tool) == plain,
			FirstControlPointPosition (tool).ToString ());

		// --- with shift it snaps to the centre of the canvas --------------------

		Document snapped_document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		Drag (snapped_document, new PointD (40, 160), new PointD (200, 170));

		PintaCore.Tools.DoMouseDown (snapped_document, Args (plain, MouseButton.Left, ModifierType.ShiftMask));
		PintaCore.Tools.DoMouseMove (snapped_document, Args (plain, MouseButton.None, ModifierType.ShiftMask));
		PintaCore.Tools.DoMouseUp (snapped_document, Args (plain, MouseButton.Left, ModifierType.ShiftMask));

		PointD snapped = new (160, 100);
		Check ("with shift the point snaps to the centre of the canvas",
			FirstControlPointPosition (tool) == snapped,
			FirstControlPointPosition (tool).ToString ());

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (snapped_document, escape);
	}

	/// <summary>
	/// The tool bar only shows the options that mean something in the current mode: the
	/// flip buttons belong to a curve that is still an object, the commit box to one that
	/// is paint.
	/// </summary>
	private static void TestToolBarPerMode ()
	{
		Log ("-- the tool bar follows the mode");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Workspace.NewDocument (new Size (200, 120), new Color (1, 1, 1));

		// The tool bar is built when the tool is selected, so it exists by now.
		Check ("the tool bar has a flip group", tool.FlipBox is not null, tool.FlipBox is null ? "none" : "yes");
		Check ("the tool bar has a commit box", tool.CommitBox is not null, tool.CommitBox is null ? "none" : "yes");

		if (tool.FlipBox is null || tool.CommitBox is null)
			return;

		tool.ToolMode = ToolMode.KolourPaint;
		Check ("KolourPaint hides the flip buttons", !tool.FlipBox.Visible, tool.FlipBox.Visible ? "shown" : "hidden");
		Check ("KolourPaint shows the commit box", tool.CommitBox.Visible, tool.CommitBox.Visible ? "shown" : "hidden");

		tool.ToolMode = ToolMode.MSPaint;
		Check ("MS Paint shows the flip buttons", tool.FlipBox.Visible, tool.FlipBox.Visible ? "shown" : "hidden");
		Check ("MS Paint hides the commit box", !tool.CommitBox.Visible, tool.CommitBox.Visible ? "shown" : "hidden");

		tool.ToolMode = ToolMode.KolourPaint;
	}

	/// <summary>
	/// The MS Paint mode: a finished curve becomes an object with a frame around it. It
	/// can be moved and flipped, and it is dropped onto the layer when something outside
	/// the frame is clicked.
	/// </summary>
	private static void TestShapeObject ()
	{
		Log ("-- the shape object of the MS Paint mode");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);
		tool.ToolMode = ToolMode.MSPaint;

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface blank = Snapshot (document);

		// Four points: the curve is finished and should become an object.
		PlaceFourPoints (document);

		Check ("the finished curve becomes an object", tool.HasShapeObject, tool.HasShapeObject.ToString ());
		Check ("the object is not on the layer yet", !Differs (blank, document), "layer untouched");

		// The frame and its handles are there: the outline, eight resize handles and the
		// round handle that turns the shape.
		int active = FramesActive (tool);
		Check ("the frame and its eight handles are shown", active == 10, $"{active} active handles");

		ShapeFrame? rotate_handle = tool.Handles
			.OfType<ShapeFrame> ()
			.FirstOrDefault (h => h.Active && h.IsRotateHandle);

		Check ("there is a rotate handle", rotate_handle is not null,
			rotate_handle is null ? "none" : "yes");

		// --- moving ---------------------------------------------------------
		BezierCurve? before = tool.ShapeCurve;
		Check ("the object has a curve", before is not null, before is null ? "none" : "yes");

		if (before is null) {
			tool.ToolMode = ToolMode.KolourPaint;
			return;
		}

		// Press inside the frame, well away from any handle.
		PointD inside = InsideFrame (tool);
		PointD target = new (inside.X + 18, inside.Y + 12);

		PintaCore.Tools.DoMouseDown (document, Args (inside, MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (target, MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (target, MouseButton.Left));

		BezierCurve? moved = tool.ShapeCurve;
		double dx = moved!.Value.P0.X - before.Value.P0.X;
		double dy = moved.Value.P0.Y - before.Value.P0.Y;

		Check ("dragging inside the frame moves the object",
			Math.Abs (dx - 18) < 0.5 && Math.Abs (dy - 12) < 0.5, $"moved by {dx:0.#}, {dy:0.#}");
		Check ("moving the object leaves the layer alone", !Differs (blank, document), "layer untouched");

		// --- scaling ---------------------------------------------------------
		//
		// Drag the bottom-right handle outwards: the frame grows, and the top-left
		// corner (the one across from it) stays where it was.
		RectangleD before_scale = ExtentOf (tool);
		ShapeFrame? corner = tool.Handles
			.OfType<ShapeFrame> ()
			.FirstOrDefault (h => h.Active && h.Handle == FrameHandle.BottomRight);

		Check ("the bottom right handle can be grabbed", corner is not null, corner?.Handle.ToString () ?? "none");

		if (corner is not null) {
			PintaCore.Tools.DoMouseDown (document, Args (corner.CanvasPosition, MouseButton.Left));
			PintaCore.Tools.DoMouseMove (document, Args (new PointD (corner.CanvasPosition.X + 40, corner.CanvasPosition.Y + 40), MouseButton.None));
			PintaCore.Tools.DoMouseUp (document, Args (new PointD (corner.CanvasPosition.X + 40, corner.CanvasPosition.Y + 40), MouseButton.Left));

			RectangleD after_scale = ExtentOf (tool);

			Check ("dragging a corner handle makes the object bigger",
				after_scale.Width > before_scale.Width + 1 && after_scale.Height > before_scale.Height + 1,
				$"{before_scale.Width:0.#}x{before_scale.Height:0.#} -> {after_scale.Width:0.#}x{after_scale.Height:0.#}");

			Check ("the corner across from the handle stays put",
				Math.Abs (after_scale.Left - before_scale.Left) < 0.5 &&
				Math.Abs (after_scale.Top - before_scale.Top) < 0.5,
				$"{before_scale.Left:0.#},{before_scale.Top:0.#} -> {after_scale.Left:0.#},{after_scale.Top:0.#}");
		}

		// --- turning ---------------------------------------------------------
		//
		// Grab the round handle and swing it a quarter turn around the middle.
		PointD center_before = tool.Shape!.Center;
		ShapeFrame? rotate = tool.Handles
			.OfType<ShapeFrame> ()
			.FirstOrDefault (h => h.Active && h.IsRotateHandle);

		if (rotate is not null) {
			PointD from = rotate.CanvasPosition;

			// A quarter turn about the middle: the point straight above the middle moves
			// to straight to its right.
			double radius = Math.Sqrt (
				Math.Pow (from.X - center_before.X, 2) + Math.Pow (from.Y - center_before.Y, 2));

			PointD to = new (center_before.X + radius, center_before.Y);

			PintaCore.Tools.DoMouseDown (document, Args (from, MouseButton.Left));
			PintaCore.Tools.DoMouseMove (document, Args (to, MouseButton.None));
			PintaCore.Tools.DoMouseUp (document, Args (to, MouseButton.Left));

			Check ("dragging the round handle turns the object",
				Math.Abs (tool.Shape!.Angle - Math.PI / 2d) < 0.15,
				$"{tool.Shape.Angle * 180d / Math.PI:0.#} degrees");

			Check ("turning keeps the middle where it was",
				Math.Abs (tool.Shape.Center.X - center_before.X) < 0.5 &&
				Math.Abs (tool.Shape.Center.Y - center_before.Y) < 0.5,
				$"{center_before.X:0.#},{center_before.Y:0.#} -> {tool.Shape.Center.X:0.#},{tool.Shape.Center.Y:0.#}");

			// Put it back upright, so the checks below are about flipping only.
			PintaCore.Tools.DoMouseDown (document, Args (to, MouseButton.Left));
			PintaCore.Tools.DoMouseMove (document, Args (from, MouseButton.None));
			PintaCore.Tools.DoMouseUp (document, Args (from, MouseButton.Left));
		}

		// --- flipping --------------------------------------------------------
		RectangleD extent_before = ExtentOf (tool);
		tool.FlipShapeObject (horizontal: true);
		RectangleD extent_after = ExtentOf (tool);

		Check ("flipping keeps the object where it is",
			Math.Abs (extent_after.Left - extent_before.Left) < 0.5 &&
			Math.Abs (extent_after.Right - extent_before.Right) < 0.5,
			$"{extent_before.Left:0.#}..{extent_before.Right:0.#} -> {extent_after.Left:0.#}..{extent_after.Right:0.#}");
		Check ("flipping does not commit", tool.HasShapeObject, "still an object");

		// --- dropping the object ---------------------------------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (300, 20), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 20), MouseButton.Left));

		Check ("clicking outside drops the object onto the layer", Differs (blank, document), "layer changed");
		Check ("the object is gone afterwards", !tool.HasShapeObject, "still an object");
		Check ("and its frame is gone too", FramesActive (tool) == 0, FramesActive (tool).ToString ());

		// Dropping the object is all that press does: it must not start a new curve
		// under the cursor, which would leave a stray point behind.
		Check ("dropping the object does not start a new curve",
			tool.PointCount == 0, $"{tool.PointCount} point(s) of a new curve");

		// --- the KolourPaint mode has no object ------------------------------
		tool.ToolMode = ToolMode.KolourPaint;

		Document second = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		PintaCore.Tools.SetCurrentTool (tool);
		PlaceFourPoints (second);

		Check ("the KolourPaint mode does not make an object", !tool.HasShapeObject, "an object appeared");

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (second, escape);
	}

	/// <summary>
	/// Every object that is dropped is one undo step: undoing once takes the last curve
	/// away and leaves the ones drawn before it alone.
	/// </summary>
	private static void TestUndoKeepsOneCurvePerStep ()
	{
		Log ("-- one undo step per dropped object");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);
		tool.ToolMode = ToolMode.MSPaint;

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		// --- the first curve, in the upper half ------------------------------
		DrawCurveInMSMode (document, top: true);

		Check ("the first object is dropped",
			PaintedInRect (document, 20, 10, 170, 70) > 0,
			$"{PaintedInRect (document, 20, 10, 170, 70)} painted pixels");

		// --- the second curve, in the lower half -----------------------------
		DrawCurveInMSMode (document, top: false);

		int upper = PaintedInRect (document, 20, 10, 170, 70);
		int lower = PaintedInRect (document, 20, 130, 170, 190);

		Check ("both curves are on the layer", upper > 0 && lower > 0,
			$"{upper} above, {lower} below");

		// --- undo once --------------------------------------------------------
		PintaCore.Actions.Edit.Undo.Activate ();

		Check ("one undo takes the last curve away",
			PaintedInRect (document, 20, 130, 170, 190) == 0,
			$"{PaintedInRect (document, 20, 130, 170, 190)} pixels left below");
		Check ("and leaves the first curve where it was",
			PaintedInRect (document, 20, 10, 170, 70) > 0,
			$"{PaintedInRect (document, 20, 10, 170, 70)} pixels above");

		// --- undo again -------------------------------------------------------
		PintaCore.Actions.Edit.Undo.Activate ();

		Check ("a second undo takes the first curve away too",
			PaintedInRect (document, 20, 10, 170, 70) == 0,
			$"{PaintedInRect (document, 20, 10, 170, 70)} pixels above");

		// --- an object that is still an object -------------------------------
		//
		// It is not in the history yet, so one undo has to throw it away and leave the
		// curve that was dropped before it alone.
		DrawCurveInMSMode (document, top: true);
		Check ("the curve before the object is on the layer",
			PaintedInRect (document, 20, 10, 170, 70) > 0, "nothing painted");

		// Draw a second curve but do not drop it.
		Drag (document, new PointD (30, 150), new PointD (150, 150));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 175), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (60, 175), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (120, 175), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (120, 175), MouseButton.Left));

		Check ("the second curve is still an object", tool.HasShapeObject, "not an object");

		PintaCore.Actions.Edit.Undo.Activate ();

		Check ("undoing a live object throws it away", !tool.HasShapeObject, "still an object");
		Check ("and leaves the curve dropped before it alone",
			PaintedInRect (document, 20, 10, 170, 70) > 0,
			$"{PaintedInRect (document, 20, 10, 170, 70)} pixels above");

		tool.ToolMode = ToolMode.KolourPaint;
	}

	/// <summary>Draws one curve in the MS Paint mode and drops it by clicking outside.</summary>
	private static void DrawCurveInMSMode (Document document, bool top)
	{
		double y = top ? 40 : 150;
		double bulge = top ? -20 : 20;

		Drag (document, new PointD (30, y), new PointD (150, y));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, y + bulge), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (60, y + bulge), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (120, y + bulge), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (120, y + bulge), MouseButton.Left));

		// A press far away from the frame drops the object.
		PointD away = top ? new PointD (300, 195) : new PointD (300, 5);
		PintaCore.Tools.DoMouseDown (document, Args (away, MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (away, MouseButton.Left));
	}

	/// <summary>How many pixels of the layer inside the rectangle are not white.</summary>
	private static int PaintedInRect (Document document, int x1, int y1, int x2, int y2)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		int width = surface.Width;
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		int painted = 0;

		for (int y = y1; y <= y2; ++y)
			for (int x = x1; x <= x2; ++x)
				if (pixels[y * width + x].R < 250)
					painted++;

		return painted;
	}

	/// <summary>How many of the selection frame's handles are switched on.</summary>
	private static int FramesActive (CurveTool tool)
		=> tool.Handles.OfType<ShapeFrame> ().Count (h => h.Active);

	/// <summary>A point inside the object's frame but away from its handles.</summary>
	private static PointD InsideFrame (CurveTool tool)
	{
		RectangleD bounds = tool.Shape!.Bounds;

		return new PointD (
			bounds.Left + bounds.Width * 0.5,
			bounds.Top + bounds.Height * 0.5);
	}

	/// <summary>The extent of the object's curve, without the brush margin.</summary>
	private static RectangleD ExtentOf (CurveTool tool)
	{
		(double minX, double minY, double maxX, double maxY) = CurveGeometry.Extent (tool.ShapeCurve!.Value);

		return new RectangleD (minX, minY, maxX - minX, maxY - minY);
	}

	/// <summary>
	/// Holding control while a control point is placed snaps the tangent of the curve
	/// at the end point it belongs to.
	/// </summary>
	private static void TestTangentSnapping ()
	{
		Log ("-- tangent snapping");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		Drag (document, new PointD (40, 160), new PointD (240, 160));

		// The control point is dragged to a spot that is about 22 degrees above the
		// line, which the tangent snapping puts onto 30 degrees.
		PointD target = new (140, 120);

		PintaCore.Tools.DoMouseDown (document, Args (target, MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (target, MouseButton.None, ModifierType.ControlMask));
		PintaCore.Tools.DoMouseUp (document, Args (target, MouseButton.Left, ModifierType.ControlMask));

		PointD snapped = FirstControlPointPosition (tool);

		// The distance from the start point is kept, the angle becomes 30 degrees.
		double distance = Math.Sqrt (Math.Pow (snapped.X - 40, 2) + Math.Pow (snapped.Y - 160, 2));
		double expected_distance = Math.Sqrt (100 * 100 + 40 * 40);

		Check ("the tangent is snapped to 30 degrees",
			Math.Abs (snapped.Y - (160 - expected_distance * 0.5d)) < 0.01,
			$"{snapped} (distance {distance:0.00}, expected {expected_distance:0.00})");
	}

	/// <summary>
	/// A closed curve is filled, and the fill reaches the layer.
	/// </summary>
	private static void TestClosedAndFilled ()
	{
		Log ("-- closed and filled");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		tool.ClosedEnabled = true;
		tool.FillEnabled = true;

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		// A curve that loops around a box.
		Drag (document, new PointD (40, 40), new PointD (240, 40));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (240, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (240, 160), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (240, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (40, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (40, 160), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (40, 160), MouseButton.Left));

		FinishWithEnter (document);

		Check ("the inside of the closed curve is filled", PaintedAt (document, 140, 100), "the middle must be painted");
		Check ("the outside of the closed curve is empty", !PaintedAt (document, 300, 180), "outside");

		tool.FillEnabled = false;
		tool.ClosedEnabled = false;
	}

	/// <summary>
	/// With the symmetry on, the second control point follows the first one; grabbing
	/// the point that was derived breaks the symmetry on purpose.
	/// </summary>
	private static void TestSymmetry ()
	{
		Log ("-- symmetry");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		tool.Symmetry = SymmetryMode.Mirror;

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		Drag (document, new PointD (40, 160), new PointD (240, 160));

		// The first control point.
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (80, 60), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (80, 60), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (80, 60), MouseButton.Left));

		// The second one: the first control point is mirrored in the perpendicular
		// bisector of the line, which is the vertical line through its middle.
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (200, 100), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (200, 100), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (200, 100), MouseButton.Left));

		Check ("the control points are mirrored",
			tool.Points[2] == new PointD (80, 100),
			$"{tool.Points[2]} (the other one is at {tool.Points[3]})");

		// Grabbing the point that was derived breaks the symmetry.
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (80, 100), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (70, 110), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (70, 110), MouseButton.Left));

		Check ("grabbing the derived point breaks the symmetry",
			tool.Symmetry == SymmetryMode.Off,
			tool.Symmetry.ToString ());

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
	}

	/// <summary>
	/// The panel with the stroke, the shape and the symmetry options can be opened.
	/// </summary>
	private static void TestStylePanel ()
	{
		Log ("-- style panel");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);

		bool opened = true;

		try {
			tool.OpenStylePanel ();
		} catch (Exception e) {
			opened = false;
			Log ("exception: " + e);
		}

		Check ("the style panel opens", opened, opened ? "opened" : "the panel could not be opened");
	}

	/// <summary>
	/// The switches of the style panel change the tool, the way a click on them does.
	/// </summary>
	private static void TestStylePanelSwitches ()
	{
		Log ("-- style panel switches");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);

		tool.ClosedEnabled = false;
		tool.FillEnabled = false;

		// Clicking a switch of the panel has to change the tool.
		tool.ClosedSwitch!.Active = true;
		Check ("the switch of the panel turns the closed curve on", tool.ClosedEnabled, tool.ClosedEnabled.ToString ());

		tool.FillSwitch!.Active = true;
		Check ("the switch of the panel turns the fill on", tool.FillEnabled, tool.FillEnabled.ToString ());

		// And the other way round: the tool moves the switch.
		tool.ClosedEnabled = false;
		Check ("the tool turns the switch of the panel off", !tool.ClosedSwitch.Active, tool.ClosedSwitch.Active.ToString ());

		// The buttons and the spin buttons of the panel, too.
		tool.StartEndGroup!.Press (1);
		Check ("the button of the panel changes the start of the stroke", tool.StartEnd == EndShape.Pointed, tool.StartEnd.ToString ());
		Check ("only one button of the group is pressed", tool.StartEndGroup.PressedCount == 1, tool.StartEndGroup.PressedCount.ToString ());

		tool.FinishEndGroup!.Press (2);
		Check ("the button of the panel changes the end of the stroke", tool.FinishEnd == EndShape.Square, tool.FinishEnd.ToString ());
		Check ("the two ends are picked on their own", tool.StartEnd != tool.FinishEnd, $"{tool.StartEnd} / {tool.FinishEnd}");

		tool.TaperStartSpin!.Value = 8;
		Check ("the spin button of the panel changes the taper", tool.TaperStartWidth == 8, tool.TaperStartWidth.ToString ());

		tool.SymmetryGroup!.Press (1);
		Check ("the button of the panel changes the symmetry",
			tool.Symmetry == SymmetryMode.Mirror,
			tool.Symmetry.ToString ());

		tool.FillEnabled = false;
		tool.StartEnd = EndShape.Round;
		tool.FinishEnd = EndShape.Round;
		tool.TaperStartWidth = 0;
		tool.Symmetry = SymmetryMode.Off;
	}

	/// <summary>
	/// The curve that was committed last can be put on the clipboard as an image, the
	/// way MS Paint copies a shape.
	/// </summary>
	private static void TestClipboardCopy ()
	{
		Log ("-- clipboard");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		PlaceFourPoints (document);
		FinishWithEnter (document);

		using ImageSurface? copy = tool.RenderLastCurve ();

		Check ("the committed curve can be copied", copy is not null, "no curve");

		if (copy is null)
			return;

		Check ("the copy is only as big as the curve",
			copy.Width < 320 && copy.Height < 200,
			$"{copy.Width}x{copy.Height}");

		int painted = PaintedPixels (copy);
		Check ("the copy contains the curve", painted > 0, $"{painted} pixels");
	}

	/// <summary>How many pixels of the surface are not fully transparent.</summary>
	private static int PaintedPixels (ImageSurface surface)
	{
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		int painted = 0;

		for (int i = 0; i < pixels.Length; ++i)
			if (pixels[i].A > 0)
				painted++;

		return painted;
	}

	/// <summary>Whether a pixel of the layer is not white.</summary>
	private static bool PaintedAt (Document document, int x, int y)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		return pixels[y * surface.Width + x].R < 250;
	}

	/// <summary>
	/// The curve that was committed last can be picked up again and adjusted; escape
	/// puts the old one back.
	/// </summary>
	private static void TestEditLastCurve ()
	{
		Log ("-- edit last curve");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		PlaceFourPoints (document);
		FinishWithEnter (document);

		ImageSurface committed = Snapshot (document);

		Check ("the curve is committed", ToolLayerPixels (document) == 0, "the tool layer is empty");

		// --- pick it up again ---------------------------------------------------

		tool.EditLastCurve ();

		Check ("the curve is picked up again", ActiveHandles () == 6, ActiveHandles ().ToString ());
		Check ("and it is taken off the layer", Differs (committed, document), "the layer changed");

		// --- escape puts it back ------------------------------------------------

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);

		Check ("escape puts the curve back", !Differs (committed, document), "the layer is unchanged");
		Check ("and the handles are gone", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- and adjust it ------------------------------------------------------

		tool.EditLastCurve ();

		PintaCore.Tools.DoMouseDown (document, Args (new PointD (270, 110), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (300, 60), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 60), MouseButton.Left));

		FinishWithEnter (document);

		Check ("the adjusted curve is committed", Differs (committed, document), "the curve changed");
		Check ("the handles are gone", ActiveHandles () == 0, ActiveHandles ().ToString ());
	}

	private static void TestGeometry ()
	{
		Log ("-- geometry");

		// Shift only: 0 / 45 / 90 degrees, boundaries at 22.5 and 67.5 degrees.
		// Note: on a horizontal/vertical snap only the other coordinate is kept, exactly
		// like KolourPaint (the distance along the snapped axis is preserved).
		PointD e20 = Angle (20, 10);
		PointD e40 = Angle (40, 10);
		PointD e80 = Angle (80, 10);

		CheckSnap ("shift 20deg -> 0", e20, shift: true, control: false, new PointD (e20.X, 0), 0);
		CheckSnap ("shift 40deg -> 45", e40, shift: true, control: false, Angle (45, 10), 45);
		CheckSnap ("shift 80deg -> 90", e80, shift: true, control: false, new PointD (0, e80.Y), 90);

		// Control only: 0 / 30 / 60 / 90 degrees.
		PointD c10 = Angle (10, 10);
		PointD c25 = Angle (25, 10);
		PointD c50 = Angle (50, 10);
		PointD c80 = Angle (80, 10);

		CheckSnap ("ctrl 10deg -> 0", c10, shift: false, control: true, new PointD (c10.X, 0), 0);
		CheckSnap ("ctrl 25deg -> 30", c25, shift: false, control: true, Angle (30, 10), 30);
		CheckSnap ("ctrl 50deg -> 60", c50, shift: false, control: true, Angle (60, 10), 60);
		CheckSnap ("ctrl 80deg -> 90", c80, shift: false, control: true, new PointD (0, c80.Y), 90);

		// Shift+Control: 30 / 45 / 60 are all available.
		CheckSnap ("ctrl+shift 20deg -> 30", Angle (20, 10), shift: true, control: true, Angle (30, 10), 30);
		CheckSnap ("ctrl+shift 40deg -> 45", Angle (40, 10), shift: true, control: true, Angle (45, 10), 45);
		CheckSnap ("ctrl+shift 55deg -> 60", Angle (55, 10), shift: true, control: true, Angle (60, 10), 60);

		// Horizontal / vertical snapping must keep the other coordinate untouched.
		PointD horizontal = CurveGeometry.SnapToAngles (new PointD (10, 10), new PointD (30, 14), true, false, out _);
		Check ("shift keeps x and flattens y", horizontal == new PointD (30, 10), horizontal.ToString ());

		PointD vertical = CurveGeometry.SnapToAngles (new PointD (10, 10), new PointD (14, 30), true, false, out _);
		Check ("shift keeps y and flattens x", vertical == new PointD (10, 30), vertical.ToString ());

		// No modifier: no snapping at all.
		PointD plain = CurveGeometry.SnapToAngles (new PointD (0, 0), new PointD (7, 3), false, false, out double none);
		Check ("no modifier is a no-op", plain == new PointD (7, 3) && double.IsNaN (none), plain.ToString ());

		// 2 / 3 / 4 point resolution.
		PointD[] two = [new (0, 0), new (10, 10)];
		BezierCurve line = CurveGeometry.Resolve (two);
		Check ("2 points -> straight line", line.P1 == two[0] && line.P2 == two[1], line.ToString ());

		PointD[] three = [new (0, 0), new (10, 0), new (5, -5)];
		BezierCurve quadratic = CurveGeometry.Resolve (three);
		Check ("3 points -> both controls coincide", quadratic.P1 == three[2] && quadratic.P2 == three[2], quadratic.ToString ());

		PointD[] four = [new (0, 0), new (10, 0), new (2, -5), new (8, -5)];
		BezierCurve cubic = CurveGeometry.Resolve (four);
		Check ("4 points -> both controls", cubic.P1 == four[2] && cubic.P2 == four[3], cubic.ToString ());
	}

	private static void TestStrings ()
	{
		Log ("-- localization");

		CultureInfo original = CultureInfo.CurrentUICulture;

		try {
			CultureInfo.CurrentUICulture = new CultureInfo ("zh-Hans");
			Check ("zh-Hans tool name", Strings.Get ("ToolName") == "贝塞尔", Strings.Get ("ToolName"));
			Check ("zh-Hans hint", Strings.Get ("HintFirstControl").Contains ("控制点"), Strings.Get ("HintFirstControl"));

			CultureInfo.CurrentUICulture = new CultureInfo ("en-US");
			Check ("en tool name", Strings.Get ("ToolName") == "Bezier", Strings.Get ("ToolName"));
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	private static void TestIcon ()
	{
		Log ("-- icon");

		string cache_directory = CurveAddin.IconCacheDirectory ();
		Check ("icons were unpacked from the assembly", Directory.Exists (cache_directory), cache_directory);

		using Gdk.Texture icon = Pinta.Resources.ResourceLoader.GetIcon (CurveAddin.ToolIcon, 32);
		Check ("icon resolves", icon.Width > 0 && icon.Height > 0, $"{icon.Width}x{icon.Height}");
		icon.SaveToPng (Path.Combine (OutputDirectory, "toolbar-icon.png"));

		// The button that picks the last curve up again uses an icon of the theme.
		using Gdk.Texture edit_icon = Pinta.Resources.ResourceLoader.GetIcon ("document-edit-symbolic", 24);
		Check ("the edit icon resolves", edit_icon.Width > 0 && edit_icon.Height > 0, $"{edit_icon.Width}x{edit_icon.Height}");
	}

	private static void TestDrawing ()
	{
		Log ("-- drawing");

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		Log ($"document {document.ImageSize.Width}x{document.ImageSize.Height}");

		curve_tool = PintaCore.Tools.FirstOrDefault (t => t.GetType ().Name == "CurveTool");
		if (curve_tool is null) {
			Check ("curve tool is registered", false, "not found");
			return;
		}

		PintaCore.Tools.SetCurrentTool (curve_tool);
		Check ("curve tool becomes current", PintaCore.Tools.CurrentTool == curve_tool, PintaCore.Tools.CurrentTool?.GetType ().Name ?? "null");
		Log ($"toolbox entries: {PintaCore.Tools.Count ()}, icon={curve_tool.ToolItem.IconName}, shortcut={curve_tool.ShortcutKey.Name ()}");

		ImageSurface blank = Snapshot (document);

		// --- 1: four points, the shape completes by itself -------------------
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		PintaCore.Tools.DoMouseDown (document, Args (new PointD (40, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (200, 170), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (200, 170), MouseButton.Left));
		Check ("2 end points -> 2 handles", ActiveHandles () == 2, ActiveHandles ().ToString ());
		Check ("preview does not touch the layer", !Differs (blank, document), "layer untouched");

		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (70, 30), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (70, 30), MouseButton.Left));
		Check ("1 control point -> 4 handles", ActiveHandles () == 4, ActiveHandles ().ToString ());

		// Export the tool layer now: it shows the live preview of the curve that is not
		// part of the image yet.
		SaveSurface (document.Layers.ToolLayer.Surface, "preview.png");

		PintaCore.Tools.DoMouseDown (document, Args (new PointD (280, 120), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (270, 110), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (270, 110), MouseButton.Left));
		Check ("4 points stay adjustable", ActiveHandles () == 6, ActiveHandles ().ToString ());
		Check ("nothing is committed yet", !Differs (blank, document), "layer untouched");

		FinishWithEnter (document);
		Check ("Enter commits the shape", Differs (blank, document), "layer changed");
		Check ("no handles after commit", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- 2: three points, finished with the other mouse button -----------
		PintaCore.Palette.PrimaryColor = new Color (0.8, 0, 0);
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (40, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (160, 100), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (160, 100), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (100, 180), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (110, 170), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (110, 170), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (250, 60), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (250, 60), MouseButton.Right));
		Check ("right click finishes a 3 point curve", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- 3: a plain line -------------------------------------------------
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0.8);
		Drag (document, new PointD (20, 190), new PointD (300, 190));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (300, 190), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 190), MouseButton.Right));

		// --- 4: a shift snapped line (21.8 degrees -> 0 degrees) -------------
		PintaCore.Palette.PrimaryColor = new Color (0, 0.45, 0);
		Drag (document, new PointD (150, 20), new PointD (250, 60), ModifierType.ShiftMask);
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (250, 45), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (250, 45), MouseButton.Right));

		Check ("curves were painted", Differs (blank, document), "layer changed");
		SaveResult (document, "curve-result.png");

		// --- undo / redo -----------------------------------------------------
		ImageSurface before_undo = Snapshot (document);

		PintaCore.Actions.Edit.Undo.Activate ();
		Check ("undo changes the layer", Differs (before_undo, document), "layer changed");

		PintaCore.Actions.Edit.Redo.Activate ();
		Check ("redo restores the layer", !Differs (before_undo, document), "layer restored");

		// --- escape cancels an in-progress shape ------------------------------
		ImageSurface before_cancel = Snapshot (document);
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (10, 10), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (100, 100), MouseButton.None));
		Check ("shape in progress", ActiveHandles () == 2, ActiveHandles ().ToString ());

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
		Check ("escape clears the handles", ActiveHandles () == 0, ActiveHandles ().ToString ());
		Check ("escape leaves the image untouched", !Differs (before_cancel, document), "layer untouched");

		// --- the handle markers must render without blowing up ------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (30, 120), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (150, 60), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (150, 60), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (90, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (100, 150), MouseButton.None));

		string handles_size = RenderHandles (curve_tool);
		Check ("handle markers render", handles_size != "no node", handles_size);

		PintaCore.Tools.DoKeyDown (document, escape);
		Check ("escape finishes the session", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- antialiasing off: the stroke must still look like a curve ----------
		curve_tool.UseAntialiasing = false;
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);
		Drag (document, new PointD (60, 60), new PointD (260, 140));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (260, 140), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (260, 140), MouseButton.Right));
		Check ("antialiasing can be turned off", !curve_tool.UseAntialiasing, "aa off");
		SaveResult (document, "no-antialias.png");
		curve_tool.UseAntialiasing = true;
	}

	private static void UninstallAddin (string id)
	{
		Mono.Addins.AddinRegistry registry = Mono.Addins.AddinManager.Registry;
		Mono.Addins.Setup.SetupService service = new (registry);

		Log ($"uninstalling {id} from {registry.RegistryPath}");
		service.Uninstall (new SilentProgress (), id);

		string registered = string.Join (", ", registry.GetAddins ().Select (a => $"{a.Id} {a.Description.Version}"));
		Log ("registry now contains: " + (registered.Length == 0 ? "(nothing)" : registered));

		// Match the exact add-in id: "TheMorbidArk.PintaExtension.BezierCurve" is a prefix of "TheMorbidArk.PintaExtension.BezierCurve.SmokeTest".
		bool still_registered = registry.GetAddins ().Any (a => a.Id == id || a.Id.StartsWith (id + ",", StringComparison.Ordinal));

		Check ("add-in is gone from the registry", !still_registered, registered);
	}

	private static void DumpRegistry ()
	{
		Mono.Addins.AddinRegistry registry = Mono.Addins.AddinManager.Registry;

		foreach (Mono.Addins.Addin addin in registry.GetAddins ())
			Log ($"registry: {addin.Id} {addin.Description.Version} enabled={addin.Enabled} path={addin.Description.BasePath}");

		Log ("registry dir: " + registry.RegistryPath);

		foreach (Mono.Addins.ExtensionNode node in Mono.Addins.AddinManager.GetExtensionNodes (typeof (IExtension))) {
			string description;

			try {
				Type? type = (node as Mono.Addins.TypeExtensionNode)?.Type;
				description = type is null ? "type=null" : "type=" + type.FullName;
			} catch (Exception e) {
				description = "type resolution failed: " + e.GetType ().Name + ": " + e.Message;
			}

			Log ($"node: addin={node.Addin?.Id} path={node.Path} {description}");
		}
	}

	private static void InstallPackages (string[] packages)
	{
		Mono.Addins.AddinRegistry registry = Mono.Addins.AddinManager.Registry;
		Mono.Addins.Setup.SetupService service = new (registry);

		Log ($"installing {packages.Length} package(s): {string.Join (", ", packages)}");

		bool installed = service.Install (new SilentProgress (), packages);
		Check ("packages install", installed, installed ? "ok" : "failed");

		string registered = string.Join (", ", registry.GetAddins ().Select (a => $"{a.Id} {a.Description.Version}"));
		Log ("registry now contains: " + registered);
		Check ("curve add-in is registered", registered.Contains ("TheMorbidArk.PintaExtension.BezierCurve"), registered);
	}

	private sealed class SilentProgress : Mono.Addins.IProgressStatus
	{
		public int LogLevel => 0;
		public bool IsCanceled => false;
		public void SetMessage (string message) { }
		public void SetProgress (double progress) { }
		public void Log (string message) { }
		public void ReportWarning (string message) => Log ("warning: " + message);
		public void ReportError (string message, Exception? exception) => Log ("error: " + message + " " + exception?.Message);
		public void Cancel () { }
	}


	/// <summary>
	/// With antialiasing off the stroke has to land exactly on whole pixels: a 1px wide
	/// horizontal line must produce one fully black row, a 2px line two of them, and no
	/// grey rows in between. Cairo centres a stroke on the path, so an odd width needs a
	/// half pixel offset (the same trick Pinta's pencil tool uses).
	/// </summary>
	private static void TestAntialiasingOffPixelAlignment ()
	{
		Log ("-- pixel alignment (antialiasing off)");

		foreach (int brush_width in new[] { 1, 2, 3 }) {
			Document document = PintaCore.Workspace.NewDocument (new Size (320, 120), new Color (1, 1, 1));

			PintaCore.Tools.SetCurrentTool (curve_tool!);
			CurveTool tool = (CurveTool) curve_tool!;
			tool.UseAntialiasing = false;
			tool.BrushWidth = brush_width;
			PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

			Drag (document, new PointD (20, 100), new PointD (300, 100));
			PintaCore.Tools.DoMouseDown (document, Args (new PointD (300, 100), MouseButton.Right));
			PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 100), MouseButton.Right));

			(List<int> dark_rows, int grey_rows) = AnalyseColumn (document, x: 160);

			bool count_ok = dark_rows.Count == brush_width;
			bool contiguous = count_ok && dark_rows[^1] - dark_rows[0] == brush_width - 1;
			bool crisp = grey_rows == 0;
			bool covers = dark_rows.Count > 0 && IsRowFilled (document, dark_rows[dark_rows.Count / 2], 21, 299);

			// The drawn rows must be centred on the dragged y: a stroke of height h covers
			// [y - h/2, y + h/2], so first + last == 2y - 1 for even widths (which sit
			// between two pixel rows) and 2y for odd ones.
			const int drag_y = 100;
			int expected_sum = 2 * drag_y - 1 + (brush_width % 2);
			bool centred = count_ok && dark_rows[0] + dark_rows[^1] == expected_sum;

			Check ($"width {brush_width}: rows={dark_rows.Count} grey={grey_rows} " +
				$"[{string.Join (",", dark_rows)}]",
				count_ok && contiguous && crisp && covers && centred,
				$"contiguous={contiguous} crisp={crisp} covers={covers} centred={centred} (sum {string.Join ("+", dark_rows.Count > 0 ? [dark_rows[0], dark_rows[^1]] : new List<int> ())} vs {expected_sum})");

			tool.UseAntialiasing = true;
			SaveResult (document, $"no-antialias-{brush_width}px.png");
		}
	}

	/// <summary>Rows in a column that are fully black, and rows that are partially covered.</summary>
	private static (List<int> Dark, int Grey) AnalyseColumn (Document document, int x)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		List<int> dark = [];
		int grey = 0;

		for (int y = 0; y < size.Height; ++y) {
			ColorBgra pixel = pixels[y * size.Width + x];

			if (pixel.R < 16 && pixel.G < 16 && pixel.B < 16)
				dark.Add (y);
			else if (pixel.R < 255 || pixel.G < 255 || pixel.B < 255)
				grey++;
		}

		return (dark, grey);
	}

	/// <summary>True when every pixel in the given row between x1 and x2 is black.</summary>
	private static bool IsRowFilled (Document document, int y, int x1, int x2)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		for (int x = x1; x <= x2; ++x) {
			ColorBgra pixel = pixels[y * size.Width + x];

			if (pixel.R >= 16 || pixel.G >= 16 || pixel.B >= 16)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Measures how long a mouse move takes on a large canvas. The preview has to erase
	/// and redraw the curve on every move, so the cost is dominated by how much of the
	/// tool layer is touched.
	/// </summary>
	/// <summary>
	/// After both control points are placed the curve stays editable: dragging any point
	/// reshapes the preview, Enter finishes, a press on empty space finishes and starts the
	/// next curve, and Escape throws it away.
	/// </summary>
	private static void TestAdjustAfterFourPoints ()
	{
		Log ("-- adjust after four points");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface blank = Snapshot (document);

		PlaceFourPoints (document);

		Check ("four points stay adjustable", ActiveHandles () == 6, ActiveHandles ().ToString ());
		Check ("the curve is not committed yet", !Differs (blank, document), "layer untouched");

		// --- drag the last control point -------------------------------------
		int painted_before = ToolLayerPixels (document);

		PintaCore.Tools.DoMouseDown (document, Args (new PointD (270, 110), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (300, 60), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 60), MouseButton.Left));

		Check ("dragging a control point reshapes the preview",
			ToolLayerPixels (document) != painted_before,
			$"{painted_before} -> {ToolLayerPixels (document)}");
		Check ("dragging does not commit", !Differs (blank, document), "layer untouched");
		Check ("the shape is still adjustable", ActiveHandles () == 6, ActiveHandles ().ToString ());

		// --- drag an end point -----------------------------------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (40, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (20, 190), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (20, 190), MouseButton.Left));

		Check ("end points can be dragged too", !Differs (blank, document), "layer untouched");

		// --- a press on empty space finishes and starts the next curve --------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (250, 30), MouseButton.Left));
		Check ("pressing elsewhere commits the previous curve", Differs (blank, document), "layer changed");
		Check ("and starts a new one", ActiveHandles () == 2, ActiveHandles ().ToString ());

		PintaCore.Tools.DoMouseMove (document, Args (new PointD (300, 30), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 30), MouseButton.Left));
		FinishWithEnter (document);
		Check ("the new curve is a plain line", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- Escape throws an unfinished curve away ---------------------------
		ImageSurface after_first = Snapshot (document);
		PlaceFourPoints (document);

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);

		Check ("escape discards the adjustable curve", !Differs (after_first, document), "layer untouched");
		Check ("escape clears the handles", ActiveHandles () == 0, ActiveHandles ().ToString ());
	}

	private static void PlaceFourPoints (Document document)
	{
		Drag (document, new PointD (40, 160), new PointD (200, 170));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (70, 30), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (70, 30), MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (280, 120), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (270, 110), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (270, 110), MouseButton.Left));
	}

	/// <summary>
	/// Hovering a marker highlights it and switches to the grab cursor, so it is obvious
	/// that the point can be dragged.
	/// </summary>
	private static void TestHoverHighlight ()
	{
		Log ("-- hover highlight");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		PlaceFourPoints (document);

		// Away from every point.
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (300, 20), MouseButton.None));
		Check ("no marker is highlighted in empty space", HoveredPoints (tool) == 0, HoveredPoints (tool).ToString ());
		Check ("the default cursor is used in empty space", tool.CurrentCursor == tool.DefaultCursor, "default");

		// Over the last control point.
		PointD control = new (270, 110);
		PintaCore.Tools.DoMouseMove (document, Args (control, MouseButton.None));

		CurveHandle? hovered = tool.Handles.OfType<CurveHandle> ().FirstOrDefault (h => h.Hovered);
		bool correct_marker = hovered is not null
			&& hovered.Kind == CurveHandleKind.ControlPoint
			&& hovered.CanvasPosition == control;

		Check ("the marker under the cursor is highlighted", correct_marker,
			hovered is null ? "none" : $"{hovered.Kind} at {hovered.CanvasPosition}");
		Check ("only one marker is highlighted", HoveredPoints (tool) == 1, HoveredPoints (tool).ToString ());
		Check ("the grab cursor replaces the tool cursor", tool.CurrentCursor != tool.DefaultCursor, "grab");

		// Export the markers with one of them highlighted.
		Check ("markers render highlighted", RenderHandles (tool) != "no node", RenderHandles (tool));

		// Back to empty space: the highlight and the cursor must be restored.
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (20, 20), MouseButton.None));
		Check ("the highlight goes away", HoveredPoints (tool) == 0, HoveredPoints (tool).ToString ());
		Check ("the default cursor comes back", tool.CurrentCursor == tool.DefaultCursor, "default");

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
	}

	private static int HoveredPoints (CurveTool tool)
		=> tool.Handles.OfType<CurveHandle> ().Count (h => h.Hovered);

	/// <summary>
	/// Backspace takes back the last point (4 -> 3 -> 2 -> cancel) and the arrow keys
	/// nudge the highlighted point.
	/// </summary>
	private static void TestKeyboardEditing ()
	{
		Log ("-- keyboard editing");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface blank = Snapshot (document);

		PlaceFourPoints (document);
		Check ("four points before backspace", ActiveHandles () == 6, ActiveHandles ().ToString ());

		PressKey (document, Gdk.Constants.KEY_BackSpace);
		Check ("backspace drops a control point", ActiveHandles () == 4, ActiveHandles ().ToString ());
		Check ("backspace does not commit", !Differs (blank, document), "layer untouched");

		PressKey (document, Gdk.Constants.KEY_BackSpace);
		Check ("backspace again leaves the line", ActiveHandles () == 2, ActiveHandles ().ToString ());

		PressKey (document, Gdk.Constants.KEY_BackSpace);
		Check ("backspace on a line cancels the curve", ActiveHandles () == 0, ActiveHandles ().ToString ());
		Check ("the image is still untouched", !Differs (blank, document), "layer untouched");

		// --- arrow keys nudge the highlighted point ---------------------------
		PlaceFourPoints (document);

		PointD control = new (270, 110);
		PintaCore.Tools.DoMouseMove (document, Args (control, MouseButton.None));

		PressKey (document, Gdk.Constants.KEY_Left);
		Check ("left arrow moves the point by one pixel",
			ControlPointPosition (tool) == new PointD (control.X - 1, control.Y),
			ControlPointPosition (tool).ToString ());

		PressKey (document, Gdk.Constants.KEY_Down, ModifierType.ShiftMask);
		Check ("shift + arrow moves by ten pixels",
			ControlPointPosition (tool) == new PointD (control.X - 1, control.Y + 10),
			ControlPointPosition (tool).ToString ());

		Check ("nudging does not commit", !Differs (blank, document), "layer untouched");
		Check ("the curve is still adjustable", ActiveHandles () == 6, ActiveHandles ().ToString ());

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
	}

	private static void PressKey (Document document, uint key, ModifierType state = 0)
	{
		ToolKeyEventArgs args = new () { Key = new Gdk.Key (key), State = state };
		PintaCore.Tools.DoKeyDown (document, args);
	}

	/// <summary>Canvas position of the end point marker (points[1]).</summary>
	private static PointD EndPointPosition (CurveTool tool)
		=> tool.Handles
			.OfType<CurveHandle> ()
			.Where (h => h.Kind == CurveHandleKind.Endpoint)
			.Last ()
			.CanvasPosition;

	/// <summary>Canvas position of the second control point marker.</summary>
	private static PointD ControlPointPosition (CurveTool tool)
		=> tool.Handles
			.OfType<CurveHandle> ()
			.Where (h => h.Kind == CurveHandleKind.ControlPoint)
			.Last ()
			.CanvasPosition;

	/// <summary>Canvas position of the first control point marker.</summary>
	private static PointD FirstControlPointPosition (CurveTool tool)
		=> tool.Handles
			.OfType<CurveHandle> ()
			.Where (h => h.Kind == CurveHandleKind.ControlPoint)
			.First ()
			.CanvasPosition;

	/// <summary>
	/// The optional dash pattern must reach the canvas: a solid pattern fills the whole
	/// line, a dashed one leaves gaps.
	/// </summary>
	private static void TestDashPattern ()
	{
		Log ("-- dash pattern");

		Document solid = DrawHorizontalLine ("-");
		int solid_painted = PaintedInRow (solid, y: 100, x1: 21, x2: 299);

		Document dashed = DrawHorizontalLine (" --");   // dash + space, so the caps cannot fill the gap
		int dashed_painted = PaintedInRow (dashed, y: 100, x1: 21, x2: 299);

		Check ("a solid line fills the row", solid_painted == 279, solid_painted.ToString ());
		Check ("a dashed line leaves gaps",
			dashed_painted > 0 && dashed_painted < solid_painted,
			$"{dashed_painted} of {solid_painted}");

		SaveResult (dashed, "dash-pattern.png");

		CurveTool tool = (CurveTool) curve_tool!;
		tool.DashPattern = "-";
	}

	/// <summary>Draws and commits a horizontal line with the given dash pattern.</summary>
	private static Document DrawHorizontalLine (string dash_pattern)
	{
		CurveTool tool = (CurveTool) curve_tool!;
		Document document = PintaCore.Workspace.NewDocument (new Size (320, 120), new Color (1, 1, 1));

		PintaCore.Tools.SetCurrentTool (tool);
		tool.UseAntialiasing = true;
		tool.BrushWidth = 2;
		tool.DashPattern = dash_pattern;
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Drag (document, new PointD (20, 100), new PointD (300, 100));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (300, 100), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (300, 100), MouseButton.Right));

		return document;
	}

	/// <summary>Number of non-white pixels in a row between x1 and x2.</summary>
	private static int PaintedInRow (Document document, int y, int x1, int x2)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		int painted = 0;

		for (int x = x1; x <= x2; ++x) {
			ColorBgra pixel = pixels[y * size.Width + x];

			if (pixel.R < 250 || pixel.G < 250 || pixel.B < 250)
				painted++;
		}

		return painted;
	}

	/// <summary>The topmost row of the layer that has something painted on it, or -1.</summary>
	private static int TopmostPaintedRow (Document document)
	{
		ImageSurface surface = document.Layers.CurrentUserLayer.Surface;
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		for (int y = 0; y < size.Height; ++y) {
			for (int x = 0; x < size.Width; ++x) {
				ColorBgra pixel = pixels[y * size.Width + x];

				if (pixel.R < 250 || pixel.G < 250 || pixel.B < 250)
					return y;
			}
		}

		return -1;
	}

	/// <summary>
	/// The status bar doubles as a readout while drawing: the point being placed, how many
	/// points exist, the curve size once it is complete, and the snapped angle.
	/// </summary>
	private static void TestStatusBar ()
	{
		Log ("-- status bar");

		CultureInfo original = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentUICulture = new CultureInfo ("en-US");

		try {
			TestStatusBarMessages ();
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	private static void TestStatusBarMessages ()
	{
		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);
		tool.UseAntialiasing = false;   // so that the angle readout is visible while dragging

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		Check ("the idle message tells the user what to do",
			tool.StatusMessage ().Contains ("start and end points"),
			tool.StatusMessage ());

		// --- while dragging the line, with an angle snap ---------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (40, 160), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (240, 60), MouseButton.None, ModifierType.ShiftMask));

		string dragging = tool.StatusMessage ();
		PointD end = EndPointPosition (tool);
		string end_text = $"P2: {Math.Round (end.X)}, {Math.Round (end.Y)}";

		Check ("the snapped angle is shown", dragging.Contains ("45°"), dragging);
		Check ("the dragged point is shown with its coordinates",
			dragging.Contains (end_text),
			$"{dragging} (expected {end_text})");
		Check ("the point count is shown", dragging.Contains ("2/4"), dragging);

		PintaCore.Tools.DoMouseUp (document, Args (new PointD (240, 60), MouseButton.Left, ModifierType.ShiftMask));

		// --- three points -----------------------------------------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (60, 40), MouseButton.Left));
		Check ("three points are counted", tool.StatusMessage ().Contains ("3/4"), tool.StatusMessage ());

		// --- four points: the size of the curve is reported -------------------
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (280, 120), MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (280, 120), MouseButton.Left));

		string complete = tool.StatusMessage ();
		Check ("the finished curve reports its size", complete.Contains ("size"), complete);
		Check ("and the finished point count", complete.Contains ("4/4"), complete);

		// --- hovering a point shows it ----------------------------------------
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (280, 120), MouseButton.None));
		Check ("hovering shows the point coordinates",
			tool.StatusMessage ().Contains ("280"),
			tool.StatusMessage ());

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
		tool.UseAntialiasing = true;
	}

	/// <summary>
	/// Scenarios that involve Pinta doing something behind the tool's back: changing the
	/// selection commits the shape, switching documents commits it into the document it
	/// was started in, and the control point markers must not care about the zoom.
	/// </summary>
	private static void TestEdgeCases ()
	{
		Log ("-- edge cases");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		// --- 1. a selection change mid-shape commits the curve -------------------
		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		// Invert Selection is only enabled while a selection is visible, and it is the
		// action that commits the current tool's work (see EditActions).
		PintaCore.Actions.Edit.SelectAll.Activate ();
		Check ("a selection exists", PintaCore.Actions.Edit.InvertSelection.Sensitive, "invert selection enabled");

		ImageSurface blank = Snapshot (document);

		StartTwoPointShape (tool, document);
		Check ("shape in progress before the selection change", ActiveHandles () == 2, ActiveHandles ().ToString ());
		Check ("the preview is on the tool layer", ToolLayerPixels (document) > 0, ToolLayerPixels (document).ToString ());

		PintaCore.Actions.Edit.InvertSelection.Activate ();

		Check ("the selection change commits the curve", Differs (blank, document), "layer changed");
		Check ("no handles survive the commit", ActiveHandles () == 0, ActiveHandles ().ToString ());
		Check ("the tool layer is empty again", ToolLayerPixels (document) == 0, ToolLayerPixels (document).ToString ());

		// --- 2. switching documents mid-shape commits into the right document ----
		Document first = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface first_blank = Snapshot (first);

		StartTwoPointShape (tool, first);
		Check ("shape in progress before the document switch", ActiveHandles () == 2, ActiveHandles ().ToString ());

		Document second = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		ImageSurface second_blank = Snapshot (second);

		Check ("the curve was committed into the document it was started in",
			Differs (first_blank, first), "first document changed");
		Check ("the new document is untouched",
			!Differs (second_blank, second), "second document unchanged");
		Check ("no handles are left behind", ActiveHandles () == 0, ActiveHandles ().ToString ());

		// --- 3. the control point markers are zoom independent -------------------
		Document zoomed = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		PintaCore.Tools.SetCurrentTool (tool);
		StartThreePointShape (tool, zoomed);

		List<(double Width, double Height)> at_quarter = ActiveMarkerSizes (tool, 0.25);
		List<(double Width, double Height)> at_normal = ActiveMarkerSizes (tool, 1.0);
		List<(double Width, double Height)> at_four = ActiveMarkerSizes (tool, 4.0);

		Check ("markers exist at every zoom",
			at_quarter.Count > 0 && at_quarter.Count == at_normal.Count && at_normal.Count == at_four.Count,
			$"{at_quarter.Count}/{at_normal.Count}/{at_four.Count}");

		bool stable = at_quarter.SequenceEqual (at_normal) && at_normal.SequenceEqual (at_four);
		Check ("marker size does not depend on the zoom", stable,
			$"0.25x={Describe (at_quarter)} 1x={Describe (at_normal)} 4x={Describe (at_four)}");

		PintaCore.Workspace.Scale = 1d;

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (zoomed, escape);
	}

	private static void StartTwoPointShape (CurveTool tool, Document document)
	{
		Drag (document, new PointD (40, 160), new PointD (200, 170));
	}

	private static void StartThreePointShape (CurveTool tool, Document document)
	{
		Drag (document, new PointD (40, 160), new PointD (200, 170));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (70, 30), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (70, 30), MouseButton.Left));
	}

	/// <summary>Window-space size of the end/control point markers, which must not scale.</summary>
	private static List<(double Width, double Height)> ActiveMarkerSizes (CurveTool tool, double scale)
	{
		PintaCore.Workspace.Scale = scale;

		List<(double, double)> sizes = [];

		foreach (CurveHandle handle in tool.Handles.OfType<CurveHandle> ()) {
			if (!handle.Active || handle.Kind == CurveHandleKind.Leash)
				continue;

			RectangleI rect = handle.InvalidateRect;
			sizes.Add ((rect.Width, rect.Height));
		}

		return sizes;
	}

	private static string Describe (List<(double Width, double Height)> sizes)
		=> string.Join (" ", sizes.Select (s => $"{s.Width}x{s.Height}"));

	/// <summary>Number of non-transparent pixels on the tool layer (the preview).</summary>
	private static int ToolLayerPixels (Document document)
	{
		ImageSurface surface = document.Layers.ToolLayer.Surface;
		surface.Flush ();

		Size size = surface.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = surface.GetReadOnlyPixelData ();

		int painted = 0;

		for (int i = 0; i < size.Width * size.Height; ++i)
			if (pixels[i].A > 0)
				painted++;

		return painted;
	}

	/// <summary>
	/// The screen has to show the layer and nothing else once the tool is not drawing
	/// anything. This is the check that catches a leftover frame, a preview that was not
	/// cleared or a handle that stayed behind: the data can be right while the screen is
	/// wrong, and only a render of the canvas can tell the two apart.
	///
	/// The comparison is proved to work in both directions: with the curve tool current
	/// the markers are on the canvas, so the canvas must differ from the layer; with a
	/// tool that draws nothing on top of it, the canvas must be the layer exactly.
	/// </summary>
	private static void TestCanvasMatchesTheLayer ()
	{
		Log ("-- the canvas is the layer");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Tools.SetCurrentTool (tool);
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		// Something on the layer, so that the comparison has pixels to look at.
		Drag (document, new PointD (40, 160), new PointD (200, 170));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (200, 170), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (200, 170), MouseButton.Right));

		// A curve that is still adjustable: its markers are drawn over the canvas.
		Drag (document, new PointD (60, 40), new PointD (260, 120));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (100, 150), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (110, 140), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (110, 140), MouseButton.Left));

		Check ("the curve is still adjustable", ActiveHandles () > 0, ActiveHandles ().ToString ());

		PumpEvents ();

		using (Gdk.Texture? with_markers = RenderCanvas ()) {
			Check ("the canvas renders", with_markers is not null,
				with_markers is null ? "nothing came out" : $"{with_markers.Width}x{with_markers.Height}");

			if (with_markers is not null) {
				int drawn = PixelsDiffering (with_markers, document);
				Check ("the markers are drawn over the canvas", drawn > 0, $"{drawn} pixels differ");
			}
		}

		// Switching to a tool that draws nothing on the canvas has to take them away.
		BaseTool pencil = PintaCore.Tools.First (t => t.GetType ().Name == "PencilTool");
		PintaCore.Tools.SetCurrentTool (pencil);
		PumpEvents ();

		using Gdk.Texture? texture = RenderCanvas ();

		Check ("the canvas renders without the tool", texture is not null,
			texture is null ? "nothing came out" : $"{texture.Width}x{texture.Height}");

		if (texture is null)
			return;

		int differing = PixelsDiffering (texture, document);

		Check ("the canvas is the layer, with nothing drawn over it",
			differing == 0,
			$"{differing} pixels differ");

		// The comparison has to be able to fail: change the layer under the same picture,
		// and the two must differ.
		using (Context g = new (document.Layers.CurrentUserLayer.Surface)) {
			g.SetSourceColor (new Color (1, 0, 0));
			g.Rectangle (0, 0, 20, 20);
			g.Fill ();
		}

		int after_change = PixelsDiffering (texture, document);

		Check ("the comparison notices a difference", after_change > 0, $"{after_change} pixels differ");
	}

	/// <summary>
	/// The same drag, once with Pinta's own Line/Curve tool and once with the curve tool,
	/// has to end up as the same pixels. This is what catches a stroke that is drawn with
	/// a different width, cap or antialiasing than the rest of Pinta.
	/// </summary>
	private static void TestMatchesPintasOwnTool ()
	{
		Log ("-- the same line as Pinta's own tool");

		CurveTool tool = (CurveTool) curve_tool!;
		BaseTool line_tool = PintaCore.Tools.First (t => t.GetType ().Name == "LineCurveTool");

		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		// Pinta's own tool: the shape stays editable until Enter finalizes it.
		Document native = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		PintaCore.Tools.SetCurrentTool (line_tool);
		Drag (native, new PointD (40, 60), new PointD (280, 140));
		FinishWithEnter (native);

		// The curve tool: two points and the other mouse button make a plain line.
		// Pinta's own tool ends its strokes with a square cap, so the curve tool is asked
		// for the same ends - the comparison is about the stroke, not about the default.
		EndShape start_end = tool.StartEnd;
		EndShape finish_end = tool.FinishEnd;

		tool.StartEnd = EndShape.Square;
		tool.FinishEnd = EndShape.Square;

		Document ours = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));
		PintaCore.Tools.SetCurrentTool (tool);
		Drag (ours, new PointD (40, 60), new PointD (280, 140));
		PintaCore.Tools.DoMouseDown (ours, Args (new PointD (280, 140), MouseButton.Right));
		PintaCore.Tools.DoMouseUp (ours, Args (new PointD (280, 140), MouseButton.Right));

		tool.StartEnd = start_end;
		tool.FinishEnd = finish_end;

		ImageSurface native_layer = native.Layers.CurrentUserLayer.Surface;
		ImageSurface our_layer = ours.Layers.CurrentUserLayer.Surface;

		SaveSurface (native_layer, "native-line.png");
		SaveSurface (our_layer, "curve-line.png");

		(bool same, string detail) = CompareStrokes (native_layer, our_layer);

		Check ("a two point curve is the same as Pinta's own line", same, detail);

		// The check has to be able to fail: a different width must show up.
		using (Context g = new (our_layer)) {
			g.SetSourceColor (new Color (0, 0, 0));
			g.Rectangle (0, 0, 10, 10);
			g.Fill ();
		}

		(bool still_same, string after_detail) = CompareStrokes (native_layer, our_layer);

		Check ("the stroke comparison notices a difference", !still_same, after_detail);
	}

	/// <summary>
	/// Zoom is a way of looking at the image, not part of it: the same drag has to put
	/// the same pixels on the layer at every zoom level.
	/// </summary>
	private static void TestZoomDoesNotChangeTheResult ()
	{
		Log ("-- the zoom does not change the result");

		CurveTool tool = (CurveTool) curve_tool!;
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);

		ImageSurface? reference = null;
		double reference_scale = 0;
		List<string> differences = [];

		foreach (double scale in new[] { 1d, 4d, 36d }) {
			Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

			// The scale is set after the document is made: a new document resets it.
			PintaCore.Workspace.Scale = scale;
			PumpEvents ();

			PintaCore.Tools.SetCurrentTool (tool);

			Drag (document, new PointD (40, 60), new PointD (280, 140));
			PintaCore.Tools.DoMouseDown (document, Args (new PointD (120, 40), MouseButton.Left));
			PintaCore.Tools.DoMouseMove (document, Args (new PointD (130, 30), MouseButton.None));
			PintaCore.Tools.DoMouseUp (document, Args (new PointD (130, 30), MouseButton.Left));
			FinishWithEnter (document);

			ImageSurface layer = Snapshot (document);

			if (reference is null) {
				reference = layer;
				reference_scale = scale;
				continue;
			}

			int differing = SurfacesDiffering (reference, layer);

			differences.Add ($"{scale}x: {differing}");

			Check ($"the result at {scale}x is the same as at {reference_scale}x",
				differing == 0,
				$"{differing} pixels differ");
		}

		PintaCore.Workspace.Scale = 1d;

		Log ($"   zoom matrix: {string.Join (", ", differences)}");
	}

	/// <summary>
	/// Draws the same half-transparent curve twice: once directly, once with extra mouse
	/// moves that force repeated preview redraws. Both results must be identical - if the
	/// incremental preview painted over itself instead of a cleared region, the second
	/// one would come out darker.
	/// </summary>
	private static void TestTranslucentRedrawIsStable ()
	{
		Log ("-- translucent preview");

		Document once = DrawTranslucentCurve (extra_moves: 0);
		Document many = DrawTranslucentCurve (extra_moves: 30);

		bool identical = SurfaceDiff.Create (
			once.Layers.CurrentUserLayer.Surface,
			many.Layers.CurrentUserLayer.Surface) is null;

		Check ("repeated preview redraws do not accumulate", identical,
			identical ? "identical" : "the layers differ");
	}

	private static Document DrawTranslucentCurve (int extra_moves)
	{
		Document document = PintaCore.Workspace.NewDocument (new Size (320, 200), new Color (1, 1, 1));

		PintaCore.Tools.SetCurrentTool (curve_tool!);

		// 50% black: any double compositing shows up immediately.
		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0, 0.5);

		Drag (document, new PointD (40, 160), new PointD (200, 170));
		PintaCore.Tools.DoMouseDown (document, Args (new PointD (60, 40), MouseButton.Left));
		PintaCore.Tools.DoMouseMove (document, Args (new PointD (70, 30), MouseButton.None));
		PintaCore.Tools.DoMouseUp (document, Args (new PointD (70, 30), MouseButton.Left));

		PointD last_control = new (270, 110);
		PintaCore.Tools.DoMouseDown (document, Args (last_control, MouseButton.Left));

		for (int i = 0; i < extra_moves; ++i)
			PintaCore.Tools.DoMouseMove (document, Args (last_control, MouseButton.None));

		PintaCore.Tools.DoMouseUp (document, Args (last_control, MouseButton.Left));
		FinishWithEnter (document);

		return document;
	}

	private static void TestPreviewPerformance ()
	{
		Log ("-- preview performance");

		const int width = 3000;
		const int height = 2000;

		Document document = PintaCore.Workspace.NewDocument (new Size (width, height), new Color (1, 1, 1));

		PointD start = new (100, 1800);
		PointD end = new (900, 1800);
		PointD first_control = new (300, 1200);
		PointD last_control = new (700, 1200);

		PintaCore.Palette.PrimaryColor = new Color (0, 0, 0);
		Drag (document, start, end);
		PintaCore.Tools.DoMouseDown (document, Args (first_control, MouseButton.Left));
		PintaCore.Tools.DoMouseUp (document, Args (first_control, MouseButton.Left));
		PintaCore.Tools.DoMouseDown (document, Args (last_control, MouseButton.Left));

		const int steps = 60;
		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew ();

		for (int i = 0; i < steps; ++i) {
			double t = i / (double) (steps - 1);
			PointD point = new (
				last_control.X - 60 + 120 * t,
				last_control.Y - 40 + 80 * t);

			PintaCore.Tools.DoMouseMove (document, Args (point, MouseButton.None));
		}

		watch.Stop ();

		double average = watch.Elapsed.TotalMilliseconds / steps;

		// The area the preview actually has to repaint, from the geometry of this test.
		double curve_area = (end.X - start.X + 2 * BrushWidthSlack) * (start.Y - first_control.Y + 2 * BrushWidthSlack);
		double canvas_area = (double) width * height;

		Log ($"canvas {width}x{height}: {steps} moves in {watch.Elapsed.TotalMilliseconds:0} ms " +
			$"(avg {average:0.00} ms/move); curve covers {curve_area / canvas_area:P1} of the canvas");
		Log ($"preview cost per move: {average:0.00} ms");

		Check ("large canvas preview stays responsive", average < 25d, $"{average:0.00} ms/move");

		ToolKeyEventArgs escape = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Escape) };
		PintaCore.Tools.DoKeyDown (document, escape);
	}

	private const double BrushWidthSlack = 8;

	#endregion Test driver

	#region Helpers

	private static PointD Angle (double degrees, double distance)
	{
		double radians = degrees * Math.PI / 180d;
		return new PointD (Math.Cos (radians) * distance, Math.Sin (radians) * distance);
	}

	private static void CheckSnap (string name, PointD end, bool shift, bool control, PointD expected, double expected_degrees)
	{
		PointD actual = CurveGeometry.SnapToAngles (new PointD (0, 0), end, shift, control, out double radians);
		double degrees = radians * 180d / Math.PI;

		bool position_ok = Math.Abs (actual.X - expected.X) < 0.001 && Math.Abs (actual.Y - expected.Y) < 0.001;
		bool angle_ok = Math.Abs (degrees - expected_degrees) < 0.001;

		Check (name, position_ok && angle_ok, $"{actual} @ {degrees:0.##}deg");
	}

	private static void Check (string name, bool passed, string detail)
	{
		if (!passed)
			failures++;

		Log ($"[{(passed ? "PASS" : "FAIL")}] {name} ({detail})");
	}

	private static int ActiveHandles ()
		=> curve_tool?.Handles.Count (h => h.Active) ?? 0;

	private static ImageSurface Snapshot (Document document)
	{
		ImageSurface snapshot = document.Layers.CurrentUserLayer.Surface.Clone ();
		return snapshot;
	}

	private static bool Differs (ImageSurface reference, Document document)
		=> SurfaceDiff.Create (reference, document.Layers.CurrentUserLayer.Surface) is not null;

	private static ToolMouseEventArgs Args (PointD point, MouseButton button, ModifierType state = 0)
	{
		// The tool hit-tests the control points in window space, so mirror what the canvas
		// would report.
		PointD window = PintaCore.Workspace.HasOpenDocuments
			? PintaCore.Workspace.ActiveWorkspace.CanvasPointToView (point)
			: point;

		return new () {
			PointDouble = point,
			WindowPoint = window,
			RootPoint = window,
			MouseButton = button,
			State = state,
		};
	}

	private static void FinishWithEnter (Document document)
	{
		ToolKeyEventArgs args = new () { Key = new Gdk.Key (Gdk.Constants.KEY_Return) };
		PintaCore.Tools.DoKeyDown (document, args);
	}

	private static void Drag (
		Document document,
		PointD start,
		PointD end,
		ModifierType state = 0,
		MouseButton button = MouseButton.Left)
	{
		PintaCore.Tools.DoMouseDown (document, Args (start, button, state));
		PintaCore.Tools.DoMouseMove (document, Args (end, MouseButton.None, state));
		PintaCore.Tools.DoMouseUp (document, Args (end, button, state));
	}

	/// <summary>
	/// Runs the main loop until it has nothing left to do. A widget that was just created
	/// has no size until it has been laid out, and the canvas only redraws when the loop
	/// gets a turn.
	///
	/// Never call this from inside a render: it would process the events of the test
	/// itself and the next render would come out empty.
	/// </summary>
	private static void PumpEvents (int rounds = 20)
	{
		GLib.MainContext context = GLib.MainContext.Default ();

		for (int i = 0; i < rounds; ++i)
			while (context.Iteration (false)) { }
	}

	/// <summary>
	/// Renders the canvas widget itself - the image, the selection outline and the tool
	/// markers, exactly what is on the screen - into a texture.
	/// </summary>
	private static Gdk.Texture? RenderCanvas ()
	{
		// Pinta.Gui.Widgets is not referenced by this project, so the canvas is reached
		// through the workspace and reflection.
		object? canvas_window = PintaCore.Workspace.ActiveWorkspace.CanvasWindow;
		object? canvas = canvas_window?.GetType ().GetProperty ("Canvas")?.GetValue (canvas_window);

		if (canvas is not Gtk.Widget widget) {
			Log ($"   canvas widget not found (canvas_window={canvas_window?.GetType ().Name ?? "null"})");
			return null;
		}

		Log ($"   canvas widget: {widget.GetType ().Name} {widget.GetWidth ()}x{widget.GetHeight ()} " +
			$"mapped={widget.GetMapped ()} scale={PintaCore.Workspace.ActiveWorkspace.Scale}");

		if (widget.GetWidth () <= 0 || widget.GetHeight () <= 0)
			return null;

		Gtk.WidgetPaintable paintable = Gtk.WidgetPaintable.New (widget);
		Gsk.RenderNode? node = null;

		// The first snapshot after a widget was laid out can come out empty, so it is
		// tried a few times before giving up.
		for (int attempt = 0; attempt < 5 && node is null; ++attempt) {
			Gtk.Snapshot snapshot = Gtk.Snapshot.New ();

			paintable.Snapshot (snapshot, widget.GetWidth (), widget.GetHeight ());

			node = snapshot.ToNode ();

			if (node is null)
				PumpEvents (5);
		}

		if (node is null) {
			// A Gtk.Picture keeps its contents in a paintable of its own. When the widget
			// snapshot comes out empty (the picture has not been drawn since it was
			// invalidated), that paintable is what the canvas would show.
			Gdk.Paintable? paintable_of_picture = (widget as Gtk.Picture)?.GetPaintable ();

			if (paintable_of_picture is not null) {
				Gtk.Snapshot fallback = Gtk.Snapshot.New ();

				paintable_of_picture.Snapshot (fallback, widget.GetWidth (), widget.GetHeight ());

				node = fallback.ToNode ();

				Log ($"   render: the widget snapshot was empty, the picture paintable " +
					$"{(node is null ? "was empty too" : "worked")}");
			}
		}

		if (node is null) {
			Log ($"   render: the snapshot is empty (mapped={widget.GetMapped ()} " +
				$"realized={widget.GetRealized ()} {widget.GetWidth ()}x{widget.GetHeight ()})");
			return null;
		}

		Gsk.CairoRenderer renderer = Gsk.CairoRenderer.New ();
		renderer.Realize (null);

		Gdk.Texture texture = renderer.RenderTexture (node, null);
		renderer.Unrealize ();

		return texture;
	}

	/// <summary>
	/// How many pixels of a rendered canvas differ from the layer. The canvas is rendered
	/// at 1:1, so its pixels are the layer's pixels; anything the tool draws on top of the
	/// image shows up here.
	/// </summary>
	private static int PixelsDiffering (Gdk.Texture texture, Document document)
	{
		string path = Path.Combine (OutputDirectory, "canvas-vs-layer.png");
		texture.SaveToPng (path);

		using GdkPixbuf.Pixbuf? rendered = GdkPixbuf.Pixbuf.NewFromFile (path);

		if (rendered is null) {
			Log ("   the rendered canvas could not be read back");
			return -1;
		}

		ImageSurface layer = document.Layers.CurrentUserLayer.Surface;
		layer.Flush ();

		Size size = layer.GetSize ();
		ReadOnlySpan<ColorBgra> pixels = layer.GetReadOnlyPixelData ();

		int stride = rendered.GetRowstride ();
		int length = stride * rendered.GetHeight ();
		ReadOnlySpan<byte> data = rendered.ReadPixelBytes ().GetRegionSpan<byte> (0, (nuint) length);

		int differing = 0;

		for (int y = 0; y < Math.Min (size.Height, rendered.GetHeight ()); ++y) {
			for (int x = 0; x < Math.Min (size.Width, rendered.GetWidth ()); ++x) {
				int offset = y * stride + x * 4;

				ColorBgra pixel = pixels[y * size.Width + x];

				// A transparent pixel of the layer shows the transparent background of the
				// canvas, which has no colour of its own to compare with.
				if (pixel.A == 0)
					continue;

				if (Math.Abs (data[offset] - pixel.R) > 2 ||
					Math.Abs (data[offset + 1] - pixel.G) > 2 ||
					Math.Abs (data[offset + 2] - pixel.B) > 2)
					differing++;
			}
		}

		return differing;
	}

	/// <summary>How many pixels of two surfaces differ, alpha included.</summary>
	private static int SurfacesDiffering (ImageSurface a, ImageSurface b)
	{
		a.Flush ();
		b.Flush ();

		Size size = a.GetSize ();

		if (size != b.GetSize ())
			return int.MaxValue;

		ReadOnlySpan<ColorBgra> pa = a.GetReadOnlyPixelData ();
		ReadOnlySpan<ColorBgra> pb = b.GetReadOnlyPixelData ();

		int differing = 0;

		for (int i = 0; i < pa.Length; ++i)
			if (pa[i].BGRA != pb[i].BGRA)
				differing++;

		return differing;
	}

	/// <summary>
	/// Compares two strokes the way a person would: the same area is covered, and no
	/// pixel is more than a hair different.
	///
	/// Two different rasterisers - Pinta's shape engine flattens the line into a polyline,
	/// the curve tool strokes a Bezier - never agree to the last bit of antialiasing, so a
	/// small tolerance is allowed. What is not allowed is a different covered area, a
	/// different bounding box or a different width: those are the mistakes that matter.
	/// </summary>
	private static (bool Same, string Detail) CompareStrokes (ImageSurface a, ImageSurface b)
	{
		a.Flush ();
		b.Flush ();

		Size size = a.GetSize ();

		if (size != b.GetSize ())
			return (false, $"different sizes: {size.Width}x{size.Height} and {b.GetSize ().Width}x{b.GetSize ().Height}");

		ReadOnlySpan<ColorBgra> pa = a.GetReadOnlyPixelData ();
		ReadOnlySpan<ColorBgra> pb = b.GetReadOnlyPixelData ();

		const int tolerance = 16;

		int over = 0;
		int max_delta = 0;
		int painted_a = 0;
		int painted_b = 0;
		int left_a = int.MaxValue, top_a = int.MaxValue, right_a = -1, bottom_a = -1;
		int left_b = int.MaxValue, top_b = int.MaxValue, right_b = -1, bottom_b = -1;

		for (int y = 0; y < size.Height; ++y) {
			for (int x = 0; x < size.Width; ++x) {
				int i = y * size.Width + x;

				ColorBgra ca = pa[i];
				ColorBgra cb = pb[i];

				int delta = Math.Max (
					Math.Abs (ca.R - cb.R),
					Math.Max (Math.Abs (ca.G - cb.G), Math.Abs (ca.B - cb.B)));

				if (delta > max_delta)
					max_delta = delta;

				if (delta > tolerance)
					over++;

				if (ca.A > 0 && (ca.R < 250 || ca.G < 250 || ca.B < 250)) {
					painted_a++;
					left_a = Math.Min (left_a, x);
					top_a = Math.Min (top_a, y);
					right_a = Math.Max (right_a, x);
					bottom_a = Math.Max (bottom_a, y);
				}

				if (cb.A > 0 && (cb.R < 250 || cb.G < 250 || cb.B < 250)) {
					painted_b++;
					left_b = Math.Min (left_b, x);
					top_b = Math.Min (top_b, y);
					right_b = Math.Max (right_b, x);
					bottom_b = Math.Max (bottom_b, y);
				}
			}
		}

		bool same_box =
			left_a == left_b && top_a == top_b && right_a == right_b && bottom_a == bottom_b;

		// A pixel or two of antialiasing at the edge is fine; a different width is not.
		bool same_count = Math.Abs (painted_a - painted_b) <= Math.Max (2, painted_a / 100);

		bool same = over == 0 && same_box && same_count;

		return (same,
			$"{painted_a} vs {painted_b} painted pixels, " +
			$"box {left_a},{top_a}..{right_a},{bottom_a} vs {left_b},{top_b}..{right_b},{bottom_b}, " +
			$"max channel delta {max_delta}, {over} pixels over {tolerance}");
	}

	/// <summary>Render the active handle markers into a PNG so they can be inspected.</summary>
	private static string RenderHandles (BaseTool tool)
	{
		Gtk.Snapshot snapshot = Gtk.Snapshot.New ();

		foreach (IToolHandle handle in tool.Handles)
			if (handle.Active)
				handle.Draw (snapshot);

		Gsk.RenderNode? node = snapshot.ToNode ();
		if (node is null)
			return "no node";

		Gsk.CairoRenderer renderer = Gsk.CairoRenderer.New ();
		renderer.Realize (null);
		Gdk.Texture texture = renderer.RenderTexture (node, null);
		texture.SaveToPng (Path.Combine (OutputDirectory, "handles.png"));
		renderer.Unrealize ();

		return $"{texture.Width}x{texture.Height}";
	}

	private static void SaveSurface (ImageSurface surface, string name)
	{
		using GdkPixbuf.Pixbuf pixbuf = surface.ToPixbuf (true);
		pixbuf.Savev (Path.Combine (OutputDirectory, name), "png", [], []);
	}

	private static void SaveResult (Document document, string name)
	{
		using GdkPixbuf.Pixbuf pixbuf = document.Layers.CurrentUserLayer.Surface.ToPixbuf (false);
		pixbuf.Savev (Path.Combine (OutputDirectory, name), "png", [], []);
	}

	internal static void Log (string message)
		=> File.AppendAllText (Path.Combine (OutputDirectory, "smoke-test.log"), message + Environment.NewLine);

	#endregion Helpers
}
