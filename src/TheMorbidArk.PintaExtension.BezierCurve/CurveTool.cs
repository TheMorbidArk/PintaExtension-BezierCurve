//
// CurveTool.cs
//
// A faithful port of KolourPaint's curve tool (tools/polygonal/kpToolCurve.cpp and
// the parts of tools/polygonal/kpToolPolygonalBase.cpp that it relies on).
//
// Interaction:
//   1. drag out the start and end point of a line (the button used here also picks
//      the color: primary for left, secondary for right),
//   2. drag again to place the first control point,
//   3. drag once more to place the last control point - the shape is then finished.
//   Clicking the other mouse button at any time finishes the shape early, which is
//   how a straight line or a single-control-point curve is committed.
//
// Hold Shift to snap the initial line to 45 degree steps, or Control for 30 degree
// steps (both together give 30/45/60); this is kpToolPolygonalBase::applyModifiers().
//
// The in-progress shape is previewed on the document's tool layer, so the image
// itself is never touched until the shape is committed (KolourPaint's kpTempImage).
//
using System;
using System.Collections.Generic;
using Cairo;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve;

public sealed class CurveTool : BaseTool
{
	private const string BRUSH_WIDTH_SETTING = "PintaExtension.BezierCurve.BrushWidth";
	private const string DASH_PATTERN_SETTING = "PintaExtension.BezierCurve.DashPattern";
	private const string TOOL_MODE_SETTING = "PintaExtension.BezierCurve.ToolMode";
	private const string COMMIT_ON_RELEASE_SETTING = "PintaExtension.BezierCurve.CommitOnRelease";
	private const string START_END_SETTING = "PintaExtension.BezierCurve.StartEnd";
	private const string FINISH_END_SETTING = "PintaExtension.BezierCurve.FinishEnd";
	private const string STROKE_JOIN_SETTING = "PintaExtension.BezierCurve.StrokeJoin";
	private const string GRADIENT_SETTING = "PintaExtension.BezierCurve.Gradient";
	private const string TAPER_START_SETTING = "PintaExtension.BezierCurve.TaperStart";
	private const string TAPER_END_SETTING = "PintaExtension.BezierCurve.TaperEnd";
	private const string FILL_SETTING = "PintaExtension.BezierCurve.Fill";
	private const string CLOSED_SETTING = "PintaExtension.BezierCurve.Closed";
	private const string SMOOTH_CLOSE_SETTING = "PintaExtension.BezierCurve.SmoothClose";
	private const string SYMMETRY_SETTING = "PintaExtension.BezierCurve.Symmetry";
	private const string PIXEL_ALIGNMENT_SETTING = "PintaExtension.BezierCurve.PixelAlignment";
	private const string DEFAULT_DASH_PATTERN = "-";
	private const int MINIMUM_BRUSH_WIDTH = 1;
	private const int MAXIMUM_BRUSH_WIDTH = 100000;

	// How far a point may be from a construction point to snap to it, in canvas pixels.
	private const double SNAP_TOLERANCE = 5d;


	private readonly IPaletteService palette;
	private readonly IWorkspaceService workspace;
	private readonly ICanvasGridService? canvas_grid;

	// The shape state machine (see CurveEditor); this class only forwards to it.
	private readonly CurveEditor editor = new ();

	// How a single control point shapes the curve.
	private ToolMode tool_mode = ToolMode.KolourPaint;

	// Whether the curve is committed as soon as the last control point is placed.
	private bool commit_on_release;

	// The appearance of the curve.
	private EndShape start_end = EndShape.Round;
	private EndShape finish_end = EndShape.Round;
	private LineJoin stroke_join = LineJoin.Round;
	private bool gradient_enabled;
	private int taper_start_width;
	private int taper_end_width;
	private bool fill_enabled;

	// The shape of the curve.
	private bool closed_enabled;
	private bool smooth_close_enabled;
	private SymmetryMode symmetry_mode = SymmetryMode.Off;

	// Whether every point is put on a whole pixel.
	private bool pixel_alignment;

	// Which button is currently held down (-1 when none).
	private MouseButton drag_button = MouseButton.None;

	// True while the single control point is grabbed: a click on it finishes the curve,
	// the way it does in MS Paint.
	private bool finish_on_release;

	private ImageSurface? undo_surface;

	private RectangleI last_canvas_dirty = RectangleI.Zero;
	private bool has_canvas_dirty;

	private readonly List<RectangleI> last_handle_dirty = [];

	private readonly CurveHandle endpoint_start;
	private readonly CurveHandle endpoint_end;
	private readonly CurveHandle control_point_first;
	private readonly CurveHandle control_point_last;
	private readonly CurveHandle leash_start;
	private readonly CurveHandle leash_end;
	private readonly CurveHandle[] all_handles;

	// --- The shape that was just drawn and is still an object -----------------------
	//
	// After the last control point is placed the curve does not drop onto the layer yet:
	// it stays an object with a selection frame around it, so that it can be moved. The
	// object ends as soon as something outside the frame is clicked, which is when the
	// curve becomes pixels.

	private ShapeObject? shape_object;

	private readonly ShapeFrame frame;
	private readonly ShapeFrame rotate_handle;
	private readonly ShapeFrame[] resize_handles;
	private readonly IToolHandle[] frame_handles;

	/// <summary>Which part of the shape the current drag is holding, or null.</summary>
	private ShapeDrag? shape_drag;

	/// <summary>Where the shape was when the drag started, in canvas coordinates.</summary>
	private PointD shape_drag_origin;

	/// <summary>
	/// The transform and the bounds the shape had when the drag started. Every move of a
	/// drag is worked out from these, so that the shape never drifts.
	/// </summary>
	private ShapeState shape_drag_state;

	/// <summary>The extent of the curve in its own frame when the drag started.</summary>
	private (double MinX, double MinY, double MaxX, double MaxY) shape_drag_extent;

	/// <summary>Where the grabbed handle was when the drag started.</summary>
	private PointD shape_drag_handle_origin;

	/// <summary>The angle from the middle of the shape to the cursor when a turn began.</summary>
	private double shape_drag_angle;

	/// <summary>The handle that is being dragged, when the drag resizes the shape.</summary>
	private FrameHandle? shape_drag_handle;

	/// <summary>
	/// Which part of a shape a drag is acting on.
	/// </summary>
	private enum ShapeDrag
	{
		Move,
		Rotate,
		Resize,
	}

	/// <summary>Starts dragging the object, remembering where it began.</summary>
	private void BeginShapeDrag (ShapeDrag kind, PointD origin)
	{
		shape_drag = kind;
		shape_drag_origin = origin;
		shape_drag_handle = null;
		drag_button = MouseButton.Left;

		if (shape_object is null)
			return;

		shape_drag_state = shape_object.Save ();
		shape_drag_extent = shape_object.LocalExtent;

		shape_drag_angle = Math.Atan2 (
			origin.Y - shape_object.Center.Y,
			origin.X - shape_object.Center.X);
	}

