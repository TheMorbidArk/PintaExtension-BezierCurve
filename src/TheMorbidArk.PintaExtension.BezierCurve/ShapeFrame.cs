//
// ShapeFrame.cs
//
// The dashed frame and the handles that a shape wears while it is still an object: the
// eight handles that resize it and the one that rotates it. All of them are drawn in
// window space (IToolHandle), so they keep their size whatever the zoom is.
//
// The frame is a quadrilateral rather than a rectangle: when the shape is turned the
// frame turns with it, which is what makes a rotated shape readable.
//
using System;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

/// <summary>Which of the eight resize handles a marker is.</summary>
internal enum FrameHandle
{
	TopLeft,
	Top,
	TopRight,
	Right,
	BottomRight,
	Bottom,
	BottomLeft,
	Left,
}

/// <summary>
/// One marker of the selection frame: a resize handle, the rotate handle, or the dashed
/// outline itself.
/// </summary>
internal sealed class ShapeFrame : IToolHandle
{
	private const double HANDLE_SIZE = 7d;
	private const double GRAB_TOLERANCE = 7d;
	private const double EDGE_TOLERANCE = 3d;
	private const double ROTATE_HANDLE_RADIUS = 5d;

	private static readonly Gdk.RGBA fill_color = new () { Red = 1f, Green = 1f, Blue = 1f, Alpha = 1f };
	private static readonly Gdk.RGBA outline_color = new () { Red = 0f, Green = 0f, Blue = 0f, Alpha = 0.75f };
	private static readonly Gdk.RGBA hover_color = new () { Red = 0.21f, Green = 0.52f, Blue = 0.89f, Alpha = 1f };
	private static readonly Gdk.RGBA frame_color = new () { Red = 0.1f, Green = 0.1f, Blue = 0.1f, Alpha = 0.85f };

	private readonly IWorkspaceService workspace;

	/// <summary>The cursor that says which way this handle resizes.</summary>
	private readonly Gdk.Cursor? cursor;

	/// <summary>Which resize handle this is, or null for the frame and the rotate handle.</summary>
	internal FrameHandle? Handle { get; }

	/// <summary>True for the round handle above the frame.</summary>
	internal bool IsRotateHandle { get; }

	/// <summary>
	/// The four corners of the frame in canvas coordinates, in the order top left,
	/// top right, bottom right, bottom left. Only used by the frame itself.
	/// </summary>
	internal PointD[] Corners { get; set; } = [];

	/// <summary>The position in canvas coordinates.</summary>
	internal PointD CanvasPosition { get; set; }

	public bool Active { get; set; }

	internal bool Hovered { get; set; }

	/// <summary>The cursor for this marker, or null when it has none of its own.</summary>
	internal Gdk.Cursor? Cursor => cursor;

	private ShapeFrame (IWorkspaceService workspace, FrameHandle? handle, bool rotate, Gdk.Cursor? cursor)
	{
		this.workspace = workspace;
		Handle = handle;
		IsRotateHandle = rotate;
		this.cursor = cursor;
	}

	/// <summary>The dashed outline around the shape.</summary>
	internal static ShapeFrame CreateFrame (IWorkspaceService workspace)
		=> new (workspace, handle: null, rotate: false, cursor: null);

	/// <summary>One of the eight resize handles.</summary>
	internal static ShapeFrame CreateHandle (IWorkspaceService workspace, FrameHandle handle)
		=> new (workspace, handle, rotate: false, GdkExtensions.CursorFromName (CursorName (handle)));

	/// <summary>The round handle that turns the shape.</summary>
	internal static ShapeFrame CreateRotateHandle (IWorkspaceService workspace, Gdk.Cursor cursor)
		=> new (workspace, handle: null, rotate: true, cursor);

	/// <summary>The standard cursor that matches the direction a handle resizes in.</summary>
	private static string CursorName (FrameHandle handle)
		=> handle switch {
			FrameHandle.TopLeft => Pinta.Resources.StandardCursors.ResizeNW,
			FrameHandle.Top => Pinta.Resources.StandardCursors.ResizeN,
			FrameHandle.TopRight => Pinta.Resources.StandardCursors.ResizeNE,
			FrameHandle.Right => Pinta.Resources.StandardCursors.ResizeE,
			FrameHandle.BottomRight => Pinta.Resources.StandardCursors.ResizeSE,
			FrameHandle.Bottom => Pinta.Resources.StandardCursors.ResizeS,
			FrameHandle.BottomLeft => Pinta.Resources.StandardCursors.ResizeSW,
			_ => Pinta.Resources.StandardCursors.ResizeW,
		};

	/// <summary>Bounding rectangle to pass to InvalidateWindowRect().</summary>
	public RectangleI InvalidateRect
		=> BoundingWindowRect ().Inflated (3, 3).ToInt ();

	public void Draw (Gtk.Snapshot snapshot)
	{
		if (Handle is null && !IsRotateHandle) {
			DrawFrame (snapshot);
			return;
		}

		PointD position = workspace.CanvasPointToView (CanvasPosition);

		if (IsRotateHandle) {
			DrawRotateHandle (snapshot, position);
			return;
		}

		// A small square, like the handles of Pinta's own selection.
		double half = HANDLE_SIZE / 2;
		Gsk.PathBuilder builder = Gsk.PathBuilder.New ();
		builder.MoveTo ((float) (position.X - half), (float) (position.Y - half));
		builder.LineTo ((float) (position.X + half), (float) (position.Y - half));
		builder.LineTo ((float) (position.X + half), (float) (position.Y + half));
		builder.LineTo ((float) (position.X - half), (float) (position.Y + half));
		builder.Close ();

		Gsk.Path square = builder.ToPath ();
		snapshot.AppendFill (square, Gsk.FillRule.EvenOdd, fill_color);
		snapshot.AppendStroke (square, Gsk.Stroke.New (lineWidth: Hovered ? 2f : 1f), Hovered ? hover_color : outline_color);
	}

