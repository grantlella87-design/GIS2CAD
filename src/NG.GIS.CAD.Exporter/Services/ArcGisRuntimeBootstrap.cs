using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Esri.ArcGISRuntime;

namespace NG.GIS.CAD.Exporter.Services
{
    internal static class ArcGisRuntimeBootstrap
    {
        private static bool _initialized;
        private static readonly object Gate = new object();

        internal static void Initialize()
        {
            if (_initialized) return;
            lock (Gate)
            {
                if (_initialized) return;
                var installDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
                {
                    TryAddDllDirectory(installDir);
                    TrySetDllDirectory(installDir);
                    TrySetArcGisInstallDirectoryIfAvailable(installDir);
                }
                _initialized = true;
            }
        }

        private static void TrySetArcGisInstallDirectoryIfAvailable(string installDir)
        {
            try
            {
                var method = typeof(ArcGISRuntimeEnvironment).GetMethod(
                    "SetInstallDirectory",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { installDir });
                }
            }
            catch
            {
                // Some ArcGIS Runtime versions do not expose SetInstallDirectory,
                // or runtime may already be initialized. Native DLL probing above
                // is still what AutoCAD-hosted loading needs.
            }
        }

        private static void TryAddDllDirectory(string path)
        {
            try
            {
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
                AddDllDirectory(path);
            }
            catch { }
        }

        private static void TrySetDllDirectory(string path)
        {
            try { SetDllDirectory(path); }
            catch { }
        }

        private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}