//
// StringsTests.cs
//
// The add-in ships its translations inside the assembly (no satellite assemblies), so
// the culture lookup itself is worth testing.
//
using System.Globalization;
using NUnit.Framework;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[TestFixture]
public sealed class StringsTests
{
	private CultureInfo original_culture = null!;

	[SetUp]
	public void SetUp () => original_culture = CultureInfo.CurrentUICulture;

	[TearDown]
	public void TearDown () => CultureInfo.CurrentUICulture = original_culture;

	[Test]
	public void EnglishIsTheNeutralLanguage ()
	{
		CultureInfo.CurrentUICulture = new CultureInfo ("en-US");

		Assert.That (Strings.Get ("ToolName"), Is.EqualTo ("Bezier"));
	}

	[Test]
	public void SimplifiedChineseIsTranslated ()
	{
		CultureInfo.CurrentUICulture = new CultureInfo ("zh-Hans");

		Assert.That (Strings.Get ("ToolName"), Is.EqualTo ("贝塞尔"));
		Assert.That (Strings.Get ("DragStartEnd"), Is.EqualTo ("拖出起点和终点。"));
	}

	[Test]
	public void RegionalChineseFallsBackToZhHans ()
	{
		// zh-CN -> zh-Hans -> zh -> neutral, so the zh-Hans strings must be picked up.
		CultureInfo.CurrentUICulture = new CultureInfo ("zh-CN");

		Assert.That (Strings.Get ("ToolName"), Is.EqualTo ("贝塞尔"));
	}

	[Test]
	public void UnknownCulturesFallBackToEnglish ()
	{
		CultureInfo.CurrentUICulture = new CultureInfo ("de-DE");

		Assert.That (Strings.Get ("ToolName"), Is.EqualTo ("Bezier"));
	}

	[Test]
	public void UnknownKeysReturnTheKey ()
	{
		Assert.That (Strings.Get ("ThisKeyDoesNotExist"), Is.EqualTo ("ThisKeyDoesNotExist"));
	}
}
