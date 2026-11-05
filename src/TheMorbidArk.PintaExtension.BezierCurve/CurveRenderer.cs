//
// CurveRenderer.cs
//
// Everything that turns a curve into pixels. Kept separate from the tool so that the
// drawing can be tested against an in-memory surface, without a window.
//
using System;
using Cairo;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

/// <summary>The appearance of one curve.</summary>
internal readonly record struct CurveStyle (
	Color Color,
	int BrushWidth,
	bool Antialiasing,
	bool AlphaBlending,
	string DashPattern,
	EndShape StartEnds = EndShape.Round,
	EndShape FinishEnds = EndShape.Round,
	LineJoin Join = LineJoin.Round,
	Color? GradientEnd = null,
	int TaperStartWidth = 0,
	int TaperEndWidth = 0,
	bool Fill = false);

/// <summary>
/// The shape of the two ends of a stroke.
/// </summary>
internal enum EndShape
{
	/// <summary>A half circle; the stroke reaches half a width over the end point.</summary>
	Round,

	/// <summary>A sharp point; the stroke tapers to nothing at the end point.</summary>
	Pointed,

	/// <summary>A square that reaches half a width over the end point.</summary>
	Square,
}

internal static class CurveRenderer
{
	/// <summary>How many pieces a tapered curve is drawn from.</summary>
	private const int TAPER_SEGMENTS = 24;

	/// <summary>
	/// Strokes the curve into the context. KolourPaint uses a cubic path with round caps
	/// and joins, so the same is used here; the caller is responsible for clipping.
	/// </summary>
	internal static void Draw (Context g, BezierCurve curve, in CurveStyle style)
	{
		g.Antialias = style.Antialiasing ? Antialias.Subpixel : Antialias.None;
		g.LineWidth = style.BrushWidth;
		g.LineJoin = style.Join;
		g.SetDashFromString (style.DashPattern, style.BrushWidth, CapOf (style.StartEnds));

		if (style.AlphaBlending)
			g.SetBlendMode (BlendMode.Normal);
		else
			g.Operator = Operator.Source;

		SetSource (g, curve, style);

		double offset = PixelOffset (style);

		if (IsSinglePoint (curve)) {
			// KolourPaint draws a single pixel when every point is in the same place; a zero
			// length path with round caps would look the same, but this is exact.
			g.Arc (curve.P0.X + offset, curve.P0.Y + offset, style.BrushWidth / 2d, 0d, System.Math.PI * 2d);
			g.Fill ();
			return;
		}

		// The fill of a closed curve goes under its stroke.
		if (curve.Closed && style.Fill) {
			BuildPath (g, curve, offset);
			g.Fill ();
		}

		// Either end can be a sharp point, and a closed curve has no ends at all.
		if (!curve.Closed && (style.StartEnds == EndShape.Pointed || style.FinishEnds == EndShape.Pointed)) {
			DrawPointed (g, curve, style, offset);
			return;
		}

		// Cairo has one cap for the whole path, so two different ends are drawn as two
		// halves, each with the cap of its own end.
		if (!curve.Closed && style.StartEnds != style.FinishEnds) {
			DrawTwoHalves (g, curve, style, offset);
			return;
		}

		g.LineCap = CapOf (style.StartEnds);

		if (HasTaper (style)) {
			StrokeTapered (g, curve, style, offset);
		} else {
			BuildPath (g, curve, offset);
			g.Stroke ();
		}
	}

	/// <summary>The Cairo cap that matches the shape of an end.</summary>
	private static LineCap CapOf (EndShape ends)
		=> ends switch {
			EndShape.Square => LineCap.Square,
			EndShape.Pointed => LineCap.Butt,
			_ => LineCap.Round,
		};

	/// <summary>
	/// Draws the two halves of the curve with a cap each, for the case where the two
	/// ends are round, square and pointed in different ways. Both halves meet in the
	/// middle, so the seam is invisible.
	/// </summary>
	private static void DrawTwoHalves (Context g, BezierCurve curve, in CurveStyle style, double offset)
	{
		BezierCurve first = CurveGeometry.Subdivide (curve, 0d, 0.5d);
		BezierCurve second = CurveGeometry.Subdivide (curve, 0.5d, 1d);

		StrokeHalf (g, first, style, offset, style.StartEnds, 0d);
		StrokeHalf (g, second, style, offset, style.FinishEnds, 0.5d);
	}

