//
// CairoNativeLibrary.cs
//
// The Cairo bindings look for a native library called "cairo-graphics"; inside Pinta a
// resolver maps that to the platform's libcairo. The unit tests render into plain image
// surfaces, so they need the same mapping - and when libcairo is not installed (some CI
// runners), they skip themselves instead of failing.
//
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Cairo;
using NUnit.Framework;
using Pinta.Core;

namespace TheMorbidArk.PintaExtension.BezierCurve.Tests;

[SetUpFixture]
public sealed class CairoNativeLibrary
{
	internal static bool Available { get; private set; }

	private const string CAIRO = "cairo-graphics";

	[OneTimeSetUp]
	public void Register ()
	{
		try {
			NativeLibrary.SetDllImportResolver (typeof (ImageSurface).Assembly, Resolve);
		} catch (InvalidOperationException) {
			// A resolver is already installed; carry on and see if it works.
		}

		try {
			using ImageSurface surface = CairoExtensions.CreateImageSurface (Format.Argb32, 1, 1);
			Available = true;
		} catch (DllNotFoundException) {
			Available = false;
		}
	}

	internal static void RequireLibCairo ()
	{
		if (!Available)
			Assert.Ignore ("libcairo is not available, skipping the rendering tests");
	}

	private static IntPtr Resolve (string name, Assembly assembly, DllImportSearchPath? search_path)
	{
		if (name != CAIRO)
			return IntPtr.Zero;

		string candidate =
			OperatingSystem.IsWindows () ? "libcairo-2.dll"
			: OperatingSystem.IsMacOS () ? "libcairo.2.dylib"
			: "libcairo.so.2";

		return NativeLibrary.TryLoad (candidate, assembly, search_path, out IntPtr handle)
			? handle
			: IntPtr.Zero;
	}
}