	/// <summary>
	/// Scales the object from the handle that is being dragged. The handle opposite the
	/// dragged one stays where it is, which is what the eight handles mean.
	/// </summary>
	private void ResizeShapeObject (Document document, ToolMouseEventArgs e)
	{
		if (shape_object is null || shape_drag_handle is not FrameHandle kind)
			return;

		(double minX, double minY, double maxX, double maxY) = shape_drag_extent;

		// The drag is measured on the canvas, but the shape is scaled in its own frame, so
		// that a turned shape stretches along its own axes instead of the screen's.
		double drag_x = e.PointDouble.X - shape_drag_handle_origin.X;
		double drag_y = e.PointDouble.Y - shape_drag_handle_origin.Y;

		double sin = Math.Sin (-shape_object.Angle);
		double cos = Math.Cos (-shape_object.Angle);

		double local_dx = drag_x * cos - drag_y * sin;
		double local_dy = drag_x * sin + drag_y * cos;

		// The corner across from the dragged handle is the one that stays put.
		double anchor_x = MovesHorizontally (kind) ? (DragsFromLeft (kind) ? maxX : minX) : minX;
		double anchor_y = MovesVertically (kind) ? (DragsFromTop (kind) ? maxY : minY) : minY;

		double corner_x = DragsFromLeft (kind) ? minX : maxX;
		double corner_y = DragsFromTop (kind) ? minY : maxY;

		double original_dx = corner_x - anchor_x;
		double original_dy = corner_y - anchor_y;

		// The dragged corner follows the mouse, whatever side of the anchor it is on:
		// dragging the left edge to the right shrinks the shape, dragging the right edge
		// to the right grows it.
		double sx = MovesHorizontally (kind) && Math.Abs (original_dx) > 1e-9
			? (corner_x + local_dx - anchor_x) / original_dx
			: 1d;

		double sy = MovesVertically (kind) && Math.Abs (original_dy) > 1e-9
			? (corner_y + local_dy - anchor_y) / original_dy
			: 1d;

		// Shift keeps the proportions of a corner handle.
		if (e.IsShiftPressed && MovesHorizontally (kind) && MovesVertically (kind)) {
			double scale = Math.Max (sx, sy);
			sx = scale;
			sy = scale;
		}

		shape_object.Restore (shape_drag_state);
		shape_object.ScaleLocal (sx, sy, new PointD (anchor_x, anchor_y));

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Turns the object around its middle, following the cursor. Holding Shift snaps the
	/// turn to 15 degree steps.
	/// </summary>
	private void RotateShapeObject (Document document, ToolMouseEventArgs e)
	{
		if (shape_object is null)
			return;

		PointD center = shape_object.Center;

		double now = Math.Atan2 (e.PointDouble.Y - center.Y, e.PointDouble.X - center.X);
		double turn = now - shape_drag_angle;

		// Angles wrap, so a turn that crosses the -180/180 line must not jump a whole
		// revolution.
		if (turn > Math.PI)
			turn -= 2 * Math.PI;
		else if (turn < -Math.PI)
			turn += 2 * Math.PI;

		shape_object.Restore (shape_drag_state);

		double total = shape_object.Angle + turn;

		if (e.IsShiftPressed) {
			const double STEP = Math.PI / 12d;   // 15 degrees
			total = Math.Round (total / STEP) * STEP;
		}

		shape_object.RotateBy (total - shape_object.Angle);

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	private static bool MovesHorizontally (FrameHandle handle)
		=> handle is FrameHandle.TopLeft or FrameHandle.Left or FrameHandle.BottomLeft
			or FrameHandle.TopRight or FrameHandle.Right or FrameHandle.BottomRight;

	private static bool MovesVertically (FrameHandle handle)
		=> handle is FrameHandle.TopLeft or FrameHandle.Top or FrameHandle.TopRight
			or FrameHandle.BottomLeft or FrameHandle.Bottom or FrameHandle.BottomRight;

	/// <summary>Mirrors the object that is being edited, in its own middle.</summary>
	internal void FlipShapeObject (bool horizontal)
	{
		if (shape_object is null || !workspace.HasOpenDocuments)
			return;

		if (horizontal)
			shape_object.FlipHorizontal ();
		else
			shape_object.FlipVertical ();

		UpdatePreview (workspace.ActiveDocument);
		UpdateStatusBar ();
	}

	/// <summary>Enables the buttons that only work while an object is being edited.</summary>
	private void UpdateObjectButtons ()
	{
		bool active = shape_object is not null;

		if (flip_horizontal_button is not null)
			flip_horizontal_button.Sensitive = active;

		if (flip_vertical_button is not null)
			flip_vertical_button.Sensitive = active;
	}

	/// <summary>True when the handle drags the left or top side of the frame.</summary>
	private static bool DragsFromLeft (FrameHandle handle)
		=> handle is FrameHandle.TopLeft or FrameHandle.Left or FrameHandle.BottomLeft;

	private static bool DragsFromTop (FrameHandle handle)
		=> handle is FrameHandle.TopLeft or FrameHandle.Top or FrameHandle.TopRight;

	/// <summary>
	/// True when there is work on the canvas that the history does not know about yet: a
	/// curve that is being drawn, or an object that has not been dropped onto the layer.
	/// </summary>
	private bool HasUncommittedWork
		=> shape_object is not null || editor.HasShape;

	/// <summary>True while a drawn curve is still an object with a frame around it.</summary>
	internal bool HasShapeObject => shape_object is not null;

	/// <summary>The object that is being edited, or null; internal for the tests.</summary>
	internal ShapeObject? Shape => shape_object;

	/// <summary>The curve of the object, or null; internal for the tests.</summary>
	internal BezierCurve? ShapeCurve => shape_object?.Curve;

	public CurveTool (IServiceProvider services) : base (services)
	{
		palette = services.GetService<IPaletteService> ();
		workspace = services.GetService<IWorkspaceService> ();
		canvas_grid = services.GetService<ICanvasGridService> ();

		tool_mode = (ToolMode) Settings.GetSetting (TOOL_MODE_SETTING, (int) ToolMode.KolourPaint);

		commit_on_release = Settings.GetSetting (COMMIT_ON_RELEASE_SETTING, false);

		start_end = (EndShape) Settings.GetSetting (START_END_SETTING, (int) EndShape.Round);
		finish_end = (EndShape) Settings.GetSetting (FINISH_END_SETTING, (int) EndShape.Round);
		stroke_join = (LineJoin) Settings.GetSetting (STROKE_JOIN_SETTING, (int) LineJoin.Round);
		gradient_enabled = Settings.GetSetting (GRADIENT_SETTING, false);
		taper_start_width = Settings.GetSetting (TAPER_START_SETTING, 0);
		taper_end_width = Settings.GetSetting (TAPER_END_SETTING, 0);
		fill_enabled = Settings.GetSetting (FILL_SETTING, false);
		closed_enabled = Settings.GetSetting (CLOSED_SETTING, false);
		smooth_close_enabled = Settings.GetSetting (SMOOTH_CLOSE_SETTING, false);
		symmetry_mode = (SymmetryMode) Settings.GetSetting (SYMMETRY_SETTING, (int) SymmetryMode.Off);
		pixel_alignment = Settings.GetSetting (PIXEL_ALIGNMENT_SETTING, false);

		editor.Closed = closed_enabled;
		editor.SmoothClose = smooth_close_enabled;
		editor.Symmetry = symmetry_mode;

		endpoint_start = new (workspace, CurveHandleKind.Endpoint);
		endpoint_end = new (workspace, CurveHandleKind.Endpoint);
		control_point_first = new (workspace, CurveHandleKind.ControlPoint);
		control_point_last = new (workspace, CurveHandleKind.ControlPoint);
		leash_start = new (workspace, CurveHandleKind.Leash);
		leash_end = new (workspace, CurveHandleKind.Leash);

		all_handles = [
			leash_start,
			leash_end,
			endpoint_start,
			endpoint_end,
			control_point_first,
			control_point_last,
		];

		frame = ShapeFrame.CreateFrame (workspace);
		rotate_handle = ShapeFrame.CreateRotateHandle (workspace, grab_cursor);

		resize_handles = [
			ShapeFrame.CreateHandle (workspace, FrameHandle.TopLeft),
			ShapeFrame.CreateHandle (workspace, FrameHandle.Top),
			ShapeFrame.CreateHandle (workspace, FrameHandle.TopRight),
			ShapeFrame.CreateHandle (workspace, FrameHandle.Right),
			ShapeFrame.CreateHandle (workspace, FrameHandle.BottomRight),
			ShapeFrame.CreateHandle (workspace, FrameHandle.Bottom),
			ShapeFrame.CreateHandle (workspace, FrameHandle.BottomLeft),
			ShapeFrame.CreateHandle (workspace, FrameHandle.Left),
		];

		frame_handles = [frame, rotate_handle, .. resize_handles];
	}

	public override string Name => Strings.Get ("ToolName");
	public override string Icon => CurveAddin.ToolIcon;
	public override string StatusBarText => Strings.Get ("ToolDescription");
	public override Gdk.Key ShortcutKey => new (Gdk.Constants.KEY_V);
	public override int Priority => 38; // right after the built-in Line/Curve tool (37)
	public override Gdk.Cursor DefaultCursor => default_cursor ??= Gdk.Cursor.NewFromTexture (Resources.GetIcon ("Cursor.Line.png"), 9, 18, null);

	private Gdk.Cursor? default_cursor;
	private static readonly Gdk.Cursor grab_cursor = GdkExtensions.CursorFromName (Pinta.Resources.StandardCursors.Grab);

	// The four arrows that tell the user a press would move the shape.
	private static readonly Gdk.Cursor move_cursor = GdkExtensions.CursorFromName (Pinta.Resources.StandardCursors.Move);
	public override IEnumerable<IToolHandle> Handles => [.. all_handles, .. frame_handles];

	protected override bool ShowAntialiasingButton => true;
	protected override bool ShowAlphaBlendingButton => true;

	#region Tool bar

	private Gtk.SpinButton? brush_width;
	private Gtk.Label? brush_width_label;

	/// <summary>Internal so that the tests can drive it without the tool bar.</summary>
	internal int BrushWidth {
		get => brush_width?.GetValueAsInt () ?? Settings.GetSetting (BRUSH_WIDTH_SETTING, DEFAULT_BRUSH_WIDTH);
		set { if (brush_width is not null) brush_width.Value = value; }
	}

	/// <summary>How many points the curve in progress has; internal for the tests.</summary>
	internal int PointCount => editor.Count;

	/// <summary>Opens the style panel; internal so that the tests can drive it.</summary>
	internal void OpenStylePanel ()
		=> style_popover?.Popup ();

	/// <summary>The switch of the closed curve in the panel; internal for the tests.</summary>
	internal Gtk.Switch? ClosedSwitch => closed_switch;

	/// <summary>The switch of the fill in the panel; internal for the tests.</summary>
	internal Gtk.Switch? FillSwitch => fill_switch;

	/// <summary>The group of flip buttons; internal for the tests.</summary>
	internal Gtk.Box? FlipBox => flip_box;

	/// <summary>The commit box of the tool bar; internal for the tests.</summary>
	internal ToolBarComboBox? CommitBox => commit_box;

	/// <summary>The buttons for the start of the stroke in the panel; internal for the tests.</summary>
	internal OptionGroup? StartEndGroup => start_end_group;

	/// <summary>The buttons for the end of the stroke in the panel; internal for the tests.</summary>
	internal OptionGroup? FinishEndGroup => finish_end_group;
	/// <summary>The symmetry in the panel; internal for the tests.</summary>
	internal OptionGroup? SymmetryGroup => symmetry_group;

	/// <summary>The width the tapered stroke starts with; internal for the tests.</summary>
	internal int TaperStartWidth {
		get => taper_start_width;
		set {
			taper_start_width = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
		}
	}

	/// <summary>The spin button for the width in the panel; internal for the tests.</summary>
	internal Gtk.SpinButton? TaperStartSpin => taper_start_spin;

	/// <summary>The shape of the start of the stroke; internal for the tests.</summary>
	internal EndShape StartEnd {
		get => start_end;
		set {
			start_end = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
		}
	}

	/// <summary>The shape of the end of the stroke; internal for the tests.</summary>
	internal EndShape FinishEnd {
		get => finish_end;
		set {
			finish_end = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
		}
	}

	/// <summary>The points of the curve in progress; internal for the tests.</summary>
	internal IReadOnlyList<PointD> Points => editor.Points;

	/// <summary>Whether the curve is filled; internal so that the tests can drive it.</summary>
	internal bool FillEnabled {
		get => fill_enabled;
		set {
			fill_enabled = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
			UpdateStatusBar ();
		}
	}

	/// <summary>Whether the curve is closed; internal so that the tests can drive it.</summary>
	internal bool ClosedEnabled {
		get => closed_enabled;
		set {
			closed_enabled = value;
			editor.Closed = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
			UpdateStatusBar ();
		}
	}

	/// <summary>How the control points are mirrored; internal so that the tests can drive it.</summary>
	internal SymmetryMode Symmetry {
		get => symmetry_mode;
		set {
			symmetry_mode = value;
			editor.Symmetry = value;
			UpdateStyleWidgets ();
			RepaintPreview ();
			UpdateStatusBar ();
		}
	}

	/// <summary>
	/// Cairo dash pattern, in Pinta's dash/space notation ("-" is solid). KolourPaint has
	/// no dashes; this is an optional extra that defaults to a solid line.
	/// </summary>
	internal string DashPattern {
		get => dash_pattern_box?.ComboBox.GetEntry ().GetText () ?? DEFAULT_DASH_PATTERN;
		set => dash_pattern_box?.ComboBox.GetEntry ().SetText (value);
	}

	private ToolBarComboBox? dash_pattern_box;
	private Gtk.Label? dash_label;

	private ToolBarComboBox? control_point_box;
	private Gtk.Label? control_point_label;

	private ToolBarComboBox? commit_box;
	private Gtk.Label? commit_label;

	private Gtk.Button? edit_last_button;

	private Gtk.Box? flip_box;
	private Gtk.Button? flip_horizontal_button;
	private Gtk.Button? flip_vertical_button;

	private Gtk.Popover? style_popover;
	private Gtk.Button? style_button;
	private OptionGroup? start_end_group;
	private OptionGroup? finish_end_group;
	private OptionGroup? join_group;
	private OptionGroup? gradient_group;
	private OptionGroup? symmetry_group;
	private Gtk.SpinButton? taper_start_spin;
	private Gtk.SpinButton? taper_end_spin;
	private Gtk.Switch? closed_switch;
	private Gtk.Switch? smooth_close_switch;
	private Gtk.Switch? fill_switch;
	private Gtk.Switch? pixel_alignment_switch;

	// The entries of the combo boxes of the panel, in the order they are shown. The
	// values of the Cairo enums are not in the order the panel uses, so they are
	// mapped explicitly.
	private static readonly EndShape[] END_SHAPES = [EndShape.Round, EndShape.Pointed, EndShape.Square];
	private static readonly LineJoin[] JOINS = [LineJoin.Round, LineJoin.Bevel, LineJoin.Miter];
	private static readonly SymmetryMode[] SYMMETRIES = [SymmetryMode.Off, SymmetryMode.Mirror, SymmetryMode.Center];

	/// <summary>How a single control point shapes the curve.</summary>
	internal ToolMode ToolMode {
		get => tool_mode;
		set {
			if (tool_mode == value)
				return;

			// Leaving a mode drops whatever the mode was showing.
			if (workspace.HasOpenDocuments)
				CommitShapeObject (workspace.ActiveDocument);

			tool_mode = value;

			if (control_point_box is not null)
				control_point_box.ComboBox.Active = (int) value;

			UpdateStyleWidgets ();
			UpdateModeVisibility ();
			RepaintPreview ();
			UpdateStatusBar ();
		}
	}

	/// <summary>
	/// Shows the options that belong to the current mode and hides the ones that do not:
	/// a curve that is paint has nothing to flip, and an object commits itself when the
	/// last control point is placed.
	/// </summary>
	private void UpdateModeVisibility ()
	{
		bool object_mode = tool_mode == ToolMode.MSPaint;

		if (commit_box is not null)
			commit_box.Visible = !object_mode;

		if (commit_label is not null)
			commit_label.Visible = !object_mode;

		if (flip_box is not null)
			flip_box.Visible = object_mode;
	}

	/// <summary>
	/// Whether the curve is committed as soon as the second control point is placed (the
	/// way MS Paint does it) or stays adjustable until the user finishes it.
	/// </summary>
	internal bool CommitOnRelease {
		get => commit_on_release;
		set {
			commit_on_release = value;

			if (commit_box is not null)
				commit_box.ComboBox.Active = value ? 1 : 0;

			UpdateStatusBar ();
		}
	}

	protected override void OnBuildToolBar (Gtk.Box tb)
	{
		base.OnBuildToolBar (tb);

		// --- The brush width: Pinta's own spin button.

		brush_width_label ??= Gtk.Label.New (string.Format (" {0}: ", Strings.Get ("BrushWidth")));

		if (brush_width is null) {
			brush_width = GtkExtensions.CreateToolBarSpinButton (
				MINIMUM_BRUSH_WIDTH,
				MAXIMUM_BRUSH_WIDTH,
				1,
				Settings.GetSetting (BRUSH_WIDTH_SETTING, DEFAULT_BRUSH_WIDTH));

			brush_width.OnValueChanged += (_, _) => {
				SetCursor (DefaultCursor);
				RepaintPreview ();
			};
		}

		tb.Append (brush_width_label);
		tb.Append (brush_width);

		tb.Append (GtkExtensions.CreateToolBarSeparator ());

		// --- The dash pattern.

		dash_label ??= Gtk.Label.New (string.Format (" {0}: ", Strings.Get ("Dash")));

		if (dash_pattern_box is null) {
			// Presets follow Pinta's own dash box; a bare "--" has no visible gaps once the
			// round caps are added, so every dash pattern here starts with a space.
			dash_pattern_box = new ToolBarComboBox (60, 0, true,
				"-", " -", " --", " ---", "  -", "   -", " - --", " - - --------");
			dash_pattern_box.TooltipText = Strings.Get ("DashTooltip");

			dash_pattern_box.ComboBox.GetEntry ().SetText (
				Settings.GetSetting (DASH_PATTERN_SETTING, DEFAULT_DASH_PATTERN));

			dash_pattern_box.ComboBox.OnChanged += (_, _) => {
				RepaintPreview ();
				UpdateStatusBar ();
			};
		}

		tb.Append (dash_label);
		tb.Append (dash_pattern_box);

		tb.Append (GtkExtensions.CreateToolBarSeparator ());

		// --- What happens to the curve once it is drawn.

		control_point_label ??= Gtk.Label.New (string.Format (" {0}: ", Strings.Get ("ToolMode")));

		if (control_point_box is null) {
			control_point_box = new ToolBarComboBox (110, 0, false,
				Strings.Get ("ModeKolourPaint"), Strings.Get ("ModePaint"));
			control_point_box.TooltipText = Strings.Get ("ToolModeTooltip");

			control_point_box.ComboBox.Active = (int) tool_mode;

			control_point_box.ComboBox.OnChanged += (_, _) => ToolMode = (ToolMode) control_point_box.ComboBox.Active;
		}

		tb.Append (control_point_label);
		tb.Append (control_point_box);

		tb.Append (GtkExtensions.CreateToolBarSeparator ());

		// --- When the curve is committed.

		bool commit_applies = tool_mode != ToolMode.MSPaint;

		commit_label ??= Gtk.Label.New (string.Format (" {0}: ", Strings.Get ("Commit")));

		if (commit_box is null) {
			commit_box = new ToolBarComboBox (110, 0, false,
				Strings.Get ("CommitKeep"), Strings.Get ("CommitNow"));
			commit_box.TooltipText = Strings.Get ("CommitTooltip");

			commit_box.ComboBox.Active = commit_on_release ? 1 : 0;

			commit_box.ComboBox.OnChanged += (_, _) => {
				commit_on_release = commit_box.ComboBox.Active == 1;
				UpdateStatusBar ();
			};
		}

		commit_label.Visible = commit_applies;
		commit_box.Visible = commit_applies;

		tb.Append (commit_label);
		tb.Append (commit_box);

		tb.Append (GtkExtensions.CreateToolBarSeparator ());

		// --- The stroke, the shape and the symmetry options live in a panel, so that
		//     the tool bar does not have to hold all of them.

		if (style_button is null) {
			style_button = new Gtk.Button ();
			style_button.SetChild (Gtk.Image.NewFromIconName ("preferences-other-symbolic"));
			style_button.TooltipText = Strings.Get ("StyleTooltip");

			style_popover = BuildStylePopover ();
			style_popover.SetParent (style_button);

			style_button.OnClicked += (_, _) => style_popover?.Popup ();
		}

		tb.Append (style_button);

		// --- Flip the object that is still being edited. This whole group only means
		//     something in the MS Paint mode, where a finished curve is an object, so it
		//     is hidden rather than greyed out in the other mode.

		if (flip_box is null) {
			flip_box = Gtk.Box.New (Gtk.Orientation.Horizontal, 0);
			flip_box.Append (GtkExtensions.CreateToolBarSeparator ());

			flip_horizontal_button = new Gtk.Button ();
			flip_horizontal_button.SetChild (Gtk.Image.NewFromIconName ("image-flip-horizontal-symbolic"));
			flip_horizontal_button.TooltipText = Strings.Get ("FlipHorizontalTooltip");
			flip_horizontal_button.OnClicked += (_, _) => FlipShapeObject (horizontal: true);
			flip_box.Append (flip_horizontal_button);

			flip_vertical_button = new Gtk.Button ();
			flip_vertical_button.SetChild (Gtk.Image.NewFromIconName ("image-flip-vertical-symbolic"));
			flip_vertical_button.TooltipText = Strings.Get ("FlipVerticalTooltip");
			flip_vertical_button.OnClicked += (_, _) => FlipShapeObject (horizontal: false);
			flip_box.Append (flip_vertical_button);

			flip_box.Append (GtkExtensions.CreateToolBarSeparator ());
		}

		tb.Append (flip_box);

		// --- Pick the curve that was committed last up again.

		if (edit_last_button is null) {
			edit_last_button = new Gtk.Button ();
			edit_last_button.SetChild (Gtk.Image.NewFromIconName ("document-edit-symbolic"));
			edit_last_button.TooltipText = Strings.Get ("EditLastCurveTooltip");
			edit_last_button.OnClicked += (_, _) => EditLastCurve ();
		}

		edit_last_button.Sensitive = CanEditLastCurve;

		tb.Append (edit_last_button);

		UpdateModeVisibility ();
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (brush_width is not null)
			settings.PutSetting (BRUSH_WIDTH_SETTING, brush_width.GetValueAsInt ());

		if (dash_pattern_box is not null)
			settings.PutSetting (DASH_PATTERN_SETTING, DashPattern);

		settings.PutSetting (TOOL_MODE_SETTING, (int) tool_mode);
		settings.PutSetting (COMMIT_ON_RELEASE_SETTING, commit_on_release);
		settings.PutSetting (START_END_SETTING, (int) start_end);
		settings.PutSetting (FINISH_END_SETTING, (int) finish_end);
		settings.PutSetting (STROKE_JOIN_SETTING, (int) stroke_join);
		settings.PutSetting (GRADIENT_SETTING, gradient_enabled);
		settings.PutSetting (TAPER_START_SETTING, taper_start_width);
		settings.PutSetting (TAPER_END_SETTING, taper_end_width);
		settings.PutSetting (FILL_SETTING, fill_enabled);
		settings.PutSetting (CLOSED_SETTING, closed_enabled);
		settings.PutSetting (SMOOTH_CLOSE_SETTING, smooth_close_enabled);
		settings.PutSetting (SYMMETRY_SETTING, (int) symmetry_mode);
		settings.PutSetting (PIXEL_ALIGNMENT_SETTING, pixel_alignment);
	}

	#region The style panel

	/// <summary>
	/// The panel with the stroke, the shape and the symmetry options.
	/// </summary>
	private Gtk.Popover BuildStylePopover ()
	{
		Gtk.Popover popover = Gtk.Popover.New ();
		popover.SetHasArrow (true);

		// Two columns: the name of the option and its control. A grid keeps them in
		// line with each other, whatever the length of the names is.
		Gtk.Grid grid = Gtk.Grid.New ();
		grid.ColumnSpacing = 16;
		grid.RowSpacing = 6;
		grid.SetMarginTop (12);
		grid.SetMarginBottom (12);
		grid.SetMarginStart (12);
		grid.SetMarginEnd (12);

		int row = 0;

		// --- The stroke ----------------------------------------------------------

		grid.Attach (SectionHeader (Strings.Get ("StyleStroke")), 0, row++, 2, 1);

		start_end_group = BuildEndGroup (
			Math.Max (0, Array.IndexOf (END_SHAPES, start_end)),
			index => {
				start_end = END_SHAPES[index];
				RepaintPreview ();
			});

		grid.Attach (RowLabel (Strings.Get ("StartEnd")), 0, row, 1, 1);
		grid.Attach (start_end_group.Build (), 1, row++, 1, 1);

		finish_end_group = BuildEndGroup (
			Math.Max (0, Array.IndexOf (END_SHAPES, finish_end)),
			index => {
				finish_end = END_SHAPES[index];
				RepaintPreview ();
			});

		grid.Attach (RowLabel (Strings.Get ("FinishEnd")), 0, row, 1, 1);
		grid.Attach (finish_end_group.Build (), 1, row++, 1, 1);
		join_group = new OptionGroup (
			[Strings.Get ("JoinRound"), Strings.Get ("JoinBevel"), Strings.Get ("JoinMiter")],
			[StrokePreviews.Join (LineJoin.Round), StrokePreviews.Join (LineJoin.Bevel), StrokePreviews.Join (LineJoin.Miter)],
			Math.Max (0, Array.IndexOf (JOINS, stroke_join)),
			index => {
				stroke_join = JOINS[index];
				RepaintPreview ();
			});

		grid.Attach (RowLabel (Strings.Get ("Join")), 0, row, 1, 1);
		grid.Attach (join_group.Build (), 1, row++, 1, 1);

		gradient_group = new OptionGroup (
			[Strings.Get ("GradientOff"), Strings.Get ("GradientOn")],
			null,
			gradient_enabled ? 1 : 0,
			index => {
				gradient_enabled = index == 1;
				RepaintPreview ();
			});

		grid.Attach (RowLabel (Strings.Get ("Gradient")), 0, row, 1, 1);
		grid.Attach (gradient_group.Build (), 1, row++, 1, 1);

		taper_start_spin = Gtk.SpinButton.NewWithRange (0, MAXIMUM_BRUSH_WIDTH, 1);
		taper_start_spin.Value = taper_start_width;
		taper_start_spin.SetWidthChars (3);
		taper_start_spin.SetHalign (Gtk.Align.Start);
		taper_start_spin.OnValueChanged += (_, _) => {
			taper_start_width = taper_start_spin.GetValueAsInt ();
			RepaintPreview ();
		};

		grid.Attach (RowLabel (Strings.Get ("TaperStart")), 0, row, 1, 1);
		grid.Attach (taper_start_spin, 1, row++, 1, 1);

		taper_end_spin = Gtk.SpinButton.NewWithRange (0, MAXIMUM_BRUSH_WIDTH, 1);
		taper_end_spin.Value = taper_end_width;
		taper_end_spin.SetWidthChars (3);
		taper_end_spin.SetHalign (Gtk.Align.Start);
		taper_end_spin.OnValueChanged += (_, _) => {
			taper_end_width = taper_end_spin.GetValueAsInt ();
			RepaintPreview ();
		};

		grid.Attach (RowLabel (Strings.Get ("TaperEnd")), 0, row, 1, 1);
		grid.Attach (taper_end_spin, 1, row++, 1, 1);

		// --- The shape -----------------------------------------------------------

		grid.Attach (SectionHeader (Strings.Get ("StyleShape")), 0, row++, 2, 1);

		closed_switch = ToggleSwitch (closed_enabled, value => {
			closed_enabled = value;
			editor.Closed = value;
			RepaintPreview ();
			UpdateStatusBar ();
		});

		grid.Attach (RowLabel (Strings.Get ("Closed")), 0, row, 1, 1);
		grid.Attach (closed_switch, 1, row++, 1, 1);

		smooth_close_switch = ToggleSwitch (smooth_close_enabled, value => {
			smooth_close_enabled = value;
			editor.SmoothClose = value;
			RepaintPreview ();
			UpdateStatusBar ();
		});

		grid.Attach (RowLabel (Strings.Get ("SmoothClose")), 0, row, 1, 1);
		grid.Attach (smooth_close_switch, 1, row++, 1, 1);

		fill_switch = ToggleSwitch (fill_enabled, value => {
			fill_enabled = value;

			// A curve that is not closed cannot be filled, so the fill brings the
			// closing with it.
			if (value && !closed_enabled) {
				closed_enabled = true;
				editor.Closed = true;
			}

			RepaintPreview ();
			UpdateStatusBar ();
		});

		grid.Attach (RowLabel (Strings.Get ("Fill")), 0, row, 1, 1);
		grid.Attach (fill_switch, 1, row++, 1, 1);

		// --- The symmetry --------------------------------------------------------

		grid.Attach (SectionHeader (Strings.Get ("StyleSymmetry")), 0, row++, 2, 1);

		symmetry_group = new OptionGroup (
			[Strings.Get ("SymmetryOff"), Strings.Get ("SymmetryMirror"), Strings.Get ("SymmetryCenter")],
			null,
			Math.Max (0, Array.IndexOf (SYMMETRIES, symmetry_mode)),
			index => {
				symmetry_mode = SYMMETRIES[index];
				editor.Symmetry = symmetry_mode;
				RepaintPreview ();
				UpdateStatusBar ();
			});

		grid.Attach (RowLabel (Strings.Get ("Symmetry")), 0, row, 1, 1);
		grid.Attach (symmetry_group.Build (), 1, row++, 1, 1);

		pixel_alignment_switch = ToggleSwitch (pixel_alignment, value => {
			pixel_alignment = value;
			RepaintPreview ();
		});

		grid.Attach (RowLabel (Strings.Get ("PixelAlignment")), 0, row, 1, 1);
		grid.Attach (pixel_alignment_switch, 1, row++, 1, 1);

		popover.SetChild (grid);

		return popover;
	}

	/// <summary>
	/// Brings the widgets of the style panel in line with the state of the tool, after
	/// it was changed from somewhere else.
	/// </summary>
	private void UpdateStyleWidgets ()
	{
		start_end_group?.Select (Math.Max (0, Array.IndexOf (END_SHAPES, start_end)));
		finish_end_group?.Select (Math.Max (0, Array.IndexOf (END_SHAPES, finish_end)));
		join_group?.Select (Math.Max (0, Array.IndexOf (JOINS, stroke_join)));
		gradient_group?.Select (gradient_enabled ? 1 : 0);

		if (taper_start_spin is not null && taper_start_spin.GetValueAsInt () != taper_start_width)
			taper_start_spin.Value = taper_start_width;

		if (taper_end_spin is not null && taper_end_spin.GetValueAsInt () != taper_end_width)
			taper_end_spin.Value = taper_end_width;

		if (closed_switch is not null && closed_switch.Active != closed_enabled)
			closed_switch.Active = closed_enabled;

		if (smooth_close_switch is not null && smooth_close_switch.Active != smooth_close_enabled)
			smooth_close_switch.Active = smooth_close_enabled;

		if (fill_switch is not null && fill_switch.Active != fill_enabled)
			fill_switch.Active = fill_enabled;

		symmetry_group?.Select (Math.Max (0, Array.IndexOf (SYMMETRIES, symmetry_mode)));

		if (pixel_alignment_switch is not null && pixel_alignment_switch.Active != pixel_alignment)
			pixel_alignment_switch.Active = pixel_alignment;
	}

	private static Gtk.Switch ToggleSwitch (bool active, Action<bool> on_change)
	{
		Gtk.Switch toggle = Gtk.Switch.New ();
		toggle.Active = active;

		// Without this the switch is stretched to the whole width of the column and
		// looks like a long bar instead of a switch.
		toggle.SetHalign (Gtk.Align.Start);
		toggle.SetValign (Gtk.Align.Center);

		// The "activate" signal of a switch only starts its animation - it does not
		// carry the new state. The state is announced by the "active" property, which
		// is what this listens to.
		toggle.OnNotify += (_, _) => on_change (toggle.Active);

		return toggle;
	}

	/// <summary>The three buttons that pick the shape of one end of the stroke.</summary>
	private static OptionGroup BuildEndGroup (int active, Action<int> on_change)
		=> new (
			[Strings.Get ("CapRound"), Strings.Get ("CapPointed"), Strings.Get ("CapSquare")],
			[StrokePreviews.End (EndShape.Round), StrokePreviews.End (EndShape.Pointed), StrokePreviews.End (EndShape.Square)],
			active,
			on_change);

	private static Gtk.Label RowLabel (string text)
		=> Gtk.Label.New (text);

	/// <summary>
	/// The title of a section of the panel: a small caption above the options that
	/// belong to it.
	/// </summary>
	private static Gtk.Widget SectionHeader (string text)
	{
		Gtk.Box header = Gtk.Box.New (Gtk.Orientation.Vertical, 0);
		header.SetMarginTop (6);

		Gtk.Label label = Gtk.Label.New (text);
		label.AddCssClass ("caption-heading");
		label.SetHalign (Gtk.Align.Start);

		header.Append (label);

		return header;
	}

	#endregion

	protected override void OnAntialiasingChanged ()
	{
		base.OnAntialiasingChanged ();
		RepaintPreview ();
	}

	#endregion

	#region Life cycle

	protected override void OnActivated (Document? document)
	{
		palette.PrimaryColorChanged += HandlePaletteChanged;
		palette.SecondaryColorChanged += HandlePaletteChanged;
		workspace.ActiveDocumentChanged += HandleActiveDocumentChanged;

		ResetShape (document);
		UpdateStatusBar ();
		UpdateEditLastButton ();

		base.OnActivated (document);
	}

	protected override void OnDeactivated (Document? document, BaseTool? new_tool)
	{
		palette.PrimaryColorChanged -= HandlePaletteChanged;
		palette.SecondaryColorChanged -= HandlePaletteChanged;
		workspace.ActiveDocumentChanged -= HandleActiveDocumentChanged;

		// KolourPaint commits the shape when the tool is put away (kpToolPolygonalBase::end).
		// An object that is still being edited is dropped onto the layer first, so that
		// switching tools never loses the curves that were drawn.
		if (document is not null) {
			CommitShapeObject (document);
			CommitShape (document);
		}

		base.OnDeactivated (document, new_tool);
	}

	protected override void OnCommit (Document? document)
	{
		if (document is not null) {
			CommitShapeObject (document);
			CommitShape (document);
		}

		base.OnCommit (document);
	}

	protected override bool OnHandleUndo (Document document)
	{
		bool was_in_progress = HasUncommittedWork;

		// The preview is not part of the history, so a pending shape has to be
		// dropped before the document is reverted.
		ResetShape (document);
		ForgetLastCurve ();
		UpdateStatusBar ();

		// A curve that is still being drawn, or an object that has not been dropped onto
		// the layer yet, is not in the history at all. Undoing it therefore only throws
		// it away - reverting the image as well would take a curve the user drew before
		// it, which reads as "undo removed everything".
		if (was_in_progress)
			return true;

		return base.OnHandleUndo (document);
	}

	protected override bool OnHandleRedo (Document document)
	{
		bool was_in_progress = HasUncommittedWork;

		ResetShape (document);
		ForgetLastCurve ();
		UpdateStatusBar ();

		// Same as for undo: there is nothing to redo until the work in progress is gone.
		if (was_in_progress)
			return true;

		return base.OnHandleRedo (document);
	}

	/// <summary>
	/// The curve that was committed last is gone from the layer, so it cannot be
	/// picked up again.
	/// </summary>
	private void ForgetLastCurve ()
	{
		last_curve = null;
		editing_last_curve = false;

		UpdateEditLastButton ();
	}

	private void UpdateEditLastButton ()
	{
		if (edit_last_button is not null)
			edit_last_button.Sensitive = CanEditLastCurve;
	}

	private void HandlePaletteChanged (object? sender, EventArgs e)
		=> RepaintPreview ();

	/// <summary>
	/// The curve that was committed last belongs to a document, so the button that
	/// picks it up again has to follow the active document.
	/// </summary>
	private void HandleActiveDocumentChanged (object? sender, EventArgs e)
		=> UpdateEditLastButton ();

	#endregion

	#region Mouse

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton == MouseButton.Middle)
			return;

		// Ignore any additional button presses until the current drag is released.
		if (drag_button != MouseButton.None)
			return;

		// While a drawn curve is still an object it owns the mouse: press inside its
		// frame to move it. A press anywhere else drops it onto the layer first.
		if (shape_object is not null) {
			if (e.MouseButton == MouseButton.Left) {
				// A press on the round handle above the frame turns the shape.
				if (rotate_handle.ContainsPoint (e.WindowPoint)) {
					BeginShapeDrag (ShapeDrag.Rotate, e.PointDouble);

					SetCursor (grab_cursor);
					UpdateStatusBar ();
					return;
				}

				// A press on one of the eight handles scales the shape.
				foreach (ShapeFrame handle in resize_handles) {
					if (!handle.ContainsPoint (e.WindowPoint))
						continue;

					BeginShapeDrag (ShapeDrag.Resize, e.PointDouble);
					shape_drag_handle = handle.Handle;
					shape_drag_handle_origin = handle.CanvasPosition;

					if (handle.Cursor is not null)
						SetCursor (handle.Cursor);

					UpdateStatusBar ();
					return;
				}

				// A press inside the frame moves it.
				if (frame.ContainsInside (e.WindowPoint)) {
					BeginShapeDrag (ShapeDrag.Move, e.PointDouble);

					SetCursor (move_cursor);
					UpdateStatusBar ();
					return;
				}
			}

			// The press was outside the frame, so it drops the object and does nothing
			// else: it must not start a new curve under the cursor.
			CommitShapeObject (document);
			return;
		}

		if (editor.Count == 0) {
			BeginShape (document, e);
			return;
		}

		// The other mouse button always finishes the shape.
		if (e.MouseButton != editor.OriginButton) {
			CommitShape (document);
			return;
		}

		// Grab an existing end point or control point instead of adding a new one, so the
		// curve can be adjusted until it is finished.
		if (editor.Grab (HitTestPoint (e.WindowPoint))) {
			drag_button = e.MouseButton;

			// Clicking the single control point again is how MS Paint finishes a three
			// point curve. It only counts as a finish when the point is not moved.
			finish_on_release = editor.Count == 3 && editor.SelectedIndex == 2;

			UpdatePreview (document);
			UpdateStatusBar ();
			return;
		}

		// Both control points are placed: a press elsewhere finishes the curve and starts
		// the next one, the same way Pinta's own shape tools behave.
		if (editor.IsAdjustable) {
			CommitShape (document);
			BeginShape (document, e);
			return;
		}

		// Same button: add another control point (KolourPaint behaviour).
		editor.AddControlPoint (SnapPlacement (document, e, ClampToImage (document, e.PointDouble)));
		drag_button = e.MouseButton;

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Snaps a point to the construction points of the classic MS Paint workflow - the
	/// edges and the centre lines of the canvas, the grid and the points that are
	/// already placed - when the shift key is held down. The control key snaps the
	/// tangent of the curve at the end point the control point belongs to.
	/// </summary>
	private PointD SnapPlacement (Document document, ToolMouseEventArgs e, PointD point)
	{
		PointD result = point;

		// While the initial line is dragged out, shift and control snap the angle.
		bool dragging_line = editor.Count == 2 && editor.DraggingIndex == 1;

		if (!dragging_line && e.IsControlPressed && editor.DraggingIndex is 2 or 3) {
			// The tangent at the end point that the control point belongs to.
			PointD anchor = editor.Points[editor.DraggingIndex == 3 ? 1 : 0];

			result = CurveGeometry.SnapToTangentAngle (anchor, result, out _);
		}

		if (!dragging_line && e.IsShiftPressed) {
			result = CurveGeometry.SnapToConstructionPoints (
				result,
				OtherPoints (),
				LinePoints (),
				SnapContext (document));
		}

		if (pixel_alignment)
			result = new PointD (Math.Round (result.X), Math.Round (result.Y));

		return result;
	}

	/// <summary>
	/// The points that a new point can be snapped to: the ones that are already placed
	/// and the end points of the curve that was committed last, so that a new curve can
	/// be attached to it.
	/// </summary>
	private List<PointD> OtherPoints ()
	{
		List<PointD> points = new ();

		for (int i = 0; i < editor.Count; ++i)
			if (i != editor.DraggingIndex)
				points.Add (editor.Points[i]);

		if (last_curve is not null && last_curve.Points.Length >= 2) {
			points.Add (last_curve.Points[0]);
			points.Add (last_curve.Points[1]);
		}

		return points;
	}

	/// <summary>The two end points of the curve, or an empty list.</summary>
	private List<PointD> LinePoints ()
	{
		if (editor.Count < 2)
			return new List<PointD> ();

		return [editor.Points[0], editor.Points[1]];
	}

	private SnapContext SnapContext (Document document)
		=> new (
			document.ImageSize.Width,
			document.ImageSize.Height,
			GridCellWidth,
			GridCellHeight,
			SNAP_TOLERANCE);

	private int GridCellWidth => canvas_grid is { ShowGrid: true } && canvas_grid.CellWidth > 0 ? canvas_grid.CellWidth : 0;

	private int GridCellHeight => canvas_grid is { ShowGrid: true } && canvas_grid.CellHeight > 0 ? canvas_grid.CellHeight : 0;

	/// <summary>
	/// Starts a new curve from the given press, using the two end points under the cursor.
	/// </summary>
	private void BeginShape (Document document, ToolMouseEventArgs e)
	{
		// The color is decided by the button used here (kpToolPolygonalBase::drawingForegroundColor).
		undo_surface = document.Layers.CurrentUserLayer.Surface.Clone ();

		editor.Begin (ClampToImage (document, e.PointDouble), e.MouseButton);
		editor.Closed = closed_enabled;
		editor.SmoothClose = smooth_close_enabled;
		editor.Symmetry = symmetry_mode;

		drag_button = e.MouseButton;

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Index of the end or control point under the cursor, or -1. The test happens in
	/// window space, like the markers themselves, so the grab area does not depend on zoom.
	/// </summary>
	/// <summary>Index of the point under the given window position, or -1.</summary>
	private int HitTestPoint (PointD window_point)
	{
		for (int i = editor.Count - 1; i >= 0; --i)
			if (HandleFor (i).ContainsPoint (window_point))
				return i;

		return -1;
	}

	/// <summary>Maps a point index (0/1 = end points, 2/3 = control points) to its marker.</summary>
	private CurveHandle HandleFor (int index)
		=> index switch {
			0 => endpoint_start,
			1 => endpoint_end,
			2 => control_point_first,
			_ => control_point_last,
		};

	/// <summary>
	/// Picks the cursor for the object: the four arrows inside its frame, the resize
	/// cursor on a handle.
	/// </summary>
	private void UpdateObjectCursor (PointD window_point)
	{
		if (shape_object is null) {
			SetCursor (DefaultCursor);
			return;
		}

		bool over_handle = false;

		foreach (ShapeFrame handle in resize_handles) {
			bool hovered = handle.ContainsPoint (window_point);
			handle.Hovered = hovered;
			over_handle |= hovered;
		}

		bool over_rotate = rotate_handle.ContainsPoint (window_point);
		rotate_handle.Hovered = over_rotate;

		InvalidateFrameHandles ();

		if (over_rotate || over_handle) {
			// The resize cursors come from the theme, so they look like the ones Pinta
			// uses for its own selection.
			SetCursor (over_rotate ? grab_cursor : DefaultCursor);
			return;
		}

		SetCursor (frame.ContainsInside (window_point) ? move_cursor : DefaultCursor);
	}

	/// <summary>Highlights the marker under the cursor and picks the matching cursor.</summary>
	private void UpdateHover (PointD window_point)
		=> SetHover (HitTestPoint (window_point));

	private void SetHover (int index)
	{
		if (index == editor.HoveredIndex)
			return;

		editor.SetHover (index);

		for (int i = 0; i < 4; ++i)
			HandleFor (i).Hovered = i == index;

		SetCursor (index >= 0 ? grab_cursor : DefaultCursor);

		// The markers themselves do not move, but they have to be repainted highlighted.
		InvalidateHandles ();
	}

	protected override void OnMouseMove (Document document, ToolMouseEventArgs e)
	{
		// Dragging the object around.
		if (shape_object is not null && shape_drag == ShapeDrag.Move) {
			shape_object.Restore (shape_drag_state);

			PointD delta = new (
				e.PointDouble.X - shape_drag_origin.X,
				e.PointDouble.Y - shape_drag_origin.Y);

			shape_object.Translate (delta.X, delta.Y);

			UpdatePreview (document);
			UpdateStatusBar ();
			return;
		}

		// Dragging one of the eight handles scales the object.
		if (shape_object is not null && shape_drag == ShapeDrag.Resize) {
			ResizeShapeObject (document, e);
			return;
		}

		// Dragging the round handle turns the object.
		if (shape_object is not null && shape_drag == ShapeDrag.Rotate) {
			RotateShapeObject (document, e);
			return;
		}

		// The cursor over an object tells what a press would do.
		if (shape_object is not null) {
			UpdateObjectCursor (e.WindowPoint);
			return;
		}

		if (drag_button == MouseButton.None || !editor.IsDragging) {
			UpdateHover (e.WindowPoint);
			return;
		}

		editor.DragTo (SnapPlacement (document, e, ClampToImage (document, e.PointDouble)), e.IsShiftPressed, e.IsControlPressed);

		// The point was moved, so this is not the "click the same spot again" of MS Paint.
		finish_on_release = false;

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		if (e.MouseButton != drag_button)
			return;

		// The end of a move of the object.
		if (shape_drag is not null) {
			shape_drag = null;
			drag_button = MouseButton.None;

			SetCursor (DefaultCursor);
			UpdateStatusBar ();
			return;
		}

		drag_button = MouseButton.None;
		bool placed_control_point = editor.EndDrag ();

		SetHover (HitTestPoint (e.WindowPoint));

		// MS Paint mode: the curve is done as soon as the last control point is placed,
		// and it immediately becomes an object with a selection frame around it.
		if (tool_mode == ToolMode.MSPaint && placed_control_point && editor.Count == 4) {
			CommitShape (document, as_object: true);
			return;
		}

		// KolourPaint mode with "commit now": the same moment, but as paint.
		if (commit_on_release && placed_control_point && editor.Count == 4) {
			CommitShape (document);
			return;
		}

		// Clicking the single control point again finishes a three point curve.
		if (finish_on_release) {
			finish_on_release = false;
			CommitShape (document, as_object: tool_mode == ToolMode.MSPaint);
			return;
		}

		// A curve with both control points stays adjustable until the user finishes it
		// (Enter, the other mouse button, a new curve or switching tools).
		UpdateStatusBar ();
	}

	#endregion

	#region Keyboard

	protected override bool OnKeyDown (Document document, ToolKeyEventArgs e)
	{
		switch (e.Key.Value) {
			case Gdk.Constants.KEY_Return:
			case Gdk.Constants.KEY_KP_Enter:
				if (editor.HasShape) {
					CommitShape (document);
					return true;
				}
				break;

			case Gdk.Constants.KEY_BackSpace:
			case Gdk.Constants.KEY_Delete:
				if (editor.Count > 0)
					return RemoveLastPoint (document);
				break;

			case Gdk.Constants.KEY_Left:
			case Gdk.Constants.KEY_Right:
			case Gdk.Constants.KEY_Up:
			case Gdk.Constants.KEY_Down:
				return NudgeSelectedPoint (document, e);

			case Gdk.Constants.KEY_Escape:
				// Escape throws the object away without dropping it onto the layer.
				if (shape_object is not null) {
					DiscardShapeObject (document);
					UpdateStatusBar ();
					return true;
				}

				if (editor.Count > 0) {
					// A curve that was picked up again has to be put back onto the layer.
					ResetShape (document, restore_cancelled_curve: true);
					UpdateStatusBar ();
					return true;
				}
				break;

			case Gdk.Constants.KEY_e:
				if (e.IsControlPressed && CanEditLastCurve) {
					EditLastCurve ();
					return true;
				}
				break;

			case Gdk.Constants.KEY_bracketleft:
				BrushWidth--;
				return true;

			case Gdk.Constants.KEY_bracketright:
				BrushWidth++;
				return true;

			case Gdk.Constants.KEY_c:
				closed_enabled = !closed_enabled;
				editor.Closed = closed_enabled;

				UpdateStyleWidgets ();
				RepaintPreview ();
				UpdateStatusBar ();
				return true;

			case Gdk.Constants.KEY_f:
				fill_enabled = !fill_enabled;

				UpdateStyleWidgets ();
				RepaintPreview ();
				UpdateStatusBar ();
				return true;
		}

		return base.OnKeyDown (document, e);
	}

	/// <summary>
	/// Takes back the most recently added point: 4 -> 3 -> 2 points, then the whole curve.
	/// </summary>
	private bool RemoveLastPoint (Document document)
	{
		editor.RemoveLastPoint ();

		if (editor.HasShape) {
			UpdatePreview (document);
		} else {
			ResetShape (document);
		}

		UpdateStatusBar ();

		return true;
	}

	/// <summary>
	/// Moves the highlighted point (or the last one grabbed) by one pixel, or ten with
	/// Shift held down.
	/// </summary>
	private bool NudgeSelectedPoint (Document document, ToolKeyEventArgs e)
	{
		int index = editor.EditedIndex;

		if (index < 0 || index >= editor.Count)
			return false;

		double step = e.IsShiftPressed ? 10d : 1d;

		PointD delta = e.Key.Value switch {
			Gdk.Constants.KEY_Left => new PointD (-step, 0d),
			Gdk.Constants.KEY_Right => new PointD (step, 0d),
			Gdk.Constants.KEY_Up => new PointD (0d, -step),
			_ => new PointD (0d, step),
		};

		editor.SetPoint (index, ClampToImage (document, editor.Points[index] + delta));

		UpdatePreview (document);
		UpdateStatusBar ();

		return true;
	}

	#endregion

	#region Drawing

	private Cairo.Color CurrentColor
		=> editor.OriginButton == MouseButton.Right ? palette.SecondaryColor : palette.PrimaryColor;

	/// <summary>
	/// The other of the two palette colours; the gradient runs from the colour of the
	/// curve to this one.
	/// </summary>
	private Cairo.Color OtherColor
		=> editor.OriginButton == MouseButton.Right ? palette.PrimaryColor : palette.SecondaryColor;

	private static PointD ClampToImage (Document document, PointD point)
		=> document.ClampToImageSize (point);

	/// <summary>
	/// The appearance of the curve that is being drawn.
	/// </summary>
	private CurveStyle CurrentStyle ()
		=> new (
			CurrentColor,
			BrushWidth,
			UseAntialiasing,
			UseAlphaBlending,
			DashPattern,
			start_end,
			finish_end,
			stroke_join,
			gradient_enabled ? OtherColor : null,
			taper_start_width,
			taper_end_width,
			fill_enabled);

	/// <summary>
	/// Renders the in-progress curve into the given context, with the tool's current
	/// appearance settings.
	/// </summary>
	private void DrawCurve (Context g)
		=> CurveRenderer.Draw (g, editor.Curve, CurrentStyle ());

	private RectangleI CurrentCanvasDirty (Document document)
	{
		if (shape_object is not null) {
			// Some slack, so that a moved shape never leaves a sliver of its old
			// position behind.
			return document.ClampToImageSize (shape_object.Bounds.Inflated (2, 2).ToInt ());
		}

		if (!editor.HasShape)
			return RectangleI.Zero;

		RectangleD bounds = CurveGeometry.Bounds (editor.Curve, BrushWidth);

		return document.ClampToImageSize (bounds.ToInt ());
	}

	private void UpdatePreview (Document document)
	{
		if (shape_object is not null) {
			document.Layers.ToolLayer.Hidden = false;

			RectangleI object_rect = CurrentCanvasDirty (document);
			RectangleI object_dirty = UnionWithLastDirty (object_rect);

			using (Context g = document.CreateClippedToolContext ()) {
				g.Clear (object_dirty.ToDouble ());
				DrawShapeObject (g);
			}

			document.Workspace.Invalidate (object_dirty);

			last_canvas_dirty = object_rect;
			has_canvas_dirty = true;

			UpdateFrameHandles ();
			UpdateHandles ();

			return;
		}

		if (!editor.HasShape) {
			UpdateHandles ();
			return;
		}

		document.Layers.ToolLayer.Hidden = false;

		RectangleI current = CurrentCanvasDirty (document);
		RectangleI dirty = UnionWithLastDirty (current);

		// Only the dirty region is erased and repainted. Clearing the whole (image sized)
		// tool layer on every mouse move is the dominant cost on a large canvas, and the
		// union above is guaranteed to contain the curve, so repainting all of it is safe.
		using (Context g = document.CreateClippedToolContext ()) {
			g.Clear (dirty.ToDouble ());
			DrawCurve (g);
		}

		document.Workspace.Invalidate (dirty);

		last_canvas_dirty = current;
		has_canvas_dirty = true;

		UpdateHandles ();
	}

	private void RepaintPreview ()
	{
		if (!workspace.HasOpenDocuments)
			return;

		// The object follows the appearance settings while it is being edited, so that a
		// changed width, colour, cap or fill shows up straight away.
		if (shape_object is not null)
			shape_object.Style = CurrentStyle ();

		if (editor.HasShape || shape_object is not null)
			UpdatePreview (workspace.ActiveDocument);
	}

	private RectangleI UnionWithLastDirty (RectangleI current)
		=> has_canvas_dirty ? current.Union (last_canvas_dirty) : current;

	#endregion

	#region Commit / reset

	/// <summary>
	/// A curve that was committed, with everything that is needed to draw it again.
	/// </summary>
	private sealed class CommittedCurve
	{
		public PointD[] Points = [];
		public MouseButton Button;

		public SymmetryMode Symmetry;
		public bool Closed;
		public bool SmoothClose;
		public CurveStyle Style;

		/// <summary>
		/// The pixels of the layer as they were before the curve was drawn - only the
		/// part that the curve covers.
		/// </summary>
		public ImageSurface? Before;

		/// <summary>The area the curve covers.</summary>
		public RectangleI Rect;

		/// <summary>The document the curve belongs to.</summary>
		public Document? Document;
	}

	// The curve that was committed last, so that it can be picked up again.
	private CommittedCurve? last_curve;

	/// <summary>
	/// Keeps the object that was just dropped onto the layer, so that Ctrl+E can pick it
	/// up again the way it picks up a curve.
	/// </summary>
	private void RememberShapeObject (Document document)
	{
		if (shape_object is null)
			return;

		BezierCurve curve = shape_object.Curve;

		last_curve = new CommittedCurve {
			Points = [curve.P0, curve.P3, curve.P1, curve.P2],
			Button = editor.OriginButton == MouseButton.None ? MouseButton.Left : editor.OriginButton,
			Symmetry = SymmetryMode.Off,
			Closed = curve.Closed,
			SmoothClose = curve.SmoothClose,
			Style = shape_object.Style,
			Before = CropLayer (undo_surface, CurrentCanvasDirty (document)),
			Rect = CurrentCanvasDirty (document),
			Document = document,
		};

		UpdateEditLastButton ();
	}

	// True while the curve of last_curve is being edited again.
	private bool editing_last_curve;

	private bool CanEditLastCurve
		=> last_curve is not null
			&& workspace.HasOpenDocuments
			&& ReferenceEquals (last_curve.Document, workspace.ActiveDocument);

	private void CommitShape (Document document, bool as_object = false)
	{
		// In the MS Paint mode a curve that is finished does not become pixels yet: it
		// stays an object with a selection frame around it, so that it can be moved
		// before it is dropped onto the layer.
		if (as_object && editor.HasShape && editor.Count >= 3) {
			BeginShapeObject (document);
			return;
		}

		if (editor.HasShape && undo_surface is not null) {
			using (Context g = document.CreateClippedContext ())
				DrawCurve (g);

			document.Workspace.Invalidate (UnionWithLastDirty (CurrentCanvasDirty (document)));

			document.History.PushNewItem (
				new SimpleHistoryItem (Icon, Name, undo_surface, document.Layers.CurrentUserLayerIndex));

			RememberCurve (document);
		}

		ResetShape (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Turns the curve that was just drawn into an object with a selection frame around
	/// it. The curve stays on the tool layer, so moving it never touches the pixels
	/// underneath.
	/// </summary>
	private void BeginShapeObject (Document document)
	{
		shape_object = new ShapeObject (editor.Curve, CurrentStyle ());
		shape_drag = null;

		// The curve in progress is done; the object takes its place.
		editor.Reset ();
		drag_button = MouseButton.None;
		finish_on_release = false;
		SetHover (-1);

		UpdateFrameHandles ();
		UpdateObjectButtons ();
		UpdatePreview (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Drops the object onto the layer, where it becomes pixels, and forgets it.
	/// </summary>
	private void CommitShapeObject (Document document)
	{
		if (shape_object is not null && undo_surface is not null) {
			using (Context g = document.CreateClippedContext ())
				DrawShapeObject (g);

			document.Workspace.Invalidate (UnionWithLastDirty (CurrentCanvasDirty (document)));

			document.History.PushNewItem (
				new SimpleHistoryItem (Icon, Name, undo_surface, document.Layers.CurrentUserLayerIndex));

			RememberShapeObject (document);

			// The snapshot has been handed to the history; this object is done with it.
			undo_surface = null;
		}

		ClearShapeObject (document);
		UpdateStatusBar ();
	}

	/// <summary>Throws the object away, putting the layer back the way it was.</summary>
	private void DiscardShapeObject (Document document)
	{
		ClearShapeObject (document);
		UpdateStatusBar ();
	}

	/// <summary>Forgets the object without touching the layer.</summary>
	private void ClearShapeObject (Document? document)
	{
		shape_object = null;
		shape_drag = null;

		foreach (ShapeFrame handle in frame_handles)
			handle.Active = false;

		if (document is not null && has_canvas_dirty) {
			using (Context g = document.CreateClippedToolContext ())
				g.Clear (last_canvas_dirty.ToDouble ());

			document.Workspace.Invalidate (last_canvas_dirty);
			document.Layers.ToolLayer.Hidden = true;
		}

		has_canvas_dirty = false;
		last_canvas_dirty = RectangleI.Zero;

		UpdateObjectButtons ();
	}

	/// <summary>Renders the object onto the given context.</summary>
	private void DrawShapeObject (Context g)
	{
		if (shape_object is null)
			return;

		CurveRenderer.Draw (g, shape_object.Curve, shape_object.Style);
	}

	/// <summary>
	/// Keeps the curve that was just committed, so that it can be picked up and
	/// adjusted again.
	/// </summary>
	private void RememberCurve (Document document)
	{
		RectangleI rect = CurrentCanvasDirty (document);

		last_curve = new CommittedCurve {
			Points = [.. editor.Points],
			Button = editor.OriginButton,
			Symmetry = editor.Symmetry,
			Closed = editor.Closed,
			SmoothClose = editor.SmoothClose,
			Style = CurrentStyle (),
			Before = CropLayer (undo_surface, rect),
			Rect = rect,
			Document = document,
		};

		UpdateEditLastButton ();
	}

	/// <summary>
	/// Copies the part of a surface that a rectangle covers into a surface of its own.
	/// </summary>
	private static ImageSurface? CropLayer (ImageSurface? layer, RectangleI rect)
	{
		if (layer is null || rect.IsEmpty)
			return null;

		ImageSurface crop = CairoExtensions.CreateImageSurface (Format.Argb32, rect.Width, rect.Height);

		using Context g = new Context (crop);
		g.Operator = Operator.Source;
		g.SetSourceSurface (layer, -rect.X, -rect.Y);
		g.Rectangle (0, 0, rect.Width, rect.Height);
		g.Fill ();

		return crop;
	}

	/// <summary>
	/// Takes the curve that was committed last off the layer and puts it back into the
	/// editor, so that it can be adjusted again. The curve is drawn again when the
	/// edit is finished; if it is thrown away, the old one is put back.
	/// </summary>
	internal void EditLastCurve ()
	{
		if (!CanEditLastCurve || last_curve is null)
			return;

		Document document = workspace.ActiveDocument;

		// Take the committed curve off the layer, so that the preview of the new one
		// does not lie on top of the old one.
		RestoreLayerRegion (document, last_curve.Before, last_curve.Rect);

		editor.Restore (last_curve.Points, last_curve.Button);
		editor.Symmetry = last_curve.Symmetry;
		editor.Closed = last_curve.Closed;
		editor.SmoothClose = last_curve.SmoothClose;

		closed_enabled = last_curve.Closed;
		smooth_close_enabled = last_curve.SmoothClose;
		symmetry_mode = last_curve.Symmetry;

		UpdateStyleWidgets ();

		// The layer is back to what it was before the curve was drawn, so that is what
		// the undo of the new curve has to restore.
		undo_surface = document.Layers.CurrentUserLayer.Surface.Clone ();

		editing_last_curve = true;

		UpdatePreview (document);
		UpdateStatusBar ();
	}

	/// <summary>
	/// Puts the pixels of a rectangle back to what they were in the given surface,
	/// which holds the area the rectangle covers.
	/// </summary>
	private static void RestoreLayerRegion (Document document, ImageSurface? area, RectangleI rect)
	{
		if (area is null || rect.IsEmpty)
			return;

		ImageSurface layer = document.Layers.CurrentUserLayer.Surface;

		using Context g = new Context (layer);
		g.Operator = Operator.Source;
		g.SetSourceSurface (area, rect.X, rect.Y);
		g.Rectangle (rect.ToDouble ());
		g.Fill ();
		g.Operator = Operator.Over;

		document.Workspace.Invalidate (rect);
	}

	/// <summary>
	/// Draws a committed curve onto the layer again, with the appearance it was
	/// committed with.
	/// </summary>
	private static void DrawCommittedCurve (Document document, CommittedCurve curve)
	{
		BezierCurve bezier = CurveGeometry.Resolve (curve.Points, curve.Closed, curve.SmoothClose);

		using Context g = document.CreateClippedContext ();
		CurveRenderer.Draw (g, bezier, curve.Style);

		document.Workspace.Invalidate (document.ClampToImageSize (curve.Rect));
	}

	private void ResetShape (Document? document, bool restore_cancelled_curve = false)
	{
		bool was_editing_last_curve = editing_last_curve;
		editing_last_curve = false;

		editor.Reset ();
		drag_button = MouseButton.None;
		finish_on_release = false;
		undo_surface = null;

		// The object, if there is one, is thrown away with the rest.
		ClearShapeObject (document);

		if (document is not null) {
			if (has_canvas_dirty) {
				using (Context g = document.CreateClippedToolContext ())
					g.Clear (last_canvas_dirty.ToDouble ());

				document.Workspace.Invalidate (last_canvas_dirty);
			}

			document.Layers.ToolLayer.Hidden = true;

			// The curve that was picked up again has been taken off the layer, so it has
			// to be put back when the edit is thrown away.
			if (restore_cancelled_curve && was_editing_last_curve && last_curve is not null)
				DrawCommittedCurve (document, last_curve);
		}

		has_canvas_dirty = false;
		last_canvas_dirty = RectangleI.Zero;

		SetHover (-1);
		UpdateHandles ();
	}

	#endregion

	#region Clipboard

	/// <summary>
	/// Renders the curve that was committed last into a surface of its own, the way it
	/// is put on the clipboard.
	/// </summary>
	internal ImageSurface? RenderLastCurve ()
	{
		if (last_curve is null)
			return null;

		RectangleI rect = last_curve.Rect;

		if (rect.IsEmpty)
			return null;

		ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, rect.Width, rect.Height);

		using Context g = new Context (surface);
		g.Translate (-rect.X, -rect.Y);
		CurveRenderer.Draw (g, CurveGeometry.Resolve (last_curve.Points, last_curve.Closed, last_curve.SmoothClose), last_curve.Style);

		return surface;
	}

	/// <summary>
	/// Copies the curve that was committed last to the clipboard, the way MS Paint
	/// copies a shape. When there is no curve, the usual copy of the layer happens.
	/// </summary>
	protected override bool OnHandleCopy (Document document, Gdk.Clipboard cb)
	{
		if (last_curve is null || !ReferenceEquals (last_curve.Document, document))
			return false;

		using ImageSurface? surface = RenderLastCurve ();

		if (surface is null)
			return false;

		cb.SetImage (surface);

		return true;
	}

	#endregion

	#region The frame of the shape

	/// <summary>
	/// Puts the dashed frame and the handles around the object: four corners, four edge
	/// middles and the round rotate handle above the top edge, all in window space.
	/// </summary>
	private void UpdateFrameHandles ()
	{
		if (shape_object is null) {
			foreach (ShapeFrame handle in frame_handles)
				handle.Active = false;

			return;
		}

		(double minX, double minY, double maxX, double maxY) = shape_object.LocalExtent;

		// The frame follows the shape in its own frame: its corners are mapped onto the
		// canvas, so a turned shape wears a turned frame.
		PointD Local (double x, double y) => shape_object.Map (new PointD (x, y));

		PointD top_left = Local (minX, minY);
		PointD top_right = Local (maxX, minY);
		PointD bottom_right = Local (maxX, maxY);
		PointD bottom_left = Local (minX, maxY);

		frame.Active = true;
		frame.Corners = [top_left, top_right, bottom_right, bottom_left];

		SetHandle (FrameHandle.TopLeft, top_left);
		SetHandle (FrameHandle.Top, Local ((minX + maxX) / 2d, minY));
		SetHandle (FrameHandle.TopRight, top_right);
		SetHandle (FrameHandle.Right, Local (maxX, (minY + maxY) / 2d));
		SetHandle (FrameHandle.BottomRight, bottom_right);
		SetHandle (FrameHandle.Bottom, Local ((minX + maxX) / 2d, maxY));
		SetHandle (FrameHandle.BottomLeft, bottom_left);
		SetHandle (FrameHandle.Left, Local (minX, (minY + maxY) / 2d));

		// The rotate handle floats above the middle of the top edge, along the direction
		// the shape is pointing, so it turns with the frame.
		PointD top_middle = Local ((minX + maxX) / 2d, minY);
		double zoom = workspace.ActiveWorkspace.Scale;

		if (zoom <= 0)
			zoom = 1;

		// "Up" in the shape's own frame, turned the same way and scaled to window space.
		double sin = Math.Sin (shape_object.Angle - Math.PI / 2d);
		double cos = Math.Cos (shape_object.Angle - Math.PI / 2d);

		rotate_handle.Active = true;
		rotate_handle.FrameAnchor = top_middle;
		rotate_handle.CanvasPosition = new PointD (
			top_middle.X + cos * ROTATE_HANDLE_OFFSET / zoom,
			top_middle.Y + sin * ROTATE_HANDLE_OFFSET / zoom);

		InvalidateFrameHandles ();

		void SetHandle (FrameHandle kind, PointD position)
		{
			ShapeFrame handle = resize_handles[(int) kind];
			handle.Active = true;
			handle.CanvasPosition = position;
		}
	}

	private const double ROTATE_HANDLE_OFFSET = 22d;

	/// <summary>Repaints the frame and the handles after they moved.</summary>
	private void InvalidateFrameHandles ()
	{
		if (!workspace.HasOpenDocuments)
			return;

		foreach (ShapeFrame handle in frame_handles)
			if (handle.Active)
				workspace.InvalidateWindowRect (handle.InvalidateRect);
	}

	#endregion

	#region Handles

	private void UpdateHandles ()
	{
		bool has_line = editor.Count >= 2;
		bool has_first_control_point = editor.Count >= 3;
		bool has_last_control_point = editor.Count >= 4;

		endpoint_start.Active = has_line;
		endpoint_end.Active = has_line;
		if (has_line) {
			endpoint_start.CanvasPosition = editor.Points[0];
			endpoint_end.CanvasPosition = editor.Points[1];
		}

		control_point_first.Active = has_first_control_point;
		if (has_first_control_point)
			control_point_first.CanvasPosition = editor.Points[2];

		control_point_last.Active = has_last_control_point;
		if (has_last_control_point)
			control_point_last.CanvasPosition = editor.Points[3];

		leash_start.Active = has_first_control_point;
		if (has_first_control_point) {
			leash_start.CanvasPosition = editor.Points[0];
			leash_start.SecondaryCanvasPosition = editor.Points[2];
		}

		leash_end.Active = has_last_control_point;
		if (has_last_control_point) {
			leash_end.CanvasPosition = editor.Points[1];
			leash_end.SecondaryCanvasPosition = editor.Points[3];
		}

		InvalidateHandles ();
	}

	private void InvalidateHandles ()
	{
		if (!workspace.HasOpenDocuments) {
			last_handle_dirty.Clear ();
			return;
		}

		List<RectangleI> current = [];

		foreach (CurveHandle handle in all_handles)
			if (handle.Active)
				current.Add (handle.InvalidateRect);

		// Erase wherever a handle was drawn last frame, then repaint the new ones.
		foreach (RectangleI rect in last_handle_dirty)
			workspace.InvalidateWindowRect (rect);

		foreach (RectangleI rect in current)
			workspace.InvalidateWindowRect (rect);

		last_handle_dirty.Clear ();
		last_handle_dirty.AddRange (current);
	}

	#endregion

	#region Status bar

	private void UpdateStatusBar ()
	{
		// The symmetry can be broken by grabbing the point that the other one was
		// derived from, so the panel has to follow the editor.
		if (editor.Symmetry != symmetry_mode) {
			symmetry_mode = editor.Symmetry;
			UpdateStyleWidgets ();
		}

		PintaCore.Chrome.SetStatusBarText ($" {Name}: {StatusMessage ()}");
	}

	internal string StatusMessage ()
	{
		// While a drawn curve is an object the status bar talks about the object.
		if (shape_object is not null) {
			string hint = Strings.Get ("ObjectHint");
			(double width, double height) = CurveGeometry.Size (shape_object.Curve);

			return string.Join (" · ", new[] {
				hint,
				string.Format (Strings.Get ("SizeInfo"), Math.Round (width), Math.Round (height)),
				Strings.Get ("ObjectInfo"),
			});
		}

		if (!editor.HasShape)
			return Strings.Get ("DragStartEnd");

		string finish = ButtonLabel (OtherButton (editor.OriginButton));
		string message;

		if (drag_button != MouseButton.None) {
			message = DescribePoint (editor.DraggingIndex) + " · " + Strings.Get ("ReleaseToPlace");
		} else if (editor.IsAdjustable) {
			message = string.Format (Strings.Get ("HintAdjust"), finish);
		} else if (editor.Count == 2) {
			message = string.Format (Strings.Get ("HintFirstControl"), ButtonLabel (editor.OriginButton), finish);
		} else {
			message = string.Format (Strings.Get ("HintLastControl"), ButtonLabel (editor.OriginButton), finish);
		}

		if (editing_last_curve)
			message = Strings.Get ("EditLastCurve") + " " + message;

		return message + InfoSuffix ();
	}

	/// <summary>
	/// Compact "how far along is the shape" information: the edited point's coordinates,
	/// the number of points placed so far and, for a finished curve, its size.
	/// </summary>
	private string InfoSuffix ()
	{
		List<string> parts = [];

		string angle = CurveGeometry.FormatAngle (editor.SnappedRadians);
		if (angle.Length > 0)
			parts.Add (angle);

		// While dragging, the message itself already names the point.
		if (drag_button == MouseButton.None && editor.HoveredIndex >= 0 && editor.HoveredIndex < editor.Count)
			parts.Add (DescribePoint (editor.HoveredIndex));

		if (editor.IsAdjustable) {
			(double width, double height) = CurveGeometry.Size (editor.Curve);
			parts.Add (string.Format (Strings.Get ("SizeInfo"), Math.Round (width), Math.Round (height)));
		}

		if (closed_enabled)
			parts.Add (Strings.Get ("ClosedInfo"));

		if (fill_enabled)
			parts.Add (Strings.Get ("FillInfo"));

		if (symmetry_mode != SymmetryMode.Off)
			parts.Add (string.Format (
				Strings.Get ("SymmetryInfo"),
				Strings.Get (symmetry_mode == SymmetryMode.Mirror ? "SymmetryMirror" : "SymmetryCenter")));

		parts.Add (string.Format (Strings.Get ("PointCount"), editor.Count));

		return " · " + string.Join (" · ", parts);
	}

	/// <summary>Human readable name plus canvas position of one of the points.</summary>
	private string DescribePoint (int index)
	{
		if (index < 0 || index >= editor.Count)
			return string.Empty;

		PointD point = editor.Points[index];

		return string.Format (
			Strings.Get ("PointInfo"),
			index + 1,
			Math.Round (point.X),
			Math.Round (point.Y));
	}

	private static MouseButton OtherButton (MouseButton button)
		=> button == MouseButton.Right ? MouseButton.Left : MouseButton.Right;

	private static string ButtonLabel (MouseButton button)
		=> Strings.Get (button == MouseButton.Right ? "ButtonRight" : "ButtonLeft");

	#endregion
}
