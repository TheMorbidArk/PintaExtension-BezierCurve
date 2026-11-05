//
// Strings.cs
//
// Localization for the add-in: English (Resources/Language.resx) plus any additional
// cultures in sibling .resx files, all embedded in this single assembly.
//
// ResourceManager is not used here because it only looks for culture specific
// resources in satellite assemblies; an add-in is much easier to ship as one file,
// so the embedded .resources are looked up along the normal culture chain instead.
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;

namespace TheMorbidArk.PintaExtension.BezierCurve;

internal static class Strings
{
	private const string RESOURCE_PREFIX = "TheMorbidArk.PintaExtension.BezierCurve.Language";

	private static readonly Assembly assembly = typeof (Strings).Assembly;
	private static readonly Dictionary<string, ResourceSet?> cache = new (StringComparer.OrdinalIgnoreCase);

	internal static string Get (string key)
	{
		foreach (CultureInfo culture in CultureChain (CultureInfo.CurrentUICulture)) {
			ResourceSet? resources = Load (culture.Name);
			string? value = resources?.GetString (key);

			if (value is not null)
				return value;
		}

		return key;
	}

	/// <summary>
	/// "zh-Hans" resources also serve "zh-CN" and "zh-SG", because the parent chain of
	/// those cultures goes through "zh-Hans" before reaching the neutral resources.
	/// </summary>
	private static IEnumerable<CultureInfo> CultureChain (CultureInfo culture)
	{
		for (CultureInfo? current = culture; current is not null && current.Name.Length > 0; current = current.Parent)
			yield return current;

		yield return CultureInfo.InvariantCulture;
	}

	private static ResourceSet? Load (string culture_name)
	{
		if (cache.TryGetValue (culture_name, out ResourceSet? cached))
			return cached;

		string resource_name = culture_name.Length == 0
			? $"{RESOURCE_PREFIX}.resources"
			: $"{RESOURCE_PREFIX}.{culture_name}.resources";

		ResourceSet? result = null;

		using Stream? stream = assembly.GetManifestResourceStream (resource_name);
		if (stream is not null)
			result = new ResourceSet (stream);

		cache[culture_name] = result;

		return result;
	}
}
