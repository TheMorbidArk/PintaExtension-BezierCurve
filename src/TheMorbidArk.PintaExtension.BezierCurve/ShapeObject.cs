//
// ShapeObject.cs
//
// A curve that has been drawn and is still an object: its shape, where it sits on the
// canvas and what it looks like. While it is an object the curve can be moved, scaled,
// rotated and flipped, and it wears a selection frame; once it is dropped onto the layer
// it is pixels like anything else.
//
// The curve is kept in its own frame - "local" coordinates - and placed on the canvas by
// a rotation about its own middle plus an offset. Keeping it that way means a rotated
// shape still scales along its own axes, and that the frame can be drawn rotated.
//
// Deliberately free of GTK, Cairo and Pinta services so that the arithmetic can be unit
// tested: the tool owns the frame, the handles and the history.
//
using System;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

/// <summary>Where a shape is: the curve in its own frame, its angle and its offset.</summary>
internal readonly record struct ShapeState (
	BezierCurve Local,
	double Angle,
	PointD Offset);

/// <summary>
/// A shape on the canvas: the curve, the transform that has been applied to it and the
/// area it covers.
/// </summary>
internal sealed class ShapeObject
{
	/// <summary>How small a shape may be scaled before the scale stops.</summary>
	private const double MINIMUM_SCALE = 0.01;

	/// <summary>The curve in its own frame; scaling and flipping change this.</summary>
	private BezierCurve local;

	/// <summary>How far the shape is turned, in radians, about its own middle.</summary>
	private double angle;

	/// <summary>Where the shape sits on the canvas, on top of its own middle.</summary>
	private PointD offset;

	internal ShapeObject (BezierCurve curve, CurveStyle style)
	{
		local = curve;
		Style = style;
	}

	/// <summary>How the shape looks.</summary>
	internal CurveStyle Style { get; set; }

	/// <summary>How far the shape is turned, in radians.</summary>
	internal double Angle => angle;

	/// <summary>The curve in its own frame.</summary>
	internal BezierCurve Local => local;

	/// <summary>The rectangle the local curve fits in, without any margin for the brush.</summary>
	internal (double MinX, double MinY, double MaxX, double MaxY) LocalExtent
		=> CurveGeometry.Extent (local);

	/// <summary>
	/// The canvas position of the middle of the shape. Turning happens around this point,
	/// which is why it does not move when the shape is turned.
	/// </summary>
	internal PointD Center
	{
		get {
			(double minX, double minY, double maxX, double maxY) = LocalExtent;

			return new PointD (
				(minX + maxX) / 2d + offset.X,
				(minY + maxY) / 2d + offset.Y);
		}
	}

	/// <summary>The curve as it is drawn: the local curve placed on the canvas.</summary>
	internal BezierCurve Curve
		=> new (
			Map (local.P0),
			Map (local.P1),
			Map (local.P2),
			Map (local.P3),
			local.Closed,
			local.SmoothClose);

	/// <summary>The area of the canvas the shape covers, brush width and margin included.</summary>
	internal RectangleD Bounds
		=> CurveGeometry.Bounds (Curve, Style.BrushWidth);

	/// <summary>The area the shape covers inside the canvas, for the dirty rectangle.</summary>
	internal RectangleI DirtyRect (Document document)
		=> document.ClampToImageSize (Bounds.ToInt ());

	/// <summary>Everything that a drag has to be able to put back.</summary>
	internal ShapeState Save () => new (local, angle, offset);

	/// <summary>Goes back to a state that was saved earlier, e.g. when a drag starts.</summary>
	internal void Restore (ShapeState saved)
	{
		local = saved.Local;
		angle = saved.Angle;
		offset = saved.Offset;
	}

	/// <summary>Moves the shape by the given offset.</summary>
	internal void Translate (double dx, double dy)
		=> offset = new PointD (offset.X + dx, offset.Y + dy);

	/// <summary>Turns the shape by the given angle about its own middle.</summary>
	internal void RotateBy (double radians)
		=> angle += radians;

	/// <summary>
	/// Scales the shape along its own axes, keeping the given point of its own frame
	/// where it is on the canvas.
	/// </summary>
	internal void ScaleLocal (double sx, double sy, PointD localAnchor)
	{
		sx = Clamp (sx);
		sy = Clamp (sy);

		// Where the anchor is on the canvas now; after the change it has to be there
		// still, so the offset takes up whatever moved.
		PointD anchor_before = Map (localAnchor);

		local = new BezierCurve (
			ScalePoint (local.P0),
			ScalePoint (local.P1),
			ScalePoint (local.P2),
			ScalePoint (local.P3),
			local.Closed,
			local.SmoothClose);

		PointD anchor_after = Map (localAnchor);

		offset = new PointD (
			offset.X + anchor_before.X - anchor_after.X,
			offset.Y + anchor_before.Y - anchor_after.Y);

		return;

		PointD ScalePoint (PointD point)
			=> new (
				localAnchor.X + (point.X - localAnchor.X) * sx,
				localAnchor.Y + (point.Y - localAnchor.Y) * sy);

		static double Clamp (double value)
			=> double.IsFinite (value) && Math.Abs (value) >= MINIMUM_SCALE ? value : MINIMUM_SCALE;
	}

	/// <summary>Mirrors the shape in the vertical line through the middle of its own frame.</summary>
	internal void FlipHorizontal ()
		=> local = Mirror (local, x: -1, y: 1);

	/// <summary>Mirrors the shape in the horizontal line through the middle of its own frame.</summary>
	internal void FlipVertical ()
		=> local = Mirror (local, x: 1, y: -1);

	/// <summary>Where a point of the shape's own frame ends up on the canvas.</summary>
	internal PointD Map (PointD point)
	{
		(double minX, double minY, double maxX, double maxY) = LocalExtent;

		double cx = (minX + maxX) / 2d;
		double cy = (minY + maxY) / 2d;

		double dx = point.X - cx;
		double dy = point.Y - cy;

		double sin = Math.Sin (angle);
		double cos = Math.Cos (angle);

		return new PointD (
			cx + dx * cos - dy * sin + offset.X,
			cy + dx * sin + dy * cos + offset.Y);
	}

	/// <summary>Where a canvas point falls in the shape's own frame.</summary>
	internal PointD Unmap (PointD point)
	{
		(double minX, double minY, double maxX, double maxY) = LocalExtent;

		double cx = (minX + maxX) / 2d;
		double cy = (minY + maxY) / 2d;

		double x = point.X - offset.X - cx;
		double y = point.Y - offset.Y - cy;

		double sin = Math.Sin (-angle);
		double cos = Math.Cos (-angle);

		return new PointD (cx + x * cos - y * sin, cy + x * sin + y * cos);
	}

	private BezierCurve Mirror (BezierCurve curve, double x, double y)
	{
		// Mirror about the middle of the curve in its own frame, so that flipping never
		// moves it on the canvas.
		(double minX, double minY, double maxX, double maxY) = CurveGeometry.Extent (curve);

		double cx = (minX + maxX) / 2d;
		double cy = (minY + maxY) / 2d;

		return new BezierCurve (
			Flip (curve.P0),
			Flip (curve.P1),
			Flip (curve.P2),
			Flip (curve.P3),
			curve.Closed,
			curve.SmoothClose);

		PointD Flip (PointD point)
			=> new (cx + (point.X - cx) * x, cy + (point.Y - cy) * y);
	}
}
