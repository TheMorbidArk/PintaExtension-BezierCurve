//
// CurveEditor.cs
//
// The state machine of the curve tool. Deliberately free of GTK, Cairo and Pinta
// services so that it can be unit tested: CurveTool owns everything that touches the
// application (preview layer, history, markers, cursors) and forwards what the user does
// to this class.
//
using System.Collections.Generic;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

internal sealed class CurveEditor
{
	// points[0] is the start, points[1] the end and points[2..3] the control points, so
	// the list always holds 0, 2, 3 or 4 points.
	private readonly List<PointD> points = new (4);

	internal IReadOnlyList<PointD> Points => points;

	internal int Count => points.Count;

	/// <summary>A curve needs at least its two end points.</summary>
	internal bool HasShape => points.Count >= 2;

	/// <summary>Both control points are placed, so the curve can be nudged around.</summary>
	internal bool IsAdjustable => points.Count >= 4;

	/// <summary>The button that started the shape; it also picks the color.</summary>
	internal MouseButton OriginButton { get; private set; } = MouseButton.None;

	/// <summary>Index of the point currently being dragged, or -1.</summary>
	internal int DraggingIndex { get; private set; } = -1;

	/// <summary>Last point that was grabbed; the arrow keys nudge it.</summary>
	internal int SelectedIndex { get; private set; } = -1;

	/// <summary>Index of the point under the cursor, or -1.</summary>
	internal int HoveredIndex { get; private set; } = -1;

	/// <summary>The angle the initial line was snapped to, or NaN.</summary>
	internal double SnappedRadians { get; private set; } = double.NaN;

	/// <summary>Whether the curve leads back to its start point.</summary>
	internal bool Closed { get; set; }

	/// <summary>Whether the closing segment continues the tangents of the curve.</summary>
	internal bool SmoothClose { get; set; }

	/// <summary>How the two control points of the curve relate to each other.</summary>
	internal SymmetryMode Symmetry { get; set; } = SymmetryMode.Off;

	/// <summary>
	/// The index of the control point that the other one is derived from.
	/// </summary>
	private int symmetry_source = -1;

	internal bool IsDragging => DraggingIndex >= 0;

	/// <summary>The point the user is editing right now: the dragged one, else the hovered one.</summary>
	internal int EditedIndex => IsDragging ? DraggingIndex : HoveredIndex;

	/// <summary>The curve described by the points. Only valid when <see cref="HasShape"/>.</summary>
	internal BezierCurve Curve => CurveGeometry.Resolve (points, Closed, SmoothClose);

	/// <summary>True while the control point that was just placed is still the last action.</summary>
	private bool control_point_placed;

	/// <summary>Starts a new shape with both end points at the given position.</summary>
	internal void Begin (PointD start, MouseButton button)
	{
		points.Clear ();
		points.Add (start);
		points.Add (start);

		OriginButton = button;
		DraggingIndex = 1;
		SelectedIndex = 1;
		HoveredIndex = -1;
		SnappedRadians = double.NaN;
	}

	/// <summary>Starts dragging an existing point. Returns false for an invalid index.</summary>
	internal bool Grab (int index)
	{
		if (index < 0 || index >= points.Count)
			return false;

		DraggingIndex = index;
		SelectedIndex = index;
		SnappedRadians = double.NaN;

		return true;
	}

	/// <summary>Appends a control point and starts dragging it.</summary>
	internal void AddControlPoint (PointD position)
	{
		points.Add (position);

		DraggingIndex = points.Count - 1;
		SelectedIndex = DraggingIndex;
		SnappedRadians = double.NaN;
		control_point_placed = true;

		ApplySymmetry ();
	}

	/// <summary>
	/// Derives the second control point from the one that is being dragged, when the
	/// symmetry is on. Grabbing the derived point breaks the symmetry on purpose.
	/// </summary>
	private void ApplySymmetry ()
	{
		if (Symmetry == SymmetryMode.Off || points.Count < 4)
			return;

		int other = DraggingIndex switch {
			2 => 3,
			3 => 2,
			_ => -1
		};

		if (other < 0)
			return;

		if (symmetry_source >= 0 && symmetry_source != DraggingIndex) {
			// The point that the other one was derived from was grabbed, so the symmetry
			// is broken on purpose.
			Symmetry = SymmetryMode.Off;
			symmetry_source = -1;
			return;
		}

		symmetry_source = DraggingIndex;

		points[other] = CurveGeometry.Mirror (points[DraggingIndex], points[0], points[1], Symmetry);
	}

	/// <summary>
	/// Puts the given points back into the editor, so that a curve that was already
	/// committed can be picked up and adjusted again.
	/// </summary>
	internal void Restore (IReadOnlyList<PointD> restored_points, MouseButton button)
	{
		points.Clear ();

		foreach (PointD point in restored_points)
			points.Add (point);

		OriginButton = button;
		DraggingIndex = -1;
		SelectedIndex = points.Count - 1;
		HoveredIndex = -1;
		SnappedRadians = double.NaN;
		control_point_placed = false;
		symmetry_source = -1;
	}

	/// <summary>
	/// Moves the point being dragged. Only the initial line is subject to the angle
	/// modifiers - control points and the end points of a finished curve move freely
	/// (KolourPaint's drawingALine() is true only while the line is dragged out).
	/// </summary>
	internal void DragTo (PointD position, bool shift_pressed, bool control_pressed)
	{
		if (!IsDragging)
			return;

		SnappedRadians = double.NaN;

		if (points.Count == 2 && DraggingIndex == 1) {
			position = CurveGeometry.SnapToAngles (points[0], position, shift_pressed, control_pressed, out double radians);
			SnappedRadians = radians;
		}

		points[DraggingIndex] = position;

		ApplySymmetry ();
	}

	/// <summary>
	/// Ends the current drag. Returns true when a control point was placed by it, which
	/// is how the tool knows that the curve is complete.
	/// </summary>
	internal bool EndDrag ()
	{
		bool placed = control_point_placed;
		control_point_placed = false;

		DraggingIndex = -1;
		SnappedRadians = double.NaN;

		return placed;
	}

	/// <summary>Sets a point directly, e.g. after nudging it with the arrow keys.</summary>
	internal bool SetPoint (int index, PointD position)
	{
		if (index < 0 || index >= points.Count)
			return false;

		points[index] = position;
		SelectedIndex = index;
		SnappedRadians = double.NaN;

		return true;
	}

	/// <summary>
	/// Takes back the most recent point: 4 -> 3 -> 2, and clears the shape once only the
	/// line is left.
	/// </summary>
	internal void RemoveLastPoint ()
	{
		if (points.Count > 2) {
			points.RemoveAt (points.Count - 1);
			DraggingIndex = -1;
			SelectedIndex = points.Count - 1;
			symmetry_source = -1;
			return;
		}

		Reset ();
	}

	internal void SetHover (int index)
		=> HoveredIndex = index;

	internal void Reset ()
	{
		points.Clear ();

		OriginButton = MouseButton.None;
		DraggingIndex = -1;
		SelectedIndex = -1;
		HoveredIndex = -1;
		SnappedRadians = double.NaN;
		control_point_placed = false;
		symmetry_source = -1;
	}
}
