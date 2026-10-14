using System;
using System.Collections.Generic;
using System.IO;

namespace SolidWorksQrAddIn.Core.Icons
{
    /// <summary>
    /// Decides WHERE the QR icon PNGs are extracted at runtime.
    ///
    /// The installed add-in lives under Program Files — read-only — so
    /// extracting beside the DLL fails there. Extraction therefore prefers a
    /// per-user writable folder (<c>%LOCALAPPDATA%\SolidWorksQrAddIn\icons</c>,
    /// same pattern as diagnostics); the assembly-dir <c>icons\</c> folder
    /// stays in the candidate list as the dev/unpacked fallback. The caller
    /// tries the candidates in order and keeps the first that succeeds —
    /// choosing a directory is pure path logic (unit-testable); the I/O lives
    /// in <see cref="QrToolbarIcons.EnsureExtracted"/>.
    /// </summary>
    public static class QrIconLocations
    {
        /// <summary>Subfolder name under both %LOCALAPPDATA% and the add-in dir.</summary>
        public const string IconsFolderName = "icons";

        /// <summary>Per-user root under %LOCALAPPDATA%.</summary>
        public const string PerUserFolderName = "SolidWorksQrAddIn";

        /// <summary>
        /// Candidate extraction directories, best first: the per-user
        /// LocalAppData folder, then <paramref name="addinDir"/>\icons. The
        /// LocalAppData entry is omitted only when the environment provides
        /// no LocalAppData path at all.
        /// </summary>
        public static IEnumerable<string> ExtractionDirectories(string addinDir)
        {
            string? perUser = PerUserIconsDirectory();
            if (perUser != null)
                yield return perUser;
            yield return Path.Combine(addinDir ?? ".", IconsFolderName);
        }

        /// <summary>
        /// <c>%LOCALAPPDATA%\SolidWorksQrAddIn\icons</c>, or null when the
        /// environment variable is unset/empty (a service account, a test
        /// host that clears the environment).
        /// </summary>
        public static string? PerUserIconsDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
                return null;
            return Path.Combine(local, PerUserFolderName, IconsFolderName);
        }

        // Note: the icon handed to CreateTaskpaneView2 is NOT one of the menu
        // strips — SolidWorks draws task-pane tab icons at ~16-18px and
        // silently ignores a 32px PNG. QrToolbarIcons.EnsurePaneIcon renders
        // the dedicated PaneTabSize file into whichever extraction directory
        // won above; an empty icon path is tolerated (the tab still works,
        // just unadorned), so failure stays best-effort at the call site.
    }
}
