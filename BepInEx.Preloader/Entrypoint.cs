﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Preloader.RuntimeFixes;
using HarmonyXInterop;

namespace BepInEx.Preloader
{
	internal static class PreloaderRunner
	{
		public static void PreloaderPreMain()
		{
			string bepinPath = Utility.ParentDirectory(Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH), 2);

			Paths.SetExecutablePath(EnvVars.DOORSTOP_PROCESS_PATH, bepinPath, EnvVars.DOORSTOP_MANAGED_FOLDER_DIR);
			HarmonyInterop.Initialize();
			AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;
			// Remove temporary resolver early so it won't override local resolver
			AppDomain.CurrentDomain.AssemblyResolve -= Entrypoint.ResolveCurrentDirectory;

			PreloaderMain();
		}

		private static void PreloaderMain()
		{
			if (Preloader.ConfigApplyRuntimePatches.Value)
			{
				XTermFix.Apply();
				ConsoleSetOutFix.Apply();
			}

			Preloader.Run();
		}

		private static Assembly LocalResolve(object sender, ResolveEventArgs args)
		{
			if (!Utility.TryParseAssemblyName(args.Name, out var assemblyName))
				return null;

			// Use parse assembly name on managed side because native GetName() can fail on some locales
			// if the game path has "exotic" characters
			var foundAssembly = AppDomain.CurrentDomain.GetAssemblies()
										 .FirstOrDefault(x => Utility.TryParseAssemblyName(x.FullName, out var name) && name.Name == assemblyName.Name);

			if (foundAssembly != null)
				return foundAssembly;

			if (Utility.TryResolveDllAssembly(assemblyName, Paths.BepInExAssemblyDirectory, out foundAssembly)
				|| Utility.TryResolveDllAssembly(assemblyName, Paths.PatcherPluginPath, out foundAssembly)
				|| Utility.TryResolveDllAssembly(assemblyName, Paths.PluginPath, out foundAssembly))
				return foundAssembly;

			return null;
		}
	}

	internal static class Entrypoint
	{
		private static string preloaderPath;

		/// <summary>
		///     The main entrypoint of BepInEx, called from Doorstop.
		/// </summary>
		public static void Main()
		{
			// We set it to the current directory first as a fallback, but try to use the same location as the .exe file.
			string silentExceptionLog = $"preloader_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log";

			try
			{
				EnvVars.LoadVars();

				string gamePath = Path.GetDirectoryName(EnvVars.DOORSTOP_PROCESS_PATH) ?? ".";
				silentExceptionLog = Path.Combine(gamePath, silentExceptionLog);

				// Get the path of this DLL via Doorstop env var because Assembly.Location mangles non-ASCII characters on some versions of Mono for unknown reasons
				preloaderPath = Path.GetDirectoryName(Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH));

				AppDomain.CurrentDomain.AssemblyResolve += ResolveCurrentDirectory;

				// In some versions of Unity 4, Mono tries to resolve BepInEx.dll prematurely because of the call to Paths.SetExecutablePath
				// To prevent that, we have to use reflection and a separate startup class so that we can install required assembly resolvers before the main code
				typeof(Entrypoint).Assembly.GetType($"BepInEx.Preloader.{nameof(PreloaderRunner)}")
								  ?.GetMethod(nameof(PreloaderRunner.PreloaderPreMain))
								  ?.Invoke(null, null);
			}
			catch (Exception ex)
			{
				File.WriteAllText(silentExceptionLog, ex.ToString());
			}
			finally
			{
				AppDomain.CurrentDomain.AssemblyResolve -= ResolveCurrentDirectory;
			}
		}

		internal static Assembly ResolveCurrentDirectory(object sender, ResolveEventArgs args)
		{
			// Can't use Utils here because it's not yet resolved
			var name = new AssemblyName(args.Name);

			try
			{
				return Assembly.LoadFile(Path.Combine(preloaderPath, $"{name.Name}.dll"));
			}
			catch (Exception)
			{
				return null;
			}
		}

	}
}