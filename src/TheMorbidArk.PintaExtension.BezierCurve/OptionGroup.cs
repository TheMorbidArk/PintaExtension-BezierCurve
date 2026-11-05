//
// OptionGroup.cs
//
// A row of buttons of which only one is pressed at a time.
//
// A combo box would do the same job, but it opens a popover of its own. That popover
// takes the grab of the style panel away and does not give it back, so the panel can
// not be closed by clicking outside any more (GNOME/gtk#4529). Toggle buttons do not
// open a window of their own, so the panel keeps working.
//
using System;
using System.Collections.Generic;
using System.Linq;

namespace TheMorbidArk.PintaExtension.BezierCurve;

/// <summary>A row of buttons of which only one is pressed at a time.</summary>
internal sealed class OptionGroup
{
	private readonly List<Gtk.ToggleButton> buttons = new ();
	private readonly Action<int> on_change;

	/// <summary>True while the group changes the buttons itself.</summary>
	private bool updating;

	/// <param name="labels">The name of each button.</param>
	/// <param name="previews">An optional picture for each button, drawn at the given size.</param>
	/// <param name="active">The button that is pressed to begin with.</param>
	/// <param name="on_change">Called when the user presses another button.</param>
	internal OptionGroup (
		string[] labels,
		Action<Cairo.Context, int, int>?[]? previews,
		int active,
		Action<int> on_change)
	{
		this.on_change = on_change;

		for (int i = 0; i < labels.Length; ++i) {
			int index = i;

			Action<Cairo.Context, int, int>? preview = previews is null ? null : previews[i];

			Gtk.ToggleButton button = Gtk.ToggleButton.New ();
			button.SetChild (BuildContent (labels[i], preview));

			if (preview is not null) {
				// Room for a picture above the name.
				button.SetSizeRequest (-1, 44);
			}

			button.Active = i == active;

			// The state of the widget is only final after the widget has dealt with
			// the click, which is why "toggled" is used and not "clicked".
			button.OnToggled += (_, _) => Toggled (button, index);

			buttons.Add (button);
		}
	}

	/// <summary>How many buttons the row has.</summary>
	internal int Count => buttons.Count;

	/// <summary>How many of the buttons are pressed; internal for the tests.</summary>
	internal int PressedCount => buttons.Count (button => button.Active);

	/// <summary>The row of buttons. It does not stretch, the buttons keep their size.</summary>
	internal Gtk.Box Build ()
	{
		Gtk.Box row = Gtk.Box.New (Gtk.Orientation.Horizontal, 6);
		row.SetHalign (Gtk.Align.Start);

		foreach (Gtk.ToggleButton button in buttons)
			row.Append (button);

		return row;
	}

	/// <summary>Presses the button with the given index, without telling anyone.</summary>
	internal void Select (int index)
	{
		updating = true;

		for (int i = 0; i < buttons.Count; ++i)
			buttons[i].Active = i == index;

		updating = false;
	}

	/// <summary>Presses the button with the given index, the way the user would.</summary>
	internal void Press (int index)
	{
		if (index >= 0 && index < buttons.Count)
			buttons[index].Active = true;
	}

	/// <summary>The picture above the name, or just the name.</summary>
	private static Gtk.Widget BuildContent (string label, Action<Cairo.Context, int, int>? preview)
	{
		Gtk.Box content = Gtk.Box.New (Gtk.Orientation.Vertical, 2);
		content.SetHalign (Gtk.Align.Center);
		content.SetValign (Gtk.Align.Center);

		if (preview is not null) {
			Gtk.DrawingArea picture = Gtk.DrawingArea.New ();
			picture.SetContentWidth (34);
			picture.SetContentHeight (14);
			picture.SetHalign (Gtk.Align.Center);
			picture.SetDrawFunc ((area, context, width, height) => preview (context, width, height));

			content.Append (picture);
		}

		Gtk.Label text = Gtk.Label.New (label);
		text.AddCssClass ("caption");

		content.Append (text);

		return content;
	}

	private void Toggled (Gtk.ToggleButton button, int index)
	{
		if (updating)
			return;

		if (!button.Active) {
			// The button that was pressed before stays pressed: one of them always is.
			button.Active = true;
			return;
		}

		Select (index);

		on_change (index);
	}
}