	/// <summary>Draws one half of the curve with the cap of the given end.</summary>
	/// <param name="half">The half of the curve to draw.</param>
	/// <param name="t">Where that half starts along the whole curve.</param>
	private static void StrokeHalf (
		Context g,
		BezierCurve half,
		in CurveStyle style,
		double offset,
		EndShape ends,
		double t)
	{
		g.LineCap = CapOf (ends);

		if (!HasTaper (style)) {
			g.LineWidth = style.BrushWidth;

			g.MoveTo (half.P0.X + offset, half.P0.Y + offset);
			g.CurveTo (
				half.P1.X + offset, half.P1.Y + offset,
				half.P2.X + offset, half.P2.Y + offset,
				half.P3.X + offset, half.P3.Y + offset);
			g.Stroke ();

			return;
		}

		// A tapered half is drawn in pieces as well, so that its width follows the curve.
		const int pieces = TAPER_SEGMENTS / 2;

		for (int i = 0; i < pieces; ++i) {
			double t0 = (double) i / pieces;
			double t1 = (double) (i + 1) / pieces;

			// The width is taken where the piece sits on the whole curve.
			g.LineWidth = Math.Max (TaperWidth (style, t + (t0 + t1) / 2d * 0.5d), 0.5d);

			BezierCurve piece = CurveGeometry.Subdivide (half, t0, t1);

			g.MoveTo (piece.P0.X + offset, piece.P0.Y + offset);
			g.CurveTo (
				piece.P1.X + offset, piece.P1.Y + offset,
				piece.P2.X + offset, piece.P2.Y + offset,
				piece.P3.X + offset, piece.P3.Y + offset);
			g.Stroke ();
		}
	}

	/// <summary>
	/// Draws a stroke that ends in sharp points. Cairo has no such cap, so the curve is
	/// drawn in pieces whose width grows from the point to the full width - the curve
	/// itself is not cut back, so the points sit on the end points. Each end is pointed
	/// on its own: a curve with one pointed and one round end grows from the point and
	/// keeps the full width all the way to the round end.
	/// </summary>
	private static void DrawPointed (Context g, BezierCurve curve, in CurveStyle style, double offset)
	{
		bool start_pointed = style.StartEnds == EndShape.Pointed;
		bool finish_pointed = style.FinishEnds == EndShape.Pointed;

		// Beyond a pointed end the stroke keeps the cap of the other end, so that the
		// unpainted half of the path does not leave a hard edge.
		g.LineCap = CapOf (start_pointed ? style.FinishEnds : style.StartEnds);

		double widest = HasTaper (style)
			? Math.Max (TaperWidth (style, 0d), TaperWidth (style, 1d))
			: style.BrushWidth;

		double length = CurveGeometry.Length (curve);

		// A very short curve would be all point and no body, so the ends stop growing
		// once the curve is shorter than twice the width.
		double ramp = Math.Min (1d, length / (2d * Math.Max (widest, 1d)));

		for (int i = 0; i < TAPER_SEGMENTS; ++i) {
			double t0 = (double) i / TAPER_SEGMENTS;
			double t1 = (double) (i + 1) / TAPER_SEGMENTS;
			double middle = (t0 + t1) / 2d;

			// How far along the ramp from the pointed end this piece is; 1 when that end
			// is not pointed at all.
			double from_start = start_pointed ? middle / (0.5 * ramp) : 1d;
			double from_finish = finish_pointed ? (1d - middle) / (0.5 * ramp) : 1d;

			double profile = Math.Clamp (Math.Min (from_start, from_finish), 0d, 1d);

			g.LineWidth = Math.Max (widest * profile, 0.5d);

			BezierCurve piece = CurveGeometry.Subdivide (curve, t0, t1);

			g.MoveTo (piece.P0.X + offset, piece.P0.Y + offset);
			g.CurveTo (
				piece.P1.X + offset, piece.P1.Y + offset,
				piece.P2.X + offset, piece.P2.Y + offset,
				piece.P3.X + offset, piece.P3.Y + offset);
			g.Stroke ();
		}
	}

