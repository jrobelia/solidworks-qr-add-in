using System;
using System.Collections.Generic;
using System.IO;

namespace SolidWorksQrAddIn.Core.Icons
{
    /// <summary>
    /// Decides WHERE the QR icon PNGs are extracted at runtime, and which
    /// extracted file dresses the task-pane tab.
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
            string perUser = PerUserIconsDirectory();
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

        /// <summary>
        /// Picks the PNG handed to <c>CreateTaskpaneView2</c> from the
        /// extracted set: the 32px strip when present (the task-pane tab's
        /// size class), otherwise the first file — but only a path that
        /// actually exists on disk, and empty when the extraction produced
        /// nothing usable (SolidWorks tolerates an empty icon path: the tab
        /// still works, just unadorned).
        /// </summary>
        public static string PickPaneIcon(IReadOnlyList<string> extractedPaths)
        {
            if (extractedPaths == null || extractedPaths.Count == 0)
                return string.Empty;
            string preferred = extractedPaths.Count > 1 ? extractedPaths[1] : extractedPaths[0];
            return File.Exists(preferred) ? preferred : string.Empty;
        }
    }
}
