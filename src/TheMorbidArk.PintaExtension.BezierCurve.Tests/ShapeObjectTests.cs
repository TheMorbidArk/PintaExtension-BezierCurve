//
// ShapeObjectTests.cs
//
// The arithmetic behind the object that a finished curve becomes: moving it, scaling it
// around a handle and flipping it. All of it is free of GTK and Pinta services, so it can
// be checked without a display.
//
using System;
using Cairo;
using NUnit.Framework;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class ShapeObjectTests
{
	private static CurveStyle Style (int brush_width = 1)
		=> new (new Color (0, 0, 0), brush_width, false, true, "-");

	/// <summary>A straight horizontal curve from (10, 30) to (110, 30).</summary>
	private static ShapeObject Object ()
		=> new (
			new BezierCurve (new PointD (10, 30), new PointD (10, 30), new PointD (110, 30), new PointD (110, 30)),
			Style ());

	private static void AssertPoint (PointD actual, double x, double y, string what)
	{
		Assert.That (actual.X, Is.EqualTo (x).Within (1e-4), $"{what}.X");
		Assert.That (actual.Y, Is.EqualTo (y).Within (1e-4), $"{what}.Y");
	}

	[Test]
	public void ANewObjectIsWhereTheCurveWas ()
	{
		ShapeObject shape = Object ();

		AssertPoint (shape.Curve.P0, 10, 30, "start");
		AssertPoint (shape.Curve.P3, 110, 30, "end");
	}

	[Test]
	public void TranslateMovesTheWholeCurve ()
	{
		ShapeObject shape = Object ();

		shape.Translate (5, -7);

		AssertPoint (shape.Curve.P0, 15, 23, "start");
		AssertPoint (shape.Curve.P3, 115, 23, "end");
	}

	[Test]
	public void TranslateAccumulates ()
	{
		ShapeObject shape = Object ();

		shape.Translate (5, 0);
		shape.Translate (5, 0);

		AssertPoint (shape.Curve.P0, 20, 30, "start");
	}

	[Test]
	public void ScaleGrowsTheCurveAroundTheAnchorItIsGiven ()
	{
		ShapeObject shape = Object ();

		// Twice as wide around the left end, which has to stay put.
		shape.ScaleLocal (2, 1, new PointD (10, 30));

		AssertPoint (shape.Curve.P0, 10, 30, "the anchor stays");
		AssertPoint (shape.Curve.P3, 210, 30, "the far end moves");
	}

	[Test]
	public void ScaleShrinksTheCurve ()
	{
		ShapeObject shape = Object ();

		shape.ScaleLocal (0.5, 0.5, new PointD (10, 30));

		AssertPoint (shape.Curve.P3, 60, 30, "end");
	}

	[Test]
	public void ScaleKeepsTheAnchorFixedEvenWhenItIsInTheMiddle ()
	{
		ShapeObject shape = Object ();

		shape.ScaleLocal (2, 2, new PointD (60, 30));

		AssertPoint (shape.Curve.P0, -40, 30, "start");
		AssertPoint (shape.Curve.P3, 160, 30, "end");
	}

	[Test]
	public void ScaleNeverCollapsesTheCurve ()
	{
		ShapeObject shape = Object ();

		// A zero or negative factor would make the shape impossible to grab.
		shape.ScaleLocal (0, 0, new PointD (10, 30));

		Assert.That (shape.Bounds.Width, Is.GreaterThan (0));
		Assert.That (shape.Curve.P3.X, Is.GreaterThan (shape.Curve.P0.X));
	}

	[Test]
	public void ScaleRejectsAnInfiniteFactor ()
	{
		ShapeObject shape = Object ();

		shape.ScaleLocal (double.PositiveInfinity, 1, new PointD (10, 30));

		Assert.That (double.IsFinite (shape.Curve.P3.X), Is.True, shape.Curve.P3.X.ToString ());
	}

	[Test]
	public void FlipHorizontalMirrorsTheCurveInItsOwnMiddle ()
	{
		ShapeObject shape = Object ();

		// The curve runs from x=10 to x=110, so its middle is x=60.
		shape.FlipHorizontal ();

		AssertPoint (shape.Curve.P0, 110, 30, "start");
		AssertPoint (shape.Curve.P3, 10, 30, "end");
	}

	[Test]
	public void FlipHorizontalLeavesTheBoundsWhereTheyWere ()
	{
		ShapeObject shape = Object ();
		RectangleD before = shape.Bounds;

		shape.FlipHorizontal ();
		RectangleD after = shape.Bounds;

		Assert.That (after.Left, Is.EqualTo (before.Left).Within (1e-4));
		Assert.That (after.Right, Is.EqualTo (before.Right).Within (1e-4));
	}

	[Test]
	public void FlipVerticalMirrorsTheCurveInItsOwnMiddle ()
	{
		// An arch: the ends sit on y=0 and the top of the arch reaches y=-30, so the
		// middle of the curve is y=-15.
		ShapeObject shape = new (
			new BezierCurve (new PointD (0, 0), new PointD (0, -40), new PointD (100, -40), new PointD (100, 0)),
			Style ());

		shape.FlipVertical ();

		// The ends go to where the top of the arch was, and the arch now points down.
		AssertPoint (shape.Curve.P0, 0, -30, "start");
		AssertPoint (shape.Curve.P3, 100, -30, "end");

		(double _, double min_y, double _, double max_y) = CurveGeometry.Extent (shape.Curve);
		Assert.That (max_y, Is.EqualTo (0).Within (1e-4), "the arch points down now");
		Assert.That (min_y, Is.EqualTo (-30).Within (1e-4));
	}

	[Test]
	public void FlippingTwiceGivesTheOriginalCurveBack ()
	{
		ShapeObject shape = Object ();

		shape.FlipHorizontal ();
		shape.FlipHorizontal ();

		AssertPoint (shape.Curve.P0, 10, 30, "start");
		AssertPoint (shape.Curve.P3, 110, 30, "end");
	}

	[Test]
	public void SavingAndRestoringAStateUndoesADrag ()
	{
		ShapeObject shape = Object ();
		ShapeState saved = shape.Save ();

		shape.Translate (40, 40);
		shape.ScaleLocal (3, 3, new PointD (10, 30));
		shape.RotateBy (1.1);
		shape.Restore (saved);

		AssertPoint (shape.Curve.P0, 10, 30, "start");
		AssertPoint (shape.Curve.P3, 110, 30, "end");
	}

	#region Turning

	[Test]
	public void TheMiddleOfAShapeIsTheMiddleOfItsCurve ()
	{
		AssertPoint (Object ().Center, 60, 30, "middle");
	}

	[Test]
	public void TurningKeepsTheMiddleWhereItIs ()
	{
		ShapeObject shape = Object ();
		PointD before = shape.Center;

		shape.RotateBy (Math.PI / 3d);

		AssertPoint (shape.Center, before.X, before.Y, "middle");
	}

	[Test]
	public void TurningByAHalfCircleSwapsTheEnds ()
	{
		ShapeObject shape = Object ();

		shape.RotateBy (Math.PI);

		// The curve ran from x=10 to x=110 around a middle of x=60.
		AssertPoint (shape.Curve.P0, 110, 30, "start");
		AssertPoint (shape.Curve.P3, 10, 30, "end");
	}

	[Test]
	public void TurningByAQuarterTurnsTheCurveUpright ()
	{
		ShapeObject shape = Object ();

		shape.RotateBy (Math.PI / 2d);

		// A horizontal curve becomes a vertical one through the same middle.
		AssertPoint (shape.Curve.P0, 60, -20, "start");
		AssertPoint (shape.Curve.P3, 60, 80, "end");
	}

	[Test]
	public void TurningAccumulates ()
	{
		ShapeObject shape = Object ();

		shape.RotateBy (Math.PI / 4d);
		shape.RotateBy (Math.PI / 4d);

		Assert.That (shape.Angle, Is.EqualTo (Math.PI / 2d).Within (1e-9));
	}

	[Test]
	public void TheMapAndUnmapPairAreEachOthersInverse ()
	{
		ShapeObject shape = Object ();
		shape.RotateBy (0.7);
		shape.Translate (13, -9);

		PointD local = new (37, 55);
		PointD back = shape.Unmap (shape.Map (local));

		AssertPoint (back, local.X, local.Y, "round trip");
	}

	[Test]
	public void ScalingATurnedShapeKeepsTheAnchorOnTheCanvas ()
	{
		ShapeObject shape = Object ();
		shape.RotateBy (Math.PI / 4d);

		PointD anchor = new (10, 30);
		PointD before = shape.Map (anchor);

		shape.ScaleLocal (2, 2, anchor);

		PointD after = shape.Map (anchor);

		AssertPoint (after, before.X, before.Y, "the anchor stays");
	}

	[Test]
	public void ScalingATurnedShapeStretchesAlongItsOwnAxis ()
	{
		ShapeObject shape = Object ();
		shape.RotateBy (Math.PI / 2d);

		double length_before = CurveGeometry.Extent (shape.Curve).MaxY - CurveGeometry.Extent (shape.Curve).MinY;

		// Along the shape's own x axis, which is the canvas' y axis now.
		shape.ScaleLocal (2, 1, new PointD (10, 30));

		double length_after = CurveGeometry.Extent (shape.Curve).MaxY - CurveGeometry.Extent (shape.Curve).MinY;

		Assert.That (length_after, Is.EqualTo (length_before * 2).Within (1e-4), $"{length_before} -> {length_after}");
	}

	#endregion

	[Test]
	public void TheStyleFollowsTheObject ()
	{
		ShapeObject shape = Object ();

		shape.Style = Style (brush_width: 20);

		// The bounds grow with the brush width.
		Assert.That (shape.Bounds.Height, Is.GreaterThan (20));
	}

	[Test]
	public void AClosedCurveStaysClosedThroughTheTransform ()
	{
		ShapeObject shape = new (
			new BezierCurve (new PointD (0, 0), new PointD (0, 10), new PointD (10, 10), new PointD (10, 0), Closed: true),
			Style ());

		shape.Translate (5, 5);
		shape.FlipHorizontal ();

		Assert.That (shape.Curve.Closed, Is.True);
	}
}
