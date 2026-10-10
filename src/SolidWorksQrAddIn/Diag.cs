using System;
using System.IO;
using System.Reflection;
using SolidWorksQrAddIn.Core.Icons;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// Minimal diagnostics for the in-process add-in. Writes one line per event
    /// to <c>%LOCALAPPDATA%\SolidWorksQrAddIn\debug.log</c> — always writable,
    /// even when the DLL is installed under Program Files (the previous
    /// beside-the-DLL log failed silently there, leaving installs blind).
    /// When LocalAppData is unavailable the beside-the-DLL location remains as
    /// the dev/unpacked fallback. Never throws — a logging failure must not
    /// break a COM host call.
    /// </summary>
    internal static class Diag
    {
        /// <summary>The per-user log path (first writer wins; shared naming with the icon cache).</summary>
        public static string? PerUserLogPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
                return null;
            return Path.Combine(local, QrIconLocations.PerUserFolderName, "debug.log");
        }

        /// <summary>Beside-the-DLL log path — the fallback when LocalAppData is unset.</summary>
        public static string BesideAssemblyLogPath()
        {
            string dir = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? ".";
            return Path.Combine(dir, "SolidWorksQrAddIn.debug.log");
        }

        public static void Log(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            string? perUser = PerUserLogPath();
            if (perUser != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(perUser)!);
                    File.AppendAllText(perUser, line);
                    return;
                }
                catch { /* fall through to the beside-DLL fallback */ }
            }
            try
            {
                File.AppendAllText(BesideAssemblyLogPath(), line);
            }
            catch { /* diagnostics must never throw into the host */ }
        }
    }
}