	/// <summary>The round handle above the frame, with the stalk that ties it to the frame.</summary>
	private void DrawRotateHandle (Gtk.Snapshot snapshot, PointD position)
	{
		Gsk.PathBuilder circle = Gsk.PathBuilder.New ();
		circle.AddCircle (position.ToGraphenePoint (), (float) ROTATE_HANDLE_RADIUS);

		Gsk.Path path = circle.ToPath ();
		snapshot.AppendFill (path, Gsk.FillRule.EvenOdd, fill_color);
		snapshot.AppendStroke (path, Gsk.Stroke.New (lineWidth: Hovered ? 2.5f : 1.5f), Hovered ? hover_color : outline_color);

		// A little stalk down to the frame, so it reads as being attached to it.
		PointD anchor = workspace.CanvasPointToView (FrameAnchor);

		Gsk.PathBuilder stalk = Gsk.PathBuilder.New ();
		stalk.MoveTo ((float) position.X, (float) (position.Y + ROTATE_HANDLE_RADIUS));
		stalk.LineTo ((float) anchor.X, (float) anchor.Y);
		snapshot.AppendStroke (stalk.ToPath (), Gsk.Stroke.New (lineWidth: 1f), frame_color);
	}

	/// <summary>The point of the frame that the rotate handle hangs from.</summary>
	internal PointD FrameAnchor { get; set; }

	/// <summary>True when the window point is on this marker.</summary>
	internal bool ContainsPoint (PointD window_point)
	{
		if (Handle is null && !IsRotateHandle)
			return false;

		PointD position = workspace.CanvasPointToView (CanvasPosition);

		return Math.Abs (position.X - window_point.X) <= GRAB_TOLERANCE &&
			Math.Abs (position.Y - window_point.Y) <= GRAB_TOLERANCE;
	}

	/// <summary>
	/// True when the window point is inside the frame, which is what drags the whole shape
	/// around. The frame is a quadrilateral, so this works for a turned shape too.
	/// </summary>
	internal bool ContainsInside (PointD window_point)
	{
		if (Handle is not null || IsRotateHandle)
			return false;

		if (Corners.Length != 4)
			return false;

		PointD[] corners = new PointD[4];

		for (int i = 0; i < 4; ++i) {
			corners[i] = workspace.CanvasPointToView (Corners[i]);

			// A press right on the border starts a new curve instead of dragging.
			if (Math.Abs (corners[i].X - window_point.X) < EDGE_TOLERANCE &&
				Math.Abs (corners[i].Y - window_point.Y) < EDGE_TOLERANCE)
				return false;
		}

		return InsideQuad (corners, window_point);
	}

	/// <summary>Point in quadrilateral, by the sum of the signs of the cross products.</summary>
	private static bool InsideQuad (PointD[] quad, PointD point)
	{
		bool positive = false;
		bool negative = false;

		for (int i = 0; i < 4; ++i) {
			PointD a = quad[i];
			PointD b = quad[(i + 1) % 4];

			double cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

			if (cross > 1e-9)
				positive = true;
			else if (cross < -1e-9)
				negative = true;

			if (positive && negative)
				return false;
		}

		return true;
	}

	private void DrawFrame (Gtk.Snapshot snapshot)
	{
		if (Corners.Length != 4)
			return;

		PointD a = workspace.CanvasPointToView (Corners[0]);

		Gsk.PathBuilder builder = Gsk.PathBuilder.New ();
		builder.MoveTo ((float) a.X, (float) a.Y);

		for (int i = 1; i < 4; ++i) {
			PointD corner = workspace.CanvasPointToView (Corners[i]);
			builder.LineTo ((float) corner.X, (float) corner.Y);
		}

		builder.Close ();

		Gsk.Stroke stroke = Gsk.Stroke.New (lineWidth: 1f);
		stroke.SetDash ([4f, 4f]);

		snapshot.AppendStroke (builder.ToPath (), stroke, frame_color);
	}

	private RectangleD BoundingWindowRect ()
	{
		PointD position = workspace.CanvasPointToView (CanvasPosition);

		if (Handle is not null)
			return new RectangleD (position.X - 8, position.Y - 8, 16, 16);

		if (IsRotateHandle) {
			PointD anchor = workspace.CanvasPointToView (FrameAnchor);
			RectangleD stalk = RectangleD.FromPoints (position, anchor).Inflated (6, 6);

			return stalk;
		}

		if (Corners.Length != 4)
			return new RectangleD (position.X, position.Y, 1, 1);

		RectangleD rect = RectangleD.FromPoints (
			workspace.CanvasPointToView (Corners[0]),
			workspace.CanvasPointToView (Corners[2]));

		for (int i = 1; i < 4; ++i)
			rect = rect.Union (RectangleD.FromPoints (
				workspace.CanvasPointToView (Corners[i]),
				workspace.CanvasPointToView (Corners[i])));

		return rect.Inflated (2, 2);
	}
}
