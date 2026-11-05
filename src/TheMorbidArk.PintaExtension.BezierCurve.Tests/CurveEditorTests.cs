//
// CurveEditorTests.cs
//
// The state machine behind the tool, now testable without a display: which point is
// dragged, when the shape becomes adjustable, when the angle snapping applies and what
// backspace does.
//
using NUnit.Framework;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class CurveEditorTests
{
	private CurveEditor editor = null!;

	[SetUp]
	public void SetUp () => editor = new CurveEditor ();

	private void BeginAndPlaceLine (PointD start = default, PointD end = default)
	{
		editor.Begin (start == default ? new PointD (10, 10) : start, MouseButton.Left);
		editor.DragTo (end == default ? new PointD (110, 10) : end, false, false);
		editor.EndDrag ();
	}

	private void PlaceFourPoints ()
	{
		BeginAndPlaceLine ();
		editor.AddControlPoint (new PointD (30, -40));
		editor.EndDrag ();
		editor.AddControlPoint (new PointD (90, -40));
		editor.EndDrag ();
	}

	[Test]
	public void InitiallyEmpty ()
	{
		Assert.That (editor.Count, Is.Zero);
		Assert.That (editor.HasShape, Is.False);
		Assert.That (editor.IsAdjustable, Is.False);
		Assert.That (editor.IsDragging, Is.False);
		Assert.That (editor.OriginButton, Is.EqualTo (MouseButton.None));
	}

	[Test]
	public void BeginPutsBothEndPointsUnderTheCursorAndDragsTheEnd ()
	{
		editor.Begin (new PointD (5, 6), MouseButton.Right);

		Assert.That (editor.Count, Is.EqualTo (2));
		Assert.That (editor.Points[0], Is.EqualTo (new PointD (5, 6)));
		Assert.That (editor.Points[1], Is.EqualTo (new PointD (5, 6)));
		Assert.That (editor.DraggingIndex, Is.EqualTo (1));
		Assert.That (editor.SelectedIndex, Is.EqualTo (1));
		Assert.That (editor.OriginButton, Is.EqualTo (MouseButton.Right));
		Assert.That (editor.HasShape, Is.True);
		Assert.That (editor.IsAdjustable, Is.False);
	}

	[Test]
	public void DraggingTheInitialLineMovesTheEndPoint ()
	{
		editor.Begin (new PointD (10, 10), MouseButton.Left);
		editor.DragTo (new PointD (100, 40), false, false);

		Assert.That (editor.Points[0], Is.EqualTo (new PointD (10, 10)));
		Assert.That (editor.Points[1], Is.EqualTo (new PointD (100, 40)));
		Assert.That (double.IsNaN (editor.SnappedRadians), Is.True);
	}

	[Test]
	public void TheInitialLineSnapsWithShift ()
	{
		editor.Begin (new PointD (0, 0), MouseButton.Left);
		editor.DragTo (new PointD (100, 40), shift_pressed: true, control_pressed: false);

		// 21.8 degrees is closest to 0 degrees, so the end point is flattened onto the start's y.
		Assert.That (editor.Points[1].Y, Is.EqualTo (0).Within (1e-9));
		Assert.That (editor.Points[1].X, Is.EqualTo (100).Within (1e-9));
		Assert.That (editor.SnappedRadians * 180 / System.Math.PI, Is.EqualTo (0).Within (1e-6));
	}

	[Test]
	public void TheInitialLineSnapsTo30DegreesWithControl ()
	{
		editor.Begin (new PointD (0, 0), MouseButton.Left);

		double cos = System.Math.Cos (25 * System.Math.PI / 180);
		double sin = System.Math.Sin (25 * System.Math.PI / 180);
		editor.DragTo (new PointD (100 * cos, 100 * sin), shift_pressed: false, control_pressed: true);

		Assert.That (editor.SnappedRadians * 180 / System.Math.PI, Is.EqualTo (30).Within (1e-6));
		Assert.That (editor.Points[1].X, Is.EqualTo (100 * System.Math.Cos (System.Math.PI / 6)).Within (1e-6));
		Assert.That (editor.Points[1].Y, Is.EqualTo (100 * System.Math.Sin (System.Math.PI / 6)).Within (1e-6));
	}

	[Test]
	public void ControlPointsNeverSnap ()
	{
		BeginAndPlaceLine ();

		editor.AddControlPoint (new PointD (30, 20));
		editor.DragTo (new PointD (60, 55), shift_pressed: true, control_pressed: true);

		Assert.That (editor.Points[2], Is.EqualTo (new PointD (60, 55)));
		Assert.That (double.IsNaN (editor.SnappedRadians), Is.True);
	}

	[Test]
	public void AddControlPointAppendsAndDragsIt ()
	{
		BeginAndPlaceLine ();

		editor.AddControlPoint (new PointD (30, -40));

		Assert.That (editor.Count, Is.EqualTo (3));
		Assert.That (editor.DraggingIndex, Is.EqualTo (2));
		Assert.That (editor.SelectedIndex, Is.EqualTo (2));
		Assert.That (editor.IsAdjustable, Is.False);

		editor.DragTo (new PointD (35, -45), false, false);
		Assert.That (editor.Points[2], Is.EqualTo (new PointD (35, -45)));
	}

	[Test]
	public void FourPointsMakeTheShapeAdjustable ()
	{
		PlaceFourPoints ();

		Assert.That (editor.Count, Is.EqualTo (4));
		Assert.That (editor.IsAdjustable, Is.True);
		Assert.That (editor.IsDragging, Is.False);
	}

	[Test]
	public void GrabMovesAnyPointAndRejectsInvalidIndexes ()
	{
		PlaceFourPoints ();

		Assert.That (editor.Grab (0), Is.True);
		Assert.That (editor.DraggingIndex, Is.Zero);

		editor.DragTo (new PointD (-5, -5), false, false);

		Assert.That (editor.Points[0], Is.EqualTo (new PointD (-5, -5)));
		Assert.That (editor.Points[1], Is.EqualTo (new PointD (110, 10)));

		Assert.That (editor.Grab (-1), Is.False);
		Assert.That (editor.Grab (4), Is.False);
	}

	[Test]
	public void EndDragStopsDraggingButKeepsThePoints ()
	{
		editor.Begin (new PointD (10, 10), MouseButton.Left);
		editor.DragTo (new PointD (50, 50), shift_pressed: true, control_pressed: false);
		Assert.That (editor.IsDragging, Is.True);

		editor.EndDrag ();

		Assert.That (editor.IsDragging, Is.False);
		Assert.That (editor.DraggingIndex, Is.EqualTo (-1));
		Assert.That (double.IsNaN (editor.SnappedRadians), Is.True);
		Assert.That (editor.Count, Is.EqualTo (2));
		Assert.That (editor.Points[1].X, Is.EqualTo (50).Within (1e-9));
		Assert.That (editor.Points[1].Y, Is.EqualTo (50).Within (1e-9));

		// Dragging after the release must not move anything.
		PointD after_release = editor.Points[1];
		editor.DragTo (new PointD (0, 0), false, false);
		Assert.That (editor.Points[1], Is.EqualTo (after_release));
	}

	[Test]
	public void RemoveLastPointWalksBackToNothing ()
	{
		PlaceFourPoints ();

		editor.RemoveLastPoint ();
		Assert.That (editor.Count, Is.EqualTo (3));
		Assert.That (editor.HasShape, Is.True);
		Assert.That (editor.SelectedIndex, Is.EqualTo (2));

		editor.RemoveLastPoint ();
		Assert.That (editor.Count, Is.EqualTo (2));

		editor.RemoveLastPoint ();
		Assert.That (editor.Count, Is.Zero);
		Assert.That (editor.HasShape, Is.False);
		Assert.That (editor.OriginButton, Is.EqualTo (MouseButton.None));
	}

	[Test]
	public void SetPointNudgesASinglePoint ()
	{
		PlaceFourPoints ();

		Assert.That (editor.SetPoint (3, new PointD (95, -35)), Is.True);

		Assert.That (editor.Points[3], Is.EqualTo (new PointD (95, -35)));
		Assert.That (editor.Points[2], Is.EqualTo (new PointD (30, -40)));
		Assert.That (editor.SelectedIndex, Is.EqualTo (3));
	}

	[Test]
	public void SetPointRejectsInvalidIndexes ()
	{
		PlaceFourPoints ();

		Assert.That (editor.SetPoint (4, new PointD (1, 1)), Is.False);
		Assert.That (editor.SetPoint (-1, new PointD (1, 1)), Is.False);
	}

	[Test]
	public void TheCurveUsesTheLastTwoPointsAsControls ()
	{
		PlaceFourPoints ();

		BezierCurve curve = editor.Curve;

		Assert.That (curve.P0, Is.EqualTo (new PointD (10, 10)));
		Assert.That (curve.P3, Is.EqualTo (new PointD (110, 10)));
		Assert.That (curve.P1, Is.EqualTo (new PointD (30, -40)));
		Assert.That (curve.P2, Is.EqualTo (new PointD (90, -40)));
	}

	[Test]
	public void EditedIndexPrefersTheDraggedPointThenTheHoveredOne ()
	{
		PlaceFourPoints ();

		editor.SetHover (2);
		Assert.That (editor.EditedIndex, Is.EqualTo (2));

		editor.Grab (0);
		Assert.That (editor.EditedIndex, Is.Zero);

		editor.EndDrag ();
		Assert.That (editor.EditedIndex, Is.EqualTo (2));
	}

	[Test]
	public void ResetClearsEverything ()
	{
		PlaceFourPoints ();
		editor.SetHover (1);

		editor.Reset ();

		Assert.That (editor.Count, Is.Zero);
		Assert.That (editor.HoveredIndex, Is.EqualTo (-1));
		Assert.That (editor.SelectedIndex, Is.EqualTo (-1));
		Assert.That (editor.DraggingIndex, Is.EqualTo (-1));
		Assert.That (editor.OriginButton, Is.EqualTo (MouseButton.None));
	}

	[Test]
	public void EndDragReportsThatAControlPointWasPlaced ()
	{
		editor.Begin (new PointD (0, 0), MouseButton.Left);
		editor.DragTo (new PointD (100, 0), false, false);

		// The initial line is not a control point.
		Assert.That (editor.EndDrag (), Is.False);

		editor.AddControlPoint (new PointD (30, -40));
		Assert.That (editor.EndDrag (), Is.True);

		// The report is only for the drag that placed it.
		Assert.That (editor.EndDrag (), Is.False);
	}


	[Test]
	public void RestorePutsThePointsBack ()
	{
		PointD[] points = [new (0, 0), new (100, 0), new (30, -40), new (70, -40)];

		editor.Restore (points, MouseButton.Right);

		Assert.That (editor.Count, Is.EqualTo (4));
		Assert.That (editor.IsAdjustable, Is.True);
		Assert.That (editor.OriginButton, Is.EqualTo (MouseButton.Right));
		Assert.That (editor.DraggingIndex, Is.EqualTo (-1));
		Assert.That (editor.Points[2], Is.EqualTo (points[2]));
		Assert.That (editor.Points[3], Is.EqualTo (points[3]));
	}
}
