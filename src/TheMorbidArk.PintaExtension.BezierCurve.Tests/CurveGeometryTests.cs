//
// CurveGeometryTests.cs
//
// Unit tests for the pure geometry helpers. These run without a display and without
// Pinta itself, which is what makes them suitable for CI.
//
using System;
using System.Globalization;
using NUnit.Framework;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class CurveGeometryTests
{
	/// <summary>Point at the given angle (degrees) and distance from the origin.</summary>
	private static PointD At (double degrees, double distance)
	{
		double radians = degrees * Math.PI / 180d;

		return new PointD (Math.Cos (radians) * distance, Math.Sin (radians) * distance);
	}

	private static (PointD Point, double Degrees) Snap (double degrees, bool shift, bool control)
	{
		PointD result = CurveGeometry.SnapToAngles (new PointD (0, 0), At (degrees, 10), shift, control, out double radians);

		return (result, radians * 180d / Math.PI);
	}

	[Test]
	public void NoModifier_ReturnsTheInputUnchanged ()
	{
		PointD end = new (7, 3);

		PointD result = CurveGeometry.SnapToAngles (new PointD (0, 0), end, false, false, out double radians);

		Assert.That (result, Is.EqualTo (end));
		Assert.That (double.IsNaN (radians), Is.True);
	}

	[TestCase (20d, 0d)]
	[TestCase (22d, 0d)]
	[TestCase (23d, 45d)]      // 22.5 degrees is the decision boundary
	[TestCase (40d, 45d)]
	[TestCase (67d, 45d)]
	[TestCase (68d, 90d)]      // 67.5 degrees
	[TestCase (80d, 90d)]
	public void Shift_SnapsTo45DegreeSteps (double inputDegrees, double expectedDegrees)
	{
		(_, double degrees) = Snap (inputDegrees, shift: true, control: false);

		Assert.That (degrees, Is.EqualTo (expectedDegrees).Within (1e-6));
	}

	[TestCase (10d, 0d)]
	[TestCase (14d, 0d)]
	[TestCase (16d, 30d)]      // 15 degrees
	[TestCase (25d, 30d)]
	[TestCase (44d, 30d)]
	[TestCase (46d, 60d)]      // 45 degrees
	[TestCase (50d, 60d)]
	[TestCase (74d, 60d)]
	[TestCase (76d, 90d)]      // 75 degrees
	[TestCase (80d, 90d)]
	public void Control_SnapsTo30DegreeSteps (double inputDegrees, double expectedDegrees)
	{
		(_, double degrees) = Snap (inputDegrees, shift: false, control: true);

		Assert.That (degrees, Is.EqualTo (expectedDegrees).Within (1e-6));
	}

	[TestCase (20d, 30d)]
	[TestCase (40d, 45d)]
	[TestCase (55d, 60d)]
	public void ShiftAndControl_Allow30And45And60 (double inputDegrees, double expectedDegrees)
	{
		(_, double degrees) = Snap (inputDegrees, shift: true, control: true);

		Assert.That (degrees, Is.EqualTo (expectedDegrees).Within (1e-6));
	}

	[Test]
	public void HorizontalSnap_KeepsXAndFlattensY ()
	{
		PointD result = CurveGeometry.SnapToAngles (new PointD (10, 10), new PointD (30, 14), true, false, out _);

		Assert.That (result, Is.EqualTo (new PointD (30, 10)));
	}

	[Test]
	public void VerticalSnap_KeepsYAndFlattensX ()
	{
		PointD result = CurveGeometry.SnapToAngles (new PointD (10, 10), new PointD (14, 30), true, false, out _);

		Assert.That (result, Is.EqualTo (new PointD (10, 30)));
	}

	[Test]
	public void DiagonalSnap_PreservesTheDistanceFromTheStartPoint ()
	{
		PointD start = new (5, -2);
		PointD end = new (15, 8);          // 45 degrees already, distance sqrt(200)

		PointD result = CurveGeometry.SnapToAngles (start, end, true, false, out _);

		Assert.That (result.X - start.X, Is.EqualTo (10).Within (1e-9));
		Assert.That (result.Y - start.Y, Is.EqualTo (10).Within (1e-9));
	}

	[Test]
	public void Snapping_NegativeQuadrantsMirrorTheResult ()
	{
		PointD result = CurveGeometry.SnapToAngles (new PointD (0, 0), At (200, 10), true, false, out double radians);

		// 200 degrees is closest to 180 + 45 = 225? No: allowed angles are 0/45/90, so the
		// closest is 0 -> the point stays on the negative x axis.
		Assert.That (radians * 180d / Math.PI, Is.EqualTo (0).Within (1e-6));
		Assert.That (result.Y, Is.EqualTo (0).Within (1e-9));
		Assert.That (result.X, Is.LessThan (0));
	}

	[Test]
	public void Resolve_TwoPoints_IsAStraightLine ()
	{
		PointD[] points = [new (0, 0), new (10, 10)];

		BezierCurve curve = CurveGeometry.Resolve (points);

		Assert.That (curve.P0, Is.EqualTo (points[0]));
		Assert.That (curve.P3, Is.EqualTo (points[1]));
		Assert.That (curve.P1, Is.EqualTo (points[0]));
		Assert.That (curve.P2, Is.EqualTo (points[1]));
	}

	[Test]
	public void Resolve_ThreePoints_PutsBothControlsOnTheThirdPoint ()
	{
		PointD[] points = [new (0, 0), new (10, 0), new (5, -5)];

		BezierCurve curve = CurveGeometry.Resolve (points);

		Assert.That (curve.P1, Is.EqualTo (points[2]));
		Assert.That (curve.P2, Is.EqualTo (points[2]));
	}




	[Test]
	public void Resolve_FourPoints_UsesBothControlPoints ()
	{
		PointD[] points = [new (0, 0), new (10, 0), new (2, -5), new (8, -5)];

		BezierCurve curve = CurveGeometry.Resolve (points);

		Assert.That (curve.P0, Is.EqualTo (points[0]));
		Assert.That (curve.P1, Is.EqualTo (points[2]));
		Assert.That (curve.P2, Is.EqualTo (points[3]));
		Assert.That (curve.P3, Is.EqualTo (points[1]));
	}

	[Test]
	public void Resolve_ThrowsForASinglePoint ()
	{
		Assert.Throws<ArgumentException> (() => CurveGeometry.Resolve ([new PointD (1, 1)]));
	}

	[Test]
	public void Bounds_ContainTheWholeCurvePlusHalfTheBrush ()
	{
		BezierCurve curve = new (new PointD (0, 0), new PointD (5, -20), new PointD (15, -20), new PointD (20, 0));
		const int brush_width = 3;

		RectangleD bounds = CurveGeometry.Bounds (curve, brush_width);
		double margin = brush_width / 2d;

		for (int i = 0; i <= 100; ++i) {
			PointD point = CurveGeometry.Evaluate (curve, i / 100d);

			Assert.That (bounds.Left, Is.LessThanOrEqualTo (point.X - margin), $"left at t={i}");
			Assert.That (bounds.Top, Is.LessThanOrEqualTo (point.Y - margin), $"top at t={i}");
			Assert.That (bounds.Right, Is.GreaterThanOrEqualTo (point.X + margin), $"right at t={i}");
			Assert.That (bounds.Bottom, Is.GreaterThanOrEqualTo (point.Y + margin), $"bottom at t={i}");
		}
	}

	[Test]
	public void Bounds_AreTighterThanTheControlPolygon ()
	{
		// An arch: the control points reach y=100 but the curve only reaches y=75
		// (300 t (1-t) peaks at t = 0.5).
		BezierCurve curve = new (new PointD (0, 0), new PointD (0, 100), new PointD (100, 100), new PointD (100, 0));

		RectangleD bounds = CurveGeometry.Bounds (curve, brushWidth: 1);

		Assert.That (bounds.Bottom, Is.EqualTo (75 + 0.5 + 2).Within (1e-6));
		Assert.That (bounds.Bottom, Is.LessThan (100 + 0.5 + 2));
		Assert.That (bounds.Left, Is.EqualTo (-0.5 - 2).Within (1e-6));
		Assert.That (bounds.Right, Is.EqualTo (100 + 0.5 + 2).Within (1e-6));
	}

	[Test]
	public void Bounds_OfAStraightLineAreTheEndPoints ()
	{
		PointD[] points = [new (10, 20), new (110, 70)];
		BezierCurve line = CurveGeometry.Resolve (points);

		RectangleD bounds = CurveGeometry.Bounds (line, brushWidth: 4);
		double margin = 2 + 2;   // half the brush + antialiasing

		Assert.That (bounds.Left, Is.EqualTo (10 - margin).Within (1e-6));
		Assert.That (bounds.Top, Is.EqualTo (20 - margin).Within (1e-6));
		Assert.That (bounds.Right, Is.EqualTo (110 + margin).Within (1e-6));
		Assert.That (bounds.Bottom, Is.EqualTo (70 + margin).Within (1e-6));
	}

	[Test]
	public void Bounds_GrowWithTheBrushWidth ()
	{
		BezierCurve curve = new (new PointD (0, 0), new PointD (10, 0), new PointD (20, 0), new PointD (30, 0));

		RectangleD thin = CurveGeometry.Bounds (curve, brushWidth: 1);
		RectangleD thick = CurveGeometry.Bounds (curve, brushWidth: 21);

		Assert.That (thick.Width, Is.GreaterThan (thin.Width));
		Assert.That (thick.Height, Is.GreaterThan (thin.Height));
	}

	[Test]
	public void Evaluate_ReturnsTheEndPointsAtTheEdges ()
	{
		BezierCurve curve = new (new PointD (1, 2), new PointD (3, 4), new PointD (5, 6), new PointD (7, 8));

		Assert.That (CurveGeometry.Evaluate (curve, 0d), Is.EqualTo (curve.P0));
		Assert.That (CurveGeometry.Evaluate (curve, 1d), Is.EqualTo (curve.P3));
	}

	[Test]
	public void FormatAngle_RendersWholeDegrees ()
	{
		CultureInfo original = CultureInfo.CurrentUICulture;

		try {
			CultureInfo.CurrentUICulture = new CultureInfo ("en-US");

			Assert.That (CurveGeometry.FormatAngle (Math.PI / 4d), Is.EqualTo ("45°"));
			Assert.That (CurveGeometry.FormatAngle (Math.PI / 2d), Is.EqualTo ("90°"));
			Assert.That (CurveGeometry.FormatAngle (double.NaN), Is.Empty);
		} finally {
			CultureInfo.CurrentUICulture = original;
		}
	}

	#region Construction point snapping

	private static PointD SnapPoint (PointD point, PointD[] others, int width = 200, int height = 100,
		int cell_width = 0, int cell_height = 0, double tolerance = 5d, PointD[]? line = null)
		=> CurveGeometry.SnapToConstructionPoints (
			point,
			others,
			line ?? [],
			new SnapContext (width, height, cell_width, cell_height, tolerance));

	[Test]
	public void Snap_LetsAPointThatIsNowhereNearAlone ()
	{
		PointD result = SnapPoint (new PointD (37, 41), []);

		Assert.That (result, Is.EqualTo (new PointD (37, 41)));
	}

	[Test]
	public void Snap_FindsTheCentreOfTheCanvas ()
	{
		PointD result = SnapPoint (new PointD (98, 52), []);

		Assert.That (result.X, Is.EqualTo (100).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (50).Within (1e-9));
	}

	[Test]
	public void Snap_FindsTheEdgesOfTheCanvas ()
	{
		PointD result = SnapPoint (new PointD (3, 97), []);

		Assert.That (result.X, Is.EqualTo (0).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (100).Within (1e-9));
	}

	[Test]
	public void Snap_FindsTheGrid ()
	{
		PointD result = SnapPoint (new PointD (43, 23), [], cell_width: 20, cell_height: 20);

		Assert.That (result.X, Is.EqualTo (40).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (20).Within (1e-9));
	}

	[Test]
	public void Snap_FindsTheCoordinatesOfTheOtherPoints ()
	{
		PointD[] others = [new (10, 10), new (90, 70)];

		PointD result = SnapPoint (new PointD (88, 12), others);

		Assert.That (result.X, Is.EqualTo (90).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (10).Within (1e-9));
	}

	[Test]
	public void Snap_OnlyMovesTheAxisThatIsCloseToACandidate ()
	{
		PointD result = SnapPoint (new PointD (37, 98), []);

		Assert.That (result.X, Is.EqualTo (37).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (100).Within (1e-9));
	}

	[Test]
	public void Snap_RespectsTheTolerance ()
	{
		// Six pixels away from the centre line is too far.
		PointD result = SnapPoint (new PointD (106, 50), [], tolerance: 5d);

		Assert.That (result.X, Is.EqualTo (106).Within (1e-9));
	}

	[Test]
	public void Snap_FindsTheMiddleAndTheThirdsOfTheLine ()
	{
		PointD[] line = [new (0, 0), new (90, 0)];

		PointD middle = SnapPoint (new PointD (46, 10), [], line: line);
		Assert.That (middle.X, Is.EqualTo (45).Within (1e-9));
		Assert.That (middle.Y, Is.EqualTo (10).Within (1e-9));

		PointD third = SnapPoint (new PointD (28, 10), [], line: line);
		Assert.That (third.X, Is.EqualTo (30).Within (1e-9));
	}

	[Test]
	public void Snap_PutsAPointOntoThePerpendicularBisector ()
	{
		PointD[] line = [new (0, 0), new (100, 0)];

		// The bisector of a horizontal line is the vertical line through its middle.
		PointD result = SnapPoint (new PointD (52, 30), [], line: line);

		Assert.That (result.X, Is.EqualTo (50).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (30).Within (1e-9));
	}

	[Test]
	public void Snap_LeavesAPointThatIsNowhereNearTheBisector ()
	{
		PointD[] line = [new (0, 0), new (100, 0)];

		PointD result = SnapPoint (new PointD (80, 30), [], line: line);

		Assert.That (result.X, Is.EqualTo (80).Within (1e-9));
	}

	#endregion

	#region Tangent angles

	[Test]
	public void TangentAngle_SnapsToTheClosestAllowedAngle ()
	{
		PointD result = CurveGeometry.SnapToTangentAngle (new PointD (0, 0), At (20, 10), out double radians);

		Assert.That (radians * 180d / Math.PI, Is.EqualTo (30).Within (1e-6));
		Assert.That (result.Y, Is.EqualTo (5).Within (1e-9));   // 10 * sin(30 degrees)
		Assert.That (result.X, Is.EqualTo (10 * Math.Cos (Math.PI / 6d)).Within (1e-9));
	}

	[Test]
	public void TangentAngle_SnapsToTheHorizontal ()
	{
		PointD result = CurveGeometry.SnapToTangentAngle (new PointD (0, 0), At (10, 10), out double radians);

		Assert.That (radians * 180d / Math.PI, Is.EqualTo (0).Within (1e-6));
		Assert.That (result.Y, Is.EqualTo (0).Within (1e-9));
	}

	#endregion

	#region Symmetry

	[Test]
	public void Mirror_Center_IsThePointAroundTheMiddleOfTheLine ()
	{
		PointD result = CurveGeometry.Mirror (new PointD (20, 30), new PointD (0, 0), new PointD (100, 0), SymmetryMode.Center);

		Assert.That (result.X, Is.EqualTo (80).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (-30).Within (1e-9));
	}

	[Test]
	public void Mirror_Mirror_IsTheReflectionInThePerpendicularBisector ()
	{
		PointD result = CurveGeometry.Mirror (new PointD (20, 30), new PointD (0, 0), new PointD (100, 0), SymmetryMode.Mirror);

		// The bisector of a horizontal line is the vertical line through its middle.
		Assert.That (result.X, Is.EqualTo (80).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (30).Within (1e-9));
	}

	[Test]
	public void Mirror_MirrorOfADiagonalLine ()
	{
		PointD result = CurveGeometry.Mirror (new PointD (10, 0), new PointD (0, 0), new PointD (100, 100), SymmetryMode.Mirror);

		// The bisector of the diagonal is the line x + y = 100.
		Assert.That (result.X, Is.EqualTo (100).Within (1e-9));
		Assert.That (result.Y, Is.EqualTo (90).Within (1e-9));
	}

	#endregion

	#region Pieces of a curve

	[Test]
	public void Subdivide_ReturnsTheFirstPiece ()
	{
		BezierCurve curve = new (new PointD (0, 0), new PointD (0, 100), new PointD (100, 100), new PointD (100, 0));

		BezierCurve half = CurveGeometry.Subdivide (curve, 0d, 0.5d);

		Assert.That (half.P0, Is.EqualTo (curve.P0));
		Assert.That (half.P3, Is.EqualTo (CurveGeometry.Evaluate (curve, 0.5d)));
	}

	[Test]
	public void Subdivide_PiecesTogetherAreTheWholeCurve ()
	{
		BezierCurve curve = new (new PointD (0, 0), new PointD (0, 100), new PointD (100, 100), new PointD (100, 0));

		BezierCurve first = CurveGeometry.Subdivide (curve, 0d, 0.25d);
		BezierCurve second = CurveGeometry.Subdivide (curve, 0.25d, 0.75d);
		BezierCurve third = CurveGeometry.Subdivide (curve, 0.75d, 1d);

		Assert.That (first.P0, Is.EqualTo (curve.P0));
		Assert.That (first.P3, Is.EqualTo (second.P0));
		Assert.That (second.P3, Is.EqualTo (third.P0));
		Assert.That (third.P3, Is.EqualTo (curve.P3));

		// The middle of the whole curve lies in the middle piece.
		Assert.That (CurveGeometry.Evaluate (second, 0.5d).X,
			Is.EqualTo (CurveGeometry.Evaluate (curve, 0.5d).X).Within (1e-9));
	}

	[Test]
	public void SeamControls_ContinueTheTangentsOfTheCurve ()
	{
		BezierCurve curve = new (new PointD (0, 0), new PointD (50, -100), new PointD (150, -100), new PointD (200, 0));

		(PointD first, PointD second) = CurveGeometry.SeamControls (curve);

		// Two thirds of the way from the end of the curve to its last control point.
		Assert.That (first.X, Is.EqualTo (200d - 100d / 3d).Within (1e-9));
		Assert.That (first.Y, Is.EqualTo (-200d / 3d).Within (1e-9));

		Assert.That (second.X, Is.EqualTo (100d / 3d).Within (1e-9));
		Assert.That (second.Y, Is.EqualTo (-200d / 3d).Within (1e-9));
	}

	#endregion
}
