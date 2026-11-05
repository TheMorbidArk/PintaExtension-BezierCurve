//
// CurveRendererTests.cs
//
// The renderer draws into a plain Cairo image surface, so the pixels can be inspected
// without starting Pinta.
//
using System;
using System.Collections.Generic;
using Cairo;
using NUnit.Framework;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class CurveRendererTests
{
	[SetUp]
	public void RequireCairo () => CairoNativeLibrary.RequireLibCairo ();

	private const int WIDTH = 120;
	private const int HEIGHT = 60;

	private static CurveStyle Style (
		int brush_width = 2,
		bool antialiasing = false,
		bool alpha_blending = true,
		string dash_pattern = "-",
		Color? color = null,
		EndShape start_ends = EndShape.Round,
		EndShape finish_ends = EndShape.Round,
		LineJoin join = LineJoin.Round,
		Color? gradient_end = null,
		int taper_start_width = 0,
		int taper_end_width = 0,
		bool fill = false)
		=> new (
			color ?? new Color (0, 0, 0),
			brush_width,
			antialiasing,
			alpha_blending,
			dash_pattern,
			start_ends,
			finish_ends,
			join,
			gradient_end,
			taper_start_width,
			taper_end_width,
			fill);

	private static ImageSurface Render (BezierCurve curve, CurveStyle style, bool white_background = false)
	{
		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, WIDTH, HEIGHT);

		using (Context g = new (surface)) {
			if (white_background) {
				g.SetSourceColor (new Color (1, 1, 1));
				g.Paint ();
			}

			CurveRenderer.Draw (g, curve, style);
		}

		surface.Flush ();

		return surface;
	}

	/// <summary>A horizontal "curve": both end points and both controls on the same line.</summary>
	private static BezierCurve HorizontalLine (int y)
		=> new (new PointD (10, y), new PointD (10, y), new PointD (110, y), new PointD (110, y));

	private static ColorBgra Pixel (ImageSurface surface, int x, int y)
		=> surface.GetReadOnlyPixelData ()[y * WIDTH + x];

	private static (List<int> Dark, int Faint) DarkRows (ImageSurface surface, int x = 60)
	{
		List<int> dark = [];
		int faint = 0;

		for (int y = 0; y < HEIGHT; ++y) {
			ColorBgra pixel = Pixel (surface, x, y);

			if (pixel.A >= 250)
				dark.Add (y);
			else if (pixel.A > 0)
				faint++;
		}

		return (dark, faint);
	}

	private static int PaintedInRow (ImageSurface surface, int y)
	{
		int painted = 0;

		for (int x = 10; x < WIDTH - 10; ++x)
			if (Pixel (surface, x, y).A > 0)
				painted++;

		return painted;
	}

	private static int PaintedInColumn (ImageSurface surface, int x)
	{
		int painted = 0;

		for (int y = 0; y < HEIGHT; ++y)
			if (Pixel (surface, x, y).A > 0)
				painted++;

		return painted;
	}

	[Test]
	public void AntialiasingOffPaintsOneCrispRowForAOnePixelLine ()
	{
		using ImageSurface surface = Render (HorizontalLine (30), Style (brush_width: 1));

		(List<int> dark, int faint) = DarkRows (surface);

		Assert.That (dark, Is.EqualTo (new[] { 30 }));
		Assert.That (faint, Is.Zero);
	}

	[Test]
	public void AntialiasingOffCentresOddWidthsOnTheDrag ()
	{
		using ImageSurface one = Render (HorizontalLine (30), Style (brush_width: 1));
		using ImageSurface three = Render (HorizontalLine (30), Style (brush_width: 3));
		using ImageSurface four = Render (HorizontalLine (30), Style (brush_width: 4));

		Assert.That (DarkRows (one).Dark, Is.EqualTo (new[] { 30 }));
		Assert.That (DarkRows (three).Dark, Is.EqualTo (new[] { 29, 30, 31 }));
		Assert.That (DarkRows (four).Dark, Is.EqualTo (new[] { 28, 29, 30, 31 }));
	}

	[Test]
	public void AntialiasingOffStillFillsTheWholeLine ()
	{
		using ImageSurface surface = Render (HorizontalLine (30), Style (brush_width: 2));

		Assert.That (PaintedInRow (surface, 30), Is.EqualTo (100));
	}

	[Test]
	public void AntialiasingOnProducesSoftEdgesInsteadOfWholePixels ()
	{
		using ImageSurface surface = Render (HorizontalLine (30), Style (brush_width: 1, antialiasing: true));

		(List<int> dark, int faint) = DarkRows (surface);

		Assert.That (dark, Is.Empty, "a half covered row must not be fully opaque");
		Assert.That (faint, Is.EqualTo (2), "the row above and below should be partially covered");
	}

	[Test]
	public void AShortDashPatternLeavesGaps ()
	{
		using ImageSurface solid = Render (HorizontalLine (30), Style (brush_width: 2));
		using ImageSurface dashed = Render (HorizontalLine (30), Style (brush_width: 2, dash_pattern: " -"));

		int solid_painted = PaintedInRow (solid, 30);
		int dashed_painted = PaintedInRow (dashed, 30);

		Assert.That (solid_painted, Is.EqualTo (100));
		Assert.That (dashed_painted, Is.GreaterThan (0));
		Assert.That (dashed_painted, Is.LessThan (solid_painted));
	}

	[Test]
	public void ASinglePointCurveDrawsADot ()
	{
		PointD point = new (60, 30);
		BezierCurve dot = new (point, point, point, point);

		using ImageSurface surface = Render (dot, Style (brush_width: 4));

		Assert.That (Pixel (surface, 60, 30).A, Is.GreaterThan (0));
		Assert.That (Pixel (surface, 20, 30).A, Is.Zero);
		Assert.That (Pixel (surface, 100, 30).A, Is.Zero);
	}

	[Test]
	public void AlphaBlendingOffReplacesTheBackground ()
	{
		Color half_black = new (0, 0, 0, 0.5);

		using ImageSurface blended = Render (HorizontalLine (30), Style (brush_width: 2, alpha_blending: true, color: half_black), white_background: true);
		using ImageSurface replaced = Render (HorizontalLine (30), Style (brush_width: 2, alpha_blending: false, color: half_black), white_background: true);

		// Blending keeps the surface opaque; overwriting stores the 50% alpha.
		Assert.That (Pixel (blended, 60, 30).A, Is.EqualTo (255));
		Assert.That (Pixel (replaced, 60, 30).A, Is.InRange (120, 140));
	}

	[Test]
	public void ThePixelOffsetOnlyAppliesToOddWidthsWithoutAntialiasing ()
	{
		Assert.That (CurveRenderer.PixelOffset (Style (brush_width: 1, antialiasing: false)), Is.EqualTo (0.5));
		Assert.That (CurveRenderer.PixelOffset (Style (brush_width: 3, antialiasing: false)), Is.EqualTo (0.5));
		Assert.That (CurveRenderer.PixelOffset (Style (brush_width: 2, antialiasing: false)), Is.Zero);
		Assert.That (CurveRenderer.PixelOffset (Style (brush_width: 1, antialiasing: true)), Is.Zero);
	}

	[Test]
	public void IsSinglePointDetectsADegenerateCurve ()
	{
		PointD a = new (1, 2);

		Assert.That (CurveRenderer.IsSinglePoint (new BezierCurve (a, a, a, a)), Is.True);
		Assert.That (CurveRenderer.IsSinglePoint (new BezierCurve (a, a, new PointD (3, 4), a)), Is.False);
	}

	#region The new options

	/// <summary>A curve that goes around a rectangle, so that it can be closed.</summary>
	private static BezierCurve Rectangle ()
		=> new (
			new PointD (20, 10),
			new PointD (100, 10),
			new PointD (100, 50),
			new PointD (20, 50));

	[Test]
	public void AClosedCurveIsFilledInside ()
	{
		BezierCurve curve = Rectangle () with { Closed = true };

		using ImageSurface surface = Render (curve, Style (fill: true));

		Assert.That (Pixel (surface, 60, 30).A, Is.GreaterThan (0), "the inside must be filled");
		Assert.That (Pixel (surface, 5, 30).A, Is.Zero, "the outside must stay empty");
	}

	[Test]
	public void AClosedCurveIsNotFilledWhenTheFillIsOff ()
	{
		BezierCurve curve = Rectangle () with { Closed = true };

		using ImageSurface surface = Render (curve, Style (fill: false));

		Assert.That (Pixel (surface, 60, 30).A, Is.Zero);
	}

	[Test]
	public void ARoundEndReachesOverTheEndPointAndAPointedOneDoesNot ()
	{
		using ImageSurface round = Render (HorizontalLine (30), Style (start_ends: EndShape.Round, finish_ends: EndShape.Round));
		using ImageSurface pointed = Render (HorizontalLine (30), Style (start_ends: EndShape.Pointed, finish_ends: EndShape.Pointed));

		Assert.That (Pixel (round, 9, 30).A, Is.GreaterThan (0), "a round end reaches over the end point");
		Assert.That (Pixel (pointed, 9, 30).A, Is.Zero, "a pointed end does not reach over the end point");
	}

	[Test]
	public void APointedEndTapersToNothingAtTheEndPoint ()
	{
		using ImageSurface surface = Render (HorizontalLine (30), Style (brush_width: 10, start_ends: EndShape.Pointed, finish_ends: EndShape.Pointed));

		// At the end point there is only a sliver, and the stroke grows towards the middle.
		int at_end = PaintedInColumn (surface, 10);
		int near_end = PaintedInColumn (surface, 20);
		int middle = PaintedInColumn (surface, 60);

		Assert.That (at_end, Is.LessThan (near_end), $"{at_end} then {near_end}");
		Assert.That (near_end, Is.LessThan (middle), $"{near_end} then {middle}");
		Assert.That (PaintedInColumn (surface, 8), Is.Zero, "nothing reaches over the end point");
	}

	[Test]
	public void TheTwoEndsArePickedOnTheirOwn ()
	{
		// Pointed at the start, round at the end: thin at the left, full at the right.
		using ImageSurface surface = Render (
			HorizontalLine (30),
			Style (brush_width: 10, start_ends: EndShape.Pointed, finish_ends: EndShape.Round));

		int at_start = PaintedInColumn (surface, 16);
		int at_end = PaintedInColumn (surface, 104);

		Assert.That (at_start, Is.LessThan (10), $"the start is pointed ({at_start} rows)");
		Assert.That (at_end, Is.GreaterThanOrEqualTo (10), $"the end is round ({at_end} rows)");

		// The pointed start does not reach over its end point, the round end does.
		Assert.That (Pixel (surface, 9, 30).A, Is.Zero, "the pointed end stops at the end point");
		Assert.That (Pixel (surface, 111, 30).A, Is.GreaterThan (0), "the round end reaches over the end point");
	}

	[Test]
	public void ASquareStartAndARoundEndAreBothDrawn ()
	{
		using ImageSurface surface = Render (
			HorizontalLine (30),
			Style (brush_width: 6, start_ends: EndShape.Square, finish_ends: EndShape.Round));

		// The square end reaches over its end point as well, with a flat outline.
		Assert.That (Pixel (surface, 8, 30).A, Is.GreaterThan (0), "the square end reaches over the end point");
		Assert.That (Pixel (surface, 111, 30).A, Is.GreaterThan (0), "the round end reaches over the end point");
	}

	[Test]
	public void APointedEndIsThinnerThanARoundOneAtTheSamePlace ()
	{
		using ImageSurface round = Render (HorizontalLine (30), Style (brush_width: 10, start_ends: EndShape.Round, finish_ends: EndShape.Round));
		using ImageSurface pointed = Render (HorizontalLine (30), Style (brush_width: 10, start_ends: EndShape.Pointed, finish_ends: EndShape.Pointed));

		int round_here = PaintedInColumn (round, 16);
		int pointed_here = PaintedInColumn (pointed, 16);

		Assert.That (pointed_here, Is.LessThan (round_here), $"{pointed_here} vs {round_here}");
		Assert.That (PaintedInColumn (pointed, 60), Is.EqualTo (PaintedInColumn (round, 60)), "the middle is unchanged");
	}

	[Test]
	public void TheGradientPutsTheTwoColoursAtTheTwoEnds ()
	{
		using ImageSurface surface = Render (
			HorizontalLine (30),
			Style (color: new Color (1, 0, 0), gradient_end: new Color (0, 0, 1)));

		ColorBgra start = Pixel (surface, 15, 30);
		ColorBgra end = Pixel (surface, 105, 30);

		Assert.That (start.R, Is.GreaterThan (200));
		Assert.That (start.B, Is.LessThan (50));

		Assert.That (end.B, Is.GreaterThan (200));
		Assert.That (end.R, Is.LessThan (50));
	}

	[Test]
	public void ATaperMakesTheStrokeThinnerTowardsTheEnd ()
	{
		using ImageSurface surface = Render (
			HorizontalLine (30),
			Style (brush_width: 8, taper_start_width: 8, taper_end_width: 1));

		int at_start = PaintedInColumn (surface, 15);
		int at_end = PaintedInColumn (surface, 105);

		// The width changes along the curve, so the exact number of rows depends on
		// where the pieces of the curve fall on the pixels.
		Assert.That (at_start, Is.GreaterThanOrEqualTo (6));
		Assert.That (at_end, Is.LessThanOrEqualTo (2));
		Assert.That (at_end, Is.LessThan (at_start));
	}

	[Test]
	public void TheTaperWidthChangesAlongTheCurve ()
	{
		CurveStyle style = Style (brush_width: 4) with { TaperStartWidth = 8, TaperEndWidth = 2 };

		Assert.That (CurveRenderer.TaperWidth (style, 0d), Is.EqualTo (8));
		Assert.That (CurveRenderer.TaperWidth (style, 0.5d), Is.EqualTo (5));
		Assert.That (CurveRenderer.TaperWidth (style, 1d), Is.EqualTo (2));

		Assert.That (CurveRenderer.HasTaper (style), Is.True);
		Assert.That (CurveRenderer.HasTaper (Style ()), Is.False);
	}

	[Test]
	public void ThePixelOffsetUsesTheWidestPartOfATaperedCurve ()
	{
		CurveStyle style = Style (brush_width: 2, antialiasing: false) with { TaperEndWidth = 5 };

		Assert.That (CurveRenderer.PixelOffset (style), Is.EqualTo (0.5));
	}

	#endregion
}
