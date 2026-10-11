using System;
using System.IO;
using System.Linq;
using SolidWorksQrAddIn.Core.Icons;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The icon extraction-location seam: which directories the add-in tries
    /// (per-user LocalAppData first — an Inno-installed DLL under Program
    /// Files is read-only). The task-pane tab icon itself is a dedicated
    /// 18px render covered by QrToolbarIconsTests.EnsurePaneIcon_*.
    /// </summary>
    public class QrIconLocationsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "qr-iconloc-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }

        [Fact]
        public void ExtractionDirectories_prefers_per_user_LocalAppData()
        {
            string addinDir = Path.Combine(_dir, "addin");
            var candidates = QrIconLocations.ExtractionDirectories(addinDir).ToList();

            // The last-resort candidate is always beside the DLL.
            Assert.Equal(Path.Combine(addinDir, "icons"), candidates[candidates.Count - 1]);

            // When the environment offers a LocalAppData path it leads.
            string? perUser = QrIconLocations.PerUserIconsDirectory();
            if (perUser == null)
            {
                Assert.Single(candidates);
            }
            else
            {
                Assert.Equal(2, candidates.Count);
                Assert.Equal(perUser, candidates[0]);
                Assert.Equal(
                    Path.Combine("SolidWorksQrAddIn", "icons"),
                    perUser.Substring(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData).Length)
                        .TrimStart(Path.DirectorySeparatorChar));
            }
        }

    }
}
