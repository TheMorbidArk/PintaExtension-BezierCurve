//
// CurveGeometry.cs
//
// Pure geometry helpers for the curve tool. Everything here is a side effect free
// function so that it can be unit tested and reasoned about in isolation.
//
// The snapping logic is a faithful port of kpToolPolygonalBase::applyModifiers()
// from KolourPaint (tools/polygonal/kpToolPolygonalBase.cpp), except that it works
// in doubles instead of integer QPoints.
//
using System;
using System.Collections.Generic;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

/// <summary>
/// The four points of the cubic Bezier curve that KolourPaint's curve tool draws:
/// P0 and P3 are the end points that the curve passes through, P1 and P2 are the
/// off-curve control points.
/// </summary>
internal readonly record struct BezierCurve (
	PointD P0,
	PointD P1,
	PointD P2,
	PointD P3,
	bool Closed = false,
	bool SmoothClose = false);

/// <summary>
/// How the two control points of a curve relate to each other.
/// </summary>
internal enum SymmetryMode
{
	/// <summary>The control points are placed independently.</summary>
	Off,

	/// <summary>
	/// The second control point is the mirror image of the first one, reflected in the
	/// perpendicular bisector of the line between the two end points - which draws a
	/// leaf that is symmetric around that line.
	/// </summary>
	Mirror,

	/// <summary>
	/// The second control point is the point symmetric to the first one around the
	/// middle of the line between the two end points - which draws an S that is
	/// symmetric around its middle.
	/// </summary>
	Center,
}

/// <summary>
/// The canvas a curve is drawn on: its size and the grid that is shown on it.
/// </summary>
internal readonly record struct SnapContext (
	int Width,
	int Height,
	int CellWidth,
	int CellHeight,
	double Tolerance);

/// <summary>
/// How the tool behaves once a curve has been drawn. The drawing itself is the same in
/// both modes - the two differ only in what happens to the finished curve.
/// </summary>
internal enum ToolMode
{
	/// <summary>
	/// KolourPaint's behaviour: the curve is a stroke of paint. Once it is finished it is
	/// dropped onto the layer and that is that.
	/// </summary>
	KolourPaint,

	/// <summary>
	/// MS Paint's behaviour: the finished curve stays an object with a selection frame
	/// around it, so that it can be moved, scaled and flipped before it is dropped onto
	/// the layer.
	/// </summary>
	MSPaint,
}

internal static class CurveGeometry
{
	/// <summary>
	/// Resolve the user's 2-4 dragged points into a cubic Bezier.
	/// Mirrors KolourPaint's DrawCurveShape():
	///   2 points -> a straight line,
	///   3 points -> one control point, which either carries both cubic control points
	///               (KolourPaint) or acts as a tangent handle,
	///   4 points -> a full cubic curve.
	/// </summary>
	internal static BezierCurve Resolve (
		IReadOnlyList<PointD> points,
		bool closed = false,
		bool smooth_close = false)
	{
		if (points.Count < 2)
			throw new ArgumentException ("At least two points are required.", nameof (points));

		PointD start = points[0];
		PointD end = points[1];

		(PointD cp1, PointD cp2) = points.Count switch {
			2 => (start, end),
			3 => (points[2], points[2]),
			_ => (points[2], points[3]),
		};

		return new BezierCurve (start, cp1, cp2, end, closed, smooth_close);
	}

	/// <summary>
	/// The control points of the seam of a closed curve: the tangents at both ends are
	/// continued into the closing segment, so that the curve has no corner where it
	/// meets itself.
	/// </summary>
	internal static (PointD First, PointD Second) SeamControls (BezierCurve curve)
	{
		PointD first = new (
			curve.P3.X + TWO_THIRDS * (curve.P2.X - curve.P3.X),
			curve.P3.Y + TWO_THIRDS * (curve.P2.Y - curve.P3.Y));

		PointD second = new (
			curve.P0.X + TWO_THIRDS * (curve.P1.X - curve.P0.X),
			curve.P0.Y + TWO_THIRDS * (curve.P1.Y - curve.P0.Y));

		return (first, second);
	}

	/// <summary>
	/// The part of a cubic Bezier between two parameters, computed with de Casteljau's
	/// algorithm. This is what a tapered stroke is drawn from.
	/// </summary>
	internal static BezierCurve Subdivide (BezierCurve curve, double t0, double t1)
	{
		if (t0 > t1)
			(t0, t1) = (t1, t0);

		if (t0 <= 0d && t1 >= 1d)
			return curve;

		if (t1 <= 0d)
			return new BezierCurve (curve.P0, curve.P0, curve.P0, curve.P0);

		if (t0 >= 1d)
			return new BezierCurve (curve.P3, curve.P3, curve.P3, curve.P3);

		BezierCurve left = SplitAt (curve, t1).Left;

		return SplitAt (left, t0 / t1).Right;
	}

