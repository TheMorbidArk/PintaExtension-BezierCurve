//
// CurveHandle.cs
//
// The control point markers that are drawn on top of the canvas while a curve is
// being edited. Handles are rendered in window space (see IToolHandle), so they keep
// a constant size regardless of the canvas zoom.
//
// This is an addition over KolourPaint, which does not show where the control points
// are: the leash lines make it obvious which end point the dragged control point
// belongs to.
//
using System;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

internal enum CurveHandleKind
{
	/// <summary>An end point of the curve (on the curve).</summary>
	Endpoint,
	/// <summary>A control point of the curve (off the curve).</summary>
	ControlPoint,
	/// <summary>A dashed guide line from an end point to its control point.</summary>
	Leash,
}

internal sealed class CurveHandle : IToolHandle
{
	private const double GRAB_TOLERANCE = 6d;
	private const double ENDPOINT_RADIUS = 4.5;
	private const double CONTROL_POINT_RADIUS = 3.5;

	// Same visual language as Pinta's own selection handles.
	private static readonly Gdk.RGBA endpoint_fill_color = new () { Red = 0f, Green = 0f, Blue = 1f, Alpha = 1f };
	private static readonly Gdk.RGBA control_point_fill_color = new () { Red = 1f, Green = 0f, Blue = 1f, Alpha = 0.35f };
	private static readonly Gdk.RGBA outline_color = new () { Red = 1f, Green = 1f, Blue = 1f, Alpha = 0.7f };
	private static readonly Gdk.RGBA hover_color = new () { Red = 0.21f, Green = 0.52f, Blue = 0.89f, Alpha = 1f };
	private static readonly Gdk.RGBA leash_color = new () { Red = 0.7f, Green = 0f, Blue = 0.7f, Alpha = 0.6f };

	private readonly IWorkspaceService workspace;

	internal CurveHandleKind Kind { get; }

	/// <summary>Position in canvas coordinates.</summary>
	internal PointD CanvasPosition { get; set; }

	/// <summary>Second position, only used by <see cref="CurveHandleKind.Leash"/>.</summary>
	internal PointD SecondaryCanvasPosition { get; set; }

	public bool Active { get; set; }

	/// <summary>Highlights the marker, e.g. while the cursor is over it.</summary>
	internal bool Hovered { get; set; }

	internal CurveHandle (IWorkspaceService workspace, CurveHandleKind kind)
	{
		this.workspace = workspace;
		Kind = kind;
	}

	/// <summary>Bounding rectangle to pass to InvalidateWindowRect().</summary>
	public RectangleI InvalidateRect
		=> BoundingWindowRect ().Inflated (2, 2).ToInt ();

	public void Draw (Gtk.Snapshot snapshot)
	{
		if (Kind == CurveHandleKind.Leash) {
			DrawLeash (snapshot);
			return;
		}

		PointD window_point = workspace.CanvasPointToView (CanvasPosition);
		double radius = Kind == CurveHandleKind.Endpoint ? ENDPOINT_RADIUS : CONTROL_POINT_RADIUS;

		Gsk.PathBuilder builder = Gsk.PathBuilder.New ();
		builder.AddCircle (window_point.ToGraphenePoint (), (float) radius);
		Gsk.Path circle = builder.ToPath ();

		snapshot.AppendFill (
			circle,
			Gsk.FillRule.EvenOdd,
			Kind == CurveHandleKind.Endpoint ? endpoint_fill_color : control_point_fill_color);

		Gsk.Stroke stroke = Gsk.Stroke.New (lineWidth: Hovered ? 2.0f : 1.0f);
		snapshot.AppendStroke (circle, stroke, Hovered ? hover_color : outline_color);
	}

	/// <summary>
	/// True when the window point is close enough to grab this marker. The tolerance is
	/// larger than the marker itself so that it is easy to hit.
	/// </summary>
	internal bool ContainsPoint (PointD window_point)
	{
		if (Kind == CurveHandleKind.Leash)
			return false;

		PointD position = workspace.CanvasPointToView (CanvasPosition);

		return Math.Abs (position.X - window_point.X) <= GRAB_TOLERANCE &&
			Math.Abs (position.Y - window_point.Y) <= GRAB_TOLERANCE;
	}

	private void DrawLeash (Gtk.Snapshot snapshot)
	{
		PointD from = workspace.CanvasPointToView (CanvasPosition);
		PointD to = workspace.CanvasPointToView (SecondaryCanvasPosition);

		Gsk.PathBuilder builder = Gsk.PathBuilder.New ();
		builder.MoveTo ((float) from.X, (float) from.Y);
		builder.LineTo ((float) to.X, (float) to.Y);

		Gsk.Stroke stroke = Gsk.Stroke.New (lineWidth: 1.0f);
		stroke.SetDash ([3f, 3f]);

		snapshot.AppendStroke (builder.ToPath (), stroke, leash_color);
	}

	private RectangleD BoundingWindowRect ()
	{
		PointD position = workspace.CanvasPointToView (CanvasPosition);

		if (Kind != CurveHandleKind.Leash) {
			double diameter = 2 * (Kind == CurveHandleKind.Endpoint ? ENDPOINT_RADIUS : CONTROL_POINT_RADIUS);
			return new RectangleD (position.X - diameter / 2, position.Y - diameter / 2, diameter, diameter);
		}

		PointD secondary = workspace.CanvasPointToView (SecondaryCanvasPosition);
		return RectangleD.FromPoints (position, secondary);
	}
}