	/// <summary>
	/// The path of the curve: the cubic Bezier and, when the curve is closed, the
	/// segment that leads back to its start.
	/// </summary>
	private static void BuildPath (Context g, BezierCurve curve, double offset)
	{
		g.MoveTo (curve.P0.X + offset, curve.P0.Y + offset);
		g.CurveTo (
			curve.P1.X + offset, curve.P1.Y + offset,
			curve.P2.X + offset, curve.P2.Y + offset,
			curve.P3.X + offset, curve.P3.Y + offset);

		if (!curve.Closed)
			return;

		if (curve.SmoothClose) {
			(PointD first, PointD second) = CurveGeometry.SeamControls (curve);

			g.CurveTo (
				first.X + offset, first.Y + offset,
				second.X + offset, second.Y + offset,
				curve.P0.X + offset, curve.P0.Y + offset);
		}

		g.ClosePath ();
	}

	/// <summary>
	/// Draws the curve in pieces, so that the width of the stroke can change along it.
	/// The curve is cut into <see cref="TAPER_SEGMENTS"/> pieces, each of which is
	/// stroked with the width of the curve at its middle.
	/// </summary>
	private static void StrokeTapered (Context g, BezierCurve curve, in CurveStyle style, double offset)
	{
		for (int i = 0; i < TAPER_SEGMENTS; ++i) {
			double t0 = (double) i / TAPER_SEGMENTS;
			double t1 = (double) (i + 1) / TAPER_SEGMENTS;

			BezierCurve piece = CurveGeometry.Subdivide (curve, t0, t1);

			g.LineWidth = TaperWidth (style, (t0 + t1) / 2d);
			g.MoveTo (piece.P0.X + offset, piece.P0.Y + offset);
			g.CurveTo (
				piece.P1.X + offset, piece.P1.Y + offset,
				piece.P2.X + offset, piece.P2.Y + offset,
				piece.P3.X + offset, piece.P3.Y + offset);
			g.Stroke ();
		}

		if (!curve.Closed)
			return;

		// The segment that closes the curve is not part of the Bezier, so it is drawn
		// with the width the curve ends with.
		g.LineWidth = TaperWidth (style, 1d);
		g.MoveTo (curve.P3.X + offset, curve.P3.Y + offset);
		g.LineTo (curve.P0.X + offset, curve.P0.Y + offset);
		g.Stroke ();
	}

	/// <summary>True when the width of the stroke changes along the curve.</summary>
	internal static bool HasTaper (in CurveStyle style)
		=> style.TaperStartWidth > 0 || style.TaperEndWidth > 0;

	/// <summary>
	/// The width of the stroke at the given parameter of the curve. A width of zero
	/// means that the brush width is used.
	/// </summary>
	internal static double TaperWidth (in CurveStyle style, double t)
	{
		double start = style.TaperStartWidth > 0 ? style.TaperStartWidth : style.BrushWidth;
		double end = style.TaperEndWidth > 0 ? style.TaperEndWidth : style.BrushWidth;

		return start + (end - start) * t;
	}

	/// <summary>
	/// The colour of the stroke: either a single colour or a gradient between the two
	/// ends of the curve.
	/// </summary>
	private static void SetSource (Context g, BezierCurve curve, in CurveStyle style)
	{
		if (style.GradientEnd is not Color end || curve.P0 == curve.P3) {
			g.SetSourceColor (style.Color);
			return;
		}

		// The gradient runs from the start of the curve to its end.
		LinearGradient gradient = new (curve.P0.X, curve.P0.Y, curve.P3.X, curve.P3.Y);
		gradient.AddColorStop (0, style.Color);
		gradient.AddColorStop (1, end);

		g.SetSource (gradient);
	}

	/// <summary>
	/// Cairo centres a stroke on the path, so with antialiasing off the pixels on both
	/// sides of the path are only half covered and the rasterizer has to pick one - the
	/// stroke ends up half a pixel off. Nudging the path by half a pixel (for odd widths;
	/// even widths already sit between two pixel rows) makes it cover whole pixels, which
	/// is what pixel art needs. Pinta's pencil tool does the same.
	/// </summary>
	internal static double PixelOffset (in CurveStyle style)
	{
		int width = style.BrushWidth;

		// A tapered curve is drawn with many different widths, so the widest one decides
		// where the path is put.
		if (style.TaperStartWidth > width)
			width = style.TaperStartWidth;

		if (style.TaperEndWidth > width)
			width = style.TaperEndWidth;

		return !style.Antialiasing && width % 2 == 1 ? 0.5d : 0d;
	}

	/// <summary>True when every point of the curve is in the same place.</summary>
	internal static bool IsSinglePoint (BezierCurve curve)
		=> curve.P0 == curve.P1 && curve.P1 == curve.P2 && curve.P2 == curve.P3;
}