	/// <summary>Splits a cubic Bezier at a parameter into its two halves.</summary>
	private static (BezierCurve Left, BezierCurve Right) SplitAt (BezierCurve curve, double t)
	{
		PointD p01 = Lerp (curve.P0, curve.P1, t);
		PointD p12 = Lerp (curve.P1, curve.P2, t);
		PointD p23 = Lerp (curve.P2, curve.P3, t);

		PointD p012 = Lerp (p01, p12, t);
		PointD p123 = Lerp (p12, p23, t);
		PointD p0123 = Lerp (p012, p123, t);

		return (
			new BezierCurve (curve.P0, p01, p012, p0123),
			new BezierCurve (p0123, p123, p23, curve.P3));
	}

	private static PointD Lerp (PointD a, PointD b, double t)
		=> new (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

	/// <summary>
	/// The length of the curve. A tapered stroke is not drawn evenly along it when the
	/// control points are far apart, so the length decides how fast its ends grow.
	/// </summary>
	internal static double Length (BezierCurve curve)
	{
		const int steps = 32;

		double length = 0d;
		PointD previous = curve.P0;

		for (int i = 1; i <= steps; ++i) {
			PointD point = Evaluate (curve, (double) i / steps);

			double dx = point.X - previous.X;
			double dy = point.Y - previous.Y;

			length += Math.Sqrt (dx * dx + dy * dy);
			previous = point;
		}

		return length;
	}

	/// <summary>
	/// The point that mirrors the given one, according to the symmetry mode: either
	/// around the middle of the line between the two end points, or in the
	/// perpendicular bisector of that line.
	/// </summary>
	internal static PointD Mirror (PointD point, PointD a, PointD b, SymmetryMode mode)
	{
		if (mode == SymmetryMode.Center)
			return new PointD (a.X + b.X - point.X, a.Y + b.Y - point.Y);

		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double length = Math.Sqrt (dx * dx + dy * dy);

		if (length < 1e-9)
			return point;

		// The bisector runs through the middle of the line, perpendicular to it.
		PointD middle = new ((a.X + b.X) / 2d, (a.Y + b.Y) / 2d);
		double nx = -dy / length;
		double ny = dx / length;

		double distance = (point.X - middle.X) * nx + (point.Y - middle.Y) * ny;

		PointD projection = new (middle.X + nx * distance, middle.Y + ny * distance);

		return new PointD (2 * projection.X - point.X, 2 * projection.Y - point.Y);
	}

	private const double TWO_THIRDS = 2d / 3d;

	/// <summary>
	/// Exact bounding box of the curve, inflated by half the stroke width (the round caps
	/// fit inside that) plus a small margin for antialiasing.
	/// </summary>
	internal static RectangleD Bounds (BezierCurve curve, int brushWidth)
	{
		(double minX, double minY, double maxX, double maxY) = Extent (curve);

		double margin = brushWidth / 2d + 2d;

		// RectangleD.Right/Bottom are inclusive (they subtract 1), so the span gets one
		// extra unit to make the margin symmetric on all four sides.
		return new RectangleD (
			minX - margin,
			minY - margin,
			(maxX - minX) + 2 * margin + 1,
			(maxY - minY) + 2 * margin + 1);
	}

	/// <summary>
	/// The smallest rectangle the curve itself fits in, without any margin for the brush.
	/// This is what the middle of a shape is worked out from, so that mirroring it does
	/// not drift by the margin.
	/// </summary>
	internal static (double MinX, double MinY, double MaxX, double MaxY) Extent (BezierCurve curve)
	{
		(double minX, double maxX) = AxisRange (curve.P0.X, curve.P1.X, curve.P2.X, curve.P3.X);
		(double minY, double maxY) = AxisRange (curve.P0.Y, curve.P1.Y, curve.P2.Y, curve.P3.Y);

		return (minX, minY, maxX, maxY);
	}

	/// <summary>
	/// Width and height of the curve itself (no brush width, no margins).
	/// </summary>
	internal static (double Width, double Height) Size (BezierCurve curve)
	{
		(double minX, double maxX) = AxisRange (curve.P0.X, curve.P1.X, curve.P2.X, curve.P3.X);
		(double minY, double maxY) = AxisRange (curve.P0.Y, curve.P1.Y, curve.P2.Y, curve.P3.Y);

		return (maxX - minX, maxY - minY);
	}

	/// <summary>
	/// Range of one axis of a cubic Bezier: the end points plus the extrema, which are the
	/// roots in (0, 1) of 3[(p3-3p2+3p1-p0)t^2 + 2(p0-2p1+p2)t + (p1-p0)].
	/// </summary>
	private static (double Min, double Max) AxisRange (double p0, double p1, double p2, double p3)
	{
		double min = Math.Min (p0, p3);
		double max = Math.Max (p0, p3);

		double a = p3 - 3 * p2 + 3 * p1 - p0;
		double b = 2 * (p0 - 2 * p1 + p2);
		double c = p1 - p0;

		if (Math.Abs (a) < 1e-12) {
			// The derivative is linear, so there is at most one extremum.
			if (Math.Abs (b) > 1e-12)
				Consider (-c / b);
		} else {
			double discriminant = b * b - 4 * a * c;

			if (discriminant >= 0) {
				double root = Math.Sqrt (discriminant);
				Consider ((-b + root) / (2 * a));
				Consider ((-b - root) / (2 * a));
			}
		}

		return (min, max);

		void Consider (double t)
		{
			if (t <= 0d || t >= 1d)
				return;

			double value = Evaluate (p0, p1, p2, p3, t);

			if (value < min)
				min = value;

			if (value > max)
				max = value;
		}
	}

	private static double Evaluate (double p0, double p1, double p2, double p3, double t)
	{
		double u = 1d - t;

		return u * u * u * p0
			+ 3d * u * u * t * p1
			+ 3d * u * t * t * p2
			+ t * t * t * p3;
	}

	internal static PointD Evaluate (BezierCurve curve, double t)
		=> new (
			Evaluate (curve.P0.X, curve.P1.X, curve.P2.X, curve.P3.X, t),
			Evaluate (curve.P0.Y, curve.P1.Y, curve.P2.Y, curve.P3.Y, t));

	/// <summary>
	/// Snap the end point of the initial line, in response to the Shift / Control
	/// modifiers. Port of KolourPaint's applyModifiers():
	///
	///   Shift        -> 0, 45, 90 degrees
	///   Control      -> 0, 30, 60, 90 degrees
	///   Shift+Ctrl   -> 0, 30, 45, 60, 90 degrees
	///
	/// The decision boundary between two neighbouring angles is the tangent of the
	/// angle halfway between them, which is equivalent to "pick the closest angle".
	/// </summary>
	/// <param name="snappedRadians">The chosen angle, or NaN when no snapping was applied.</param>
	internal static PointD SnapToAngles (
		PointD start,
		PointD end,
		bool shiftPressed,
		bool controlPressed,
		out double snappedRadians)
	{
		if (!shiftPressed && !controlPressed) {
			snappedRadians = double.NaN;
			return end;
		}

		// "ought to be enough for anybody" -- KolourPaint
		Span<double> angles = stackalloc double[5];
		int count = 0;
		angles[count++] = 0d;
		if (controlPressed)
			angles[count++] = Math.PI / 6d;
		if (shiftPressed)
			angles[count++] = Math.PI / 4d;
		if (controlPressed)
			angles[count++] = Math.PI / 3d;
		angles[count++] = Math.PI / 2d;

		return SnapToAllowedAngles (start, end, angles, count, out snappedRadians);
	}

	/// <summary>
	/// Snaps the direction from the anchor to the point to one of the angles a curve's
	/// tangent can take: 0, 30, 45, 60 and 90 degrees. The distance from the anchor is
	/// kept.
	/// </summary>
	/// <param name="snappedRadians">The chosen angle, or NaN when no snapping was applied.</param>
	internal static PointD SnapToTangentAngle (PointD anchor, PointD point, out double snappedRadians)
	{
		Span<double> angles = stackalloc double[5];
		int count = 0;
		angles[count++] = 0d;
		angles[count++] = Math.PI / 6d;
		angles[count++] = Math.PI / 4d;
		angles[count++] = Math.PI / 3d;
		angles[count++] = Math.PI / 2d;

		return SnapToAllowedAngles (anchor, point, angles, count, out snappedRadians);
	}

	/// <summary>
	/// Moves the end point onto the closest of the allowed angles. The decision
	/// boundary between two neighbouring angles is the tangent of the angle halfway
	/// between them, which is equivalent to "pick the closest angle".
	/// </summary>
	private static PointD SnapToAllowedAngles (
		PointD start,
		PointD end,
		ReadOnlySpan<double> angles,
		int count,
		out double snappedRadians)
	{
		double dx = end.X - start.X;
		double dy = end.Y - start.Y;

		double ratio = dx == 0d ? double.MaxValue : Math.Abs (dy / dx);

		double angle = angles[count - 1];

		for (int i = 0; i < count - 1; ++i) {
			double acceptingRatio = Math.Tan ((angles[i] + angles[i + 1]) / 2d);
			if (ratio < acceptingRatio) {
				angle = angles[i];
				break;
			}
		}

		snappedRadians = angle;

		if (angle == 0d)
			return new PointD (end.X, start.Y);   // horizontal, the x distance is kept

		if (angle == Math.PI / 2d)
			return new PointD (start.X, end.Y);   // vertical, the y distance is kept

		// Diagonal: the distance from the start point is preserved.
		double distance = Math.Sqrt (dx * dx + dy * dy);
		double signX = dx < 0d ? -1d : 1d;
		double signY = dy < 0d ? -1d : 1d;

		return new PointD (
			start.X + distance * Math.Cos (angle) * signX,
			start.Y + distance * Math.Sin (angle) * signY);
	}

	/// <summary>
	/// Snaps a point to the "construction points" of the classic MS Paint workflow: the
	/// edges and the centre lines of the canvas, its corners, the grid (when the canvas
	/// grid is shown), the coordinates of the points that are already placed, the middle
	/// and the thirds of the line between the two end points, and the perpendicular
	/// bisector of that line. That is how the original tool is used - draw a guide line
	/// first, then put the control points exactly onto its intersections.
	/// </summary>
	/// <param name="point">The point to snap, in canvas coordinates.</param>
	/// <param name="other_points">The points that are already placed; their coordinates are candidates, too.</param>
	/// <param name="line_points">The two end points of the curve, or an empty list.</param>
	/// <param name="context">The canvas and the grid it shows.</param>
	internal static PointD SnapToConstructionPoints (
		PointD point,
		IReadOnlyList<PointD> other_points,
		IReadOnlyList<PointD> line_points,
		in SnapContext context)
	{
		List<double> xs = new ();
		List<double> ys = new ();

		// The edges and the centre lines of the canvas.
		xs.Add (0);
		xs.Add (context.Width / 2d);
		xs.Add (context.Width);
		ys.Add (0);
		ys.Add (context.Height / 2d);
		ys.Add (context.Height);

		// The grid, when it is shown.
		if (context.CellWidth > 0)
			for (int grid_x = 0; grid_x <= context.Width; grid_x += context.CellWidth)
				xs.Add (grid_x);

		if (context.CellHeight > 0)
			for (int grid_y = 0; grid_y <= context.Height; grid_y += context.CellHeight)
				ys.Add (grid_y);

		// The points that are already placed.
		foreach (PointD other in other_points) {
			xs.Add (other.X);
			ys.Add (other.Y);
		}

		// The middle and the thirds of the line between the two end points.
		if (line_points.Count == 2) {
			PointD start = line_points[0];
			PointD end = line_points[1];

			foreach (double part in new[] { 1d / 3d, 1d / 2d, 2d / 3d }) {
				PointD middle = Lerp (start, end, part);
				xs.Add (middle.X);
				ys.Add (middle.Y);
			}
		}

		double? x = Nearest (point.X, xs, context.Tolerance);
		double? y = Nearest (point.Y, ys, context.Tolerance);

		// A point that is close to the perpendicular bisector of the line is put onto it -
		// but only when nothing else was close to it.
		if (x is null && y is null && line_points.Count == 2) {
			PointD? on_bisector = ProjectOntoBisector (point, line_points[0], line_points[1], context.Tolerance);

			if (on_bisector is not null)
				return on_bisector.Value;
		}

		return new PointD (x ?? point.X, y ?? point.Y);
	}

	/// <summary>
	/// The point on the perpendicular bisector of the line that is closest to the given
	/// one, or null when the point is too far away from it.
	/// </summary>
	private static PointD? ProjectOntoBisector (PointD point, PointD a, PointD b, double tolerance)
	{
		double dx = b.X - a.X;
		double dy = b.Y - a.Y;
		double length = Math.Sqrt (dx * dx + dy * dy);

		if (length < 1e-9)
			return null;

		PointD middle = new ((a.X + b.X) / 2d, (a.Y + b.Y) / 2d);
		double nx = -dy / length;
		double ny = dx / length;

		double distance = (point.X - middle.X) * nx + (point.Y - middle.Y) * ny;

		if (Math.Abs (distance) > tolerance)
			return null;

		return new PointD (point.X - distance * nx, point.Y - distance * ny);
	}

	/// <summary>The candidate closest to the value, or null when none is close enough.</summary>
	private static double? Nearest (double value, List<double> candidates, double tolerance)
	{
		double? best = null;
		double best_distance = tolerance;

		foreach (double candidate in candidates) {
			double distance = Math.Abs (candidate - value);

			if (distance < best_distance) {
				best = candidate;
				best_distance = distance;
			}
		}

		return best;
	}

	/// <summary>
	/// Human readable form of the snapped angle, for the status bar.
	/// </summary>
	internal static string FormatAngle (double radians)
	{
		if (double.IsNaN (radians))
			return string.Empty;

		int degrees = (int) Math.Round (radians * 180d / Math.PI);

		return string.Format (Strings.Get ("SnapAngle"), degrees);
	}
}
