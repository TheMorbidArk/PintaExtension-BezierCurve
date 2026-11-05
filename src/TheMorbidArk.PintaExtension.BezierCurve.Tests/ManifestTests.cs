//
// ManifestTests.cs
//
// Two things must not break:
//  - Mono.Addins tracks updates by the *add-in* version, which lives in .addin.xml, and
//    treats "same id + same version" as already installed, so a forgotten bump silently
//    does nothing;
//  - the assembly *identity* must stay constant between releases, otherwise installing a
//    new version over a running Pinta makes Mono.Addins try to load the new file for the
//    old identity and fail with a FileLoadException until Pinta is restarted.
//
using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class ManifestTests
{
	private static string ReadEmbeddedManifest (Assembly assembly)
	{
		string name = assembly.GetManifestResourceNames ()
			.Single (n => n.EndsWith (".addin.xml", StringComparison.Ordinal));

		using System.IO.Stream stream = assembly.GetManifestResourceStream (name)!;
		using System.IO.StreamReader reader = new (stream);

		return reader.ReadToEnd ();
	}

	[Test]
	public void ManifestIsEmbedded ()
	{
		Assert.That (ReadEmbeddedManifest (typeof (CurveAddin).Assembly), Does.Contain ("<Addin id=\"TheMorbidArk.PintaExtension.BezierCurve\""));
	}

	[Test]
	public void AssemblyVersionIsStable ()
	{
		Version version = typeof (CurveAddin).Assembly.GetName ().Version!;

		// Intentionally a constant: a changing identity breaks installing over a running
		// Pinta (see TheMorbidArk.PintaExtension.BezierCurve.csproj). Bumping this is a breaking change for add-in
		// updates, not a routine edit.
		Assert.That (version.ToString (), Is.EqualTo ("1.0.0.0"));
	}

	[Test]
	public void TheManifestVersionIsReadableAtRuntime ()
	{
		Assembly assembly = typeof (CurveAddin).Assembly;
		string manifest = ReadEmbeddedManifest (assembly);

		Match match = Regex.Match (manifest, "<Addin [^>]*version=\"(?<version>[^\"]+)\"");
		Assert.That (match.Success, Is.True, "the manifest has no version attribute");

		Assert.That (CurveAddin.AddinVersion (), Is.EqualTo (match.Groups["version"].Value));
	}

	[Test]
	public void TheInformationalVersionTracksTheRelease ()
	{
		string? informational = typeof (CurveAddin).Assembly
			.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute> ()
			?.InformationalVersion;

		Assert.That (informational, Is.EqualTo (CurveAddin.AddinVersion ()),
			"the informational version is what shows up in diagnostics, so it carries the release number");
	}

	[Test]
	public void ManifestDeclaresThePintaDependency ()
	{
		string manifest = ReadEmbeddedManifest (typeof (CurveAddin).Assembly);

		Assert.That (manifest, Does.Contain ("<Addin id=\"Pinta\""));
	}
}
