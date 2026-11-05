//
// mpack.cs
//
// Small wrapper around Mono.Addins.Setup so that the add-in can be packaged and
// installed with the exact same code path Pinta itself uses, instead of hand
// crafting the .mpack container.
//
//   mpack build   <outputDirectory> <file>...      build .mpack file(s)
//   mpack install <addinsDirectory> <mpack>...     install into a Mono.Addins registry
//   mpack uninstall <addinsDirectory> <addinId>...  uninstall from a registry
//   mpack list    <addinsDirectory>                list the add-ins in a registry
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Addins;
using Mono.Addins.Setup;

namespace TheMorbidArk.PintaExtension.BezierCurve.Packaging;

internal static class Program
{
	private static int Main (string[] args)
	{
		if (args.Length < 2) {
			PrintUsage ();
			return 2;
		}

		string command = args[0];
		string directory = Path.GetFullPath (args[1]);
		string[] rest = args.Skip (2).ToArray ();

		try {
			switch (command) {
				case "build":
					return Build (directory, rest);
				case "install":
					return Install (directory, rest);
				case "uninstall":
					return Uninstall (directory, rest);
				case "list":
					return List (directory);
				default:
					PrintUsage ();
					return 2;
			}
		} catch (Exception e) {
			Console.Error.WriteLine ($"error: {e.Message}");
			Console.Error.WriteLine (e);
			return 1;
		}
	}

	private static int Build (string output_directory, string[] inputs)
	{
		if (inputs.Length == 0) {
			Console.Error.WriteLine ("error: no input files");
			return 2;
		}

		List<string> files = [];

		foreach (string input in inputs) {
			string full = Path.GetFullPath (input);
			if (Directory.Exists (full))
				files.AddRange (Directory.EnumerateFiles (full, "*", SearchOption.AllDirectories));
			else if (File.Exists (full))
				files.Add (full);
			else {
				Console.Error.WriteLine ($"error: not found: {input}");
				return 2;
			}
		}

		Directory.CreateDirectory (output_directory);

		// A throwaway registry is enough: BuildPackage only needs a service instance.
		string scratch = Path.Combine (Path.GetTempPath (), "mpack-build-" + Guid.NewGuid ().ToString ("N"));
		Directory.CreateDirectory (scratch);

		try {
			SetupService service = new (new AddinRegistry (scratch));
			string[] created = service.BuildPackage (new SilentProgress (), output_directory, files.ToArray ());

			foreach (string file in created)
				Console.WriteLine ("PACKAGE: " + file);

			if (created.Length == 0) {
				Console.Error.WriteLine ("error: no package was created");
				return 1;
			}

			return 0;
		} finally {
			try { Directory.Delete (scratch, true); } catch { /* best effort */ }
		}
	}

	private static int Install (string addins_directory, string[] packages)
	{
		if (packages.Length == 0) {
			Console.Error.WriteLine ("error: no packages given");
			return 2;
		}

		Directory.CreateDirectory (addins_directory);

		SetupService service = new (new AddinRegistry (addins_directory));
		bool ok = service.Install (new SilentProgress (), packages.Select (Path.GetFullPath).ToArray ());

		if (!ok) {
			Console.Error.WriteLine ("error: installation failed");
			return 1;
		}

		Console.WriteLine ("installed into " + addins_directory);
		return 0;
	}

	private static int Uninstall (string addins_directory, string[] addin_ids)
	{
		SetupService service = new (new AddinRegistry (addins_directory));
		service.Uninstall (new SilentProgress (), addin_ids);

		Console.WriteLine ("uninstalled from " + addins_directory);
		return 0;
	}

	private static int List (string addins_directory)
	{
		AddinRegistry registry = new (addins_directory);

		foreach (Addin addin in registry.GetAddins ())
			Console.WriteLine ($"{addin.Id} {addin.Description.Version} (enabled={addin.Enabled})");

		return 0;
	}

	/// <summary>
	/// Keeps stdout clean for the generated file names; warnings and errors still go
	/// to stderr.
	/// </summary>
	private sealed class SilentProgress : IProgressStatus
	{
		public int LogLevel => 0;
		public bool IsCanceled => false;
		public void SetMessage (string message) { }
		public void SetProgress (double progress) { }
		public void Log (string message) { }
		public void ReportWarning (string message) => Console.Error.WriteLine ("warning: " + message);
		public void ReportError (string message, Exception? exception)
			=> Console.Error.WriteLine ($"error: {message}{(exception is null ? string.Empty : ": " + exception.Message)}");
		public void Cancel () { }
	}

	private static void PrintUsage ()
	{
		Console.Error.WriteLine ("usage:");
		Console.Error.WriteLine ("  mpack build     <outputDirectory> <file|directory>...");
		Console.Error.WriteLine ("  mpack install   <addinsDirectory> <mpack>...");
		Console.Error.WriteLine ("  mpack uninstall <addinsDirectory> <addinId>...");
		Console.Error.WriteLine ("  mpack list      <addinsDirectory>");
	}
}
