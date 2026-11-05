//
// CurveAddin.cs
//
// Add-in entry point: registers the curve tool and makes the add-in's icons visible to
// the GTK icon theme.
//
using System;
using System.IO;
using System.Text.RegularExpressions;
using Mono.Addins;
using Pinta.Core;

// The tests use the internal geometry and localization helpers directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo ("TheMorbidArk.PintaExtension.BezierCurve.SmokeTest")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo ("TheMorbidArk.PintaExtension.BezierCurve.Tests")]

namespace TheMorbidArk.PintaExtension.BezierCurve;

// The extension node is declared in .addin.xml (see the comment there for why).
public sealed class CurveAddin : IExtension
{
	/// <summary>Icon name used by the tool; see <see cref="RegisterIcons"/>.</summary>
	internal const string ToolIcon = "tool-bezier-curve-symbolic";

	/// <summary>The add-in id, as declared in .addin.xml.</summary>
	internal const string AddinId = "TheMorbidArk.PintaExtension.BezierCurve";

	/// <summary>Prefix of the embedded icon resources (see TheMorbidArk.PintaExtension.BezierCurve.csproj).</summary>
	private const string ICON_RESOURCE_PREFIX = "res/";

	/// <summary>
	/// How long to wait before acting on an Uninitialize: Mono.Addins unloads the old
	/// add-in and loads the new one within the same round of events, so the registry has
	/// not necessarily been updated yet.
	/// </summary>
	private const uint REMOVAL_DELAY_MS = 250;

	// Initialization is idempotent: an in-place update initializes the new instance while
	// the old one is still alive, and its Uninitialize arrives on top of that.
	private static bool registered;
	private static uint pending_removal;

	public void Initialize ()
	{
		CancelPendingRemoval ();

		if (registered)
			return;

		RegisterIcons ();

		PintaCore.Tools.AddTool (new CurveTool (PintaCore.Services));
		registered = true;
	}

	public void Uninitialize ()
	{
		// Installing a new version over a running Pinta unloads this add-in and loads the
		// new one straight away. Removing the tool here would empty the toolbox until Pinta
		// is restarted - and when the new assembly cannot be resolved (which happens while
		// upgrading from a build that still had a per-release assembly identity), it would
		// never come back. Wait a moment and only remove the tool if the add-in is really
		// gone or disabled; an update keeps it, running the old code until the next start.
		CancelPendingRemoval ();

		pending_removal = GLib.Functions.TimeoutAdd (GLib.Constants.PRIORITY_DEFAULT, REMOVAL_DELAY_MS, () => {
			pending_removal = 0;

			Addin? addin = AddinManager.Registry.GetAddin (AddinId);

			if (addin is not null && addin.Enabled)
				return false;

			registered = false;
			PintaCore.Tools.RemoveInstanceOfTool<CurveTool> ();

			return false;
		});
	}

	private static void CancelPendingRemoval ()
	{
		if (pending_removal == 0)
			return;

		GLib.Functions.SourceRemove (pending_removal);
		pending_removal = 0;
	}

	/// <summary>
	/// Tool icons are looked up by name in the GTK icon theme (ToolBoxButton uses
	/// IconName), so an icon theme directory has to be added to the theme's search
	/// path. The icons are embedded in this assembly and unpacked into the user's
	/// cache directory instead of being installed next to the DLL, which keeps the
	/// add-in a single file - and single file add-ins uninstall cleanly.
	/// </summary>
	private static void RegisterIcons ()
	{
		string directory = IconCacheDirectory ();

		try {
			ExtractEmbeddedIcons (directory);
		} catch (Exception e) {
			Console.Error.WriteLine ($"Bezier Curve: could not unpack icons: {e.Message}");
			return;
		}

		GtkExtensions.GetDefaultIconTheme ().AddSearchPath (directory);
	}

	/// <summary>
	/// Versioned so that a new release always gets freshly unpacked icons. The version
	/// comes from the add-in manifest (the same string Mono.Addins uses), not from the
	/// assembly version - that one is deliberately stable so that installing over a
	/// running Pinta keeps working (see TheMorbidArk.PintaExtension.BezierCurve.csproj).
	/// </summary>
	internal static string IconCacheDirectory ()
		=> Path.Combine (GLib.Functions.GetUserCacheDir (), "pinta-extension", "bezier-curve", AddinVersion (), "icons");

	private static string? addin_version;

	/// <summary>The version declared in the embedded .addin.xml manifest.</summary>
	internal static string AddinVersion ()
	{
		if (addin_version is not null)
			return addin_version;

		System.Reflection.Assembly assembly = typeof (CurveAddin).Assembly;

		foreach (string resource in assembly.GetManifestResourceNames ()) {
			if (!resource.EndsWith (".addin.xml", StringComparison.Ordinal))
				continue;

			using Stream? stream = assembly.GetManifestResourceStream (resource);
			if (stream is null)
				continue;

			using StreamReader reader = new (stream);
			Match match = Regex.Match (reader.ReadToEnd (), "<Addin [^>]*version=\"(?<version>[^\"]+)\"");

			if (match.Success)
				return addin_version = match.Groups["version"].Value;
		}

		return addin_version = assembly.GetName ().Version?.ToString (3) ?? "0.0.0";
	}

	private static void ExtractEmbeddedIcons (string directory)
	{
		System.Reflection.Assembly assembly = typeof (CurveAddin).Assembly;

		foreach (string resource in assembly.GetManifestResourceNames ()) {
			if (!resource.StartsWith (ICON_RESOURCE_PREFIX, StringComparison.Ordinal))
				continue;

			string relative = resource[ICON_RESOURCE_PREFIX.Length..].Replace ('\\', '/');

			if (relative.Length == 0 || relative.EndsWith ('/'))
				continue;

			string target = Path.Combine (directory, relative.Replace ('/', Path.DirectorySeparatorChar));

			if (File.Exists (target))
				continue;

			using Stream? source = assembly.GetManifestResourceStream (resource);
			if (source is null)
				continue;

			Directory.CreateDirectory (Path.GetDirectoryName (target)!);

			using FileStream destination = File.Create (target);
			source.CopyTo (destination);
		}
	}
}
