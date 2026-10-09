using System;
using System.IO;
using System.Reflection;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// Minimal diagnostics for the in-process add-in. Writes one line per event
    /// to SolidWorksQrAddIn.debug.log next to the deployed DLL. Never throws —
    /// a logging failure must not break a COM host call.
    /// </summary>
    internal static class Diag
    {
        public static void Log(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? ".";
                File.AppendAllText(
                    Path.Combine(dir, "SolidWorksQrAddIn.debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { /* diagnostics must never throw into the host */ }
        }
    }
}
