//
// StrokePreviews.cs
//
// The little pictures that the buttons of the style panel show above their names.
// Each one is drawn with Cairo into the drawing area of the button, so they follow the
// colour of the theme and need no image files.
//
using System;
using Cairo;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

internal static class StrokePreviews
{
	private static readonly Gdk.RGBA fallback_colour = new () { Red = 0.3f, Green = 0.3f, Blue = 0.3f, Alpha = 1f };

	/// <summary>A short line that shows how the ends of a stroke look.</summary>
	internal static Action<Cairo.Context, int, int> End (EndShape ends)
		=> (g, width, height) => {
			double y = height / 2d;

			// A pointed end tapers to nothing, so the line is longer to make the point
			// visible next to the other two.
			double left = width * (ends == EndShape.Pointed ? 0.06 : 0.18);
			double right = width * (ends == EndShape.Pointed ? 0.94 : 0.82);

			if (ends != EndShape.Pointed) {
				Stroke (g, ends, y, left, right);
				return;
			}

			// The two ends are drawn as thin points.
			g.SetSourceColor (TextColour ());
			g.LineWidth = 2;

			for (int i = 0; i < 9; ++i) {
				double t0 = (double) i / 9;
				double t1 = (double) (i + 1) / 9;

				// Half a diamond: wide in the middle, a point at either end.
				double w0 = 2.5 * Math.Sin (Math.PI * t0);
				double w1 = 2.5 * Math.Sin (Math.PI * t1);

				g.MoveTo (left + (right - left) * t0, y - w0);
				g.LineTo (left + (right - left) * (t0 + t1) / 2d + 0.3, y - (w0 + w1) / 2d);
				g.LineTo (left + (right - left) * t0, y + w0);
				g.Fill ();
			}
		};

	/// <summary>A corner that shows how a join looks.</summary>
	internal static Action<Cairo.Context, int, int> Join (LineJoin join)
		=> (g, width, height) => {
			double left = width * 0.18;
			double right = width * 0.82;

			g.LineWidth = 3;
			g.LineCap = LineCap.Round;
			g.LineJoin = join;
			g.SetSourceColor (TextColour ());

			g.MoveTo (left, height - 2);
			g.LineTo (left, 3);
			g.LineTo (right, 3);
			g.Stroke ();
		};

	/// <summary>A line that gets thinner towards its end.</summary>
	internal static Action<Cairo.Context, int, int> Taper (bool towards_end)
		=> (g, width, height) => {
			double y = height / 2d;
			double left = 2;
			double right = width - 2;

			double start = towards_end ? 5 : 1;
			double end = towards_end ? 1 : 5;

			const int pieces = 12;

			g.LineCap = LineCap.Butt;
			g.SetSourceColor (TextColour ());

			for (int i = 0; i < pieces; ++i) {
				double t0 = (double) i / pieces;
				double t1 = (double) (i + 1) / pieces;

				g.LineWidth = start + (end - start) * (t0 + t1) / 2d;
				g.MoveTo (left + (right - left) * t0, y);
				g.LineTo (left + (right - left) * t1 + 0.3, y);
				g.Stroke ();
			}
		};

	/// <summary>Draws a line with round, square or flat ends.</summary>
	private static void Stroke (Cairo.Context g, EndShape ends, double y, double left, double right)
	{
		g.LineWidth = 3;
		g.LineCap = ends == EndShape.Square ? LineCap.Square : LineCap.Round;
		g.LineJoin = LineJoin.Round;
		g.SetSourceColor (TextColour ());

		g.MoveTo (left, y);
		g.LineTo (right, y);
		g.Stroke ();
	}

	/// <summary>The colour of the text of the theme, so the pictures work in the dark theme too.</summary>
	private static Color TextColour ()
	{
		Gdk.RGBA colour = fallback_colour;

		try {
			PintaCore.Chrome.MainWindow.GetStyleContext ().GetColor (out colour);
		} catch {
			// No window yet: the fallback is dark grey, which is readable on both.
		}

		return new Color (colour.Red, colour.Green, colour.Blue, colour.Alpha);
	}
}
