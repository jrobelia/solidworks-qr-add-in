using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorksQrAddIn.Core.Deployment;
using SolidWorksQrAddIn.Core.Registration;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket-7 headless checks for the deployment plan: the single source of
    /// truth for what the deployment folder contains. The plan is data plus a
    /// folder verifier — pack-deploy.ps1 materializes it and
    /// verify-deploy.ps1 reports it, so these tests pin the contract both
    /// scripts implement.
    /// </summary>
    public class DeploymentPlanTests
    {
        private const string InvenTreeClsid = "A1B2C3D4-E5F6-7890-ABCD-EF1234567890";

        // ---------------- the runtime payload ----------------

        [Fact]
        public void Runtime_binaries_are_the_addins_own_assemblies_plus_the_two_pinned_dependencies()
        {
            Assert.Equal(
                new[]
                {
                    "SolidWorksQrAddIn.dll",
                    "SolidWorksQrAddIn.Core.dll",
                    "QRCoder.dll",
                    "Clipper2Lib.dll",
                },
                DeploymentPlan.RuntimeBinaries);
        }

        [Fact]
        public void Runtime_binaries_never_list_SolidWorks_interop_or_dev_tooling()
        {
            foreach (var name in DeploymentPlan.RuntimeBinaries)
            {
                Assert.EndsWith(".dll", name, StringComparison.OrdinalIgnoreCase);
                Assert.False(name.StartsWith("SolidWorks.Interop.", StringComparison.OrdinalIgnoreCase),
                    $"interop assemblies are embedded, never shipped: {name}");
            }
        }

        // ---------------- shipped non-binary files ----------------

        [Fact]
        public void Shipped_files_cover_notices_install_doc_licenses_and_registration_scripts()
        {
            var deployed = DeploymentPlan.ShippedFiles.Select(f => f.DeployedPath).ToList();

            // License + attribution notices ship beside the binaries.
            Assert.Contains("THIRD-PARTY-NOTICES.md", deployed);
            Assert.Contains(deployed, p => p.StartsWith("licenses/", StringComparison.Ordinal));
            Assert.Contains(deployed,
                p => p.IndexOf("QRCoder", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(deployed,
                p => p.IndexOf("Clipper2", StringComparison.OrdinalIgnoreCase) >= 0);

            // The install procedure ships with the folder it describes.
            Assert.Contains("DEPLOYMENT.md", deployed);
            Assert.Contains("ACCEPTANCE.md", deployed);

            // The per-user registration scripts ship — the user runs them in place.
            Assert.Contains(deployed, p => p.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Every_shipped_file_source_exists_in_the_repo()
        {
            var root = RepoRoot();
            foreach (var file in DeploymentPlan.ShippedFiles)
                Assert.True(File.Exists(Path.Combine(root, file.Source.Replace('/', '\\'))),
                    $"planned source missing from the repo: {file.Source}");
        }

        [Fact]
        public void No_planned_file_is_forbidden()
        {
            var all = DeploymentPlan.RuntimeBinaries
                .Concat(DeploymentPlan.ShippedFiles.Select(f => f.DeployedPath));
            foreach (var path in all)
            {
                var name = Path.GetFileName(path);
                foreach (var prefix in DeploymentPlan.ForbiddenFilePrefixes)
                    Assert.False(name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                        $"planned file collides with forbidden prefix '{prefix}': {path}");
            }
        }

        // ---------------- folder verification ----------------

        [Fact]
        public void VerifyFolder_reports_a_missing_folder()
        {
            var problems = DeploymentPlan.VerifyFolder(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            Assert.Contains(problems, p => p.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void VerifyFolder_on_an_empty_folder_reports_every_required_file()
        {
            var dir = NewTempDir();
            try
            {
                var problems = DeploymentPlan.VerifyFolder(dir);
                foreach (var name in DeploymentPlan.RuntimeBinaries)
                    Assert.Contains(problems, p => p.Contains(name));
                Assert.Contains(problems, p => p.Contains("THIRD-PARTY-NOTICES.md"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void VerifyFolder_accepts_a_complete_deployment_folder()
        {
            var dir = NewTempDir();
            try
            {
                PopulateComplete(dir);
                Assert.Empty(DeploymentPlan.VerifyFolder(dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void VerifyFolder_flags_SolidWorks_interop_dlls_as_forbidden()
        {
            var dir = NewTempDir();
            try
            {
                PopulateComplete(dir);
                File.WriteAllText(Path.Combine(dir, "SolidWorks.Interop.sldworks.dll"), "x");
                var problems = DeploymentPlan.VerifyFolder(dir);
                Assert.Contains(problems,
                    p => p.Contains("SolidWorks.Interop.sldworks.dll"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void VerifyFolder_flags_dev_tooling_and_unexpected_binaries()
        {
            var dir = NewTempDir();
            try
            {
                PopulateComplete(dir);
                File.WriteAllText(Path.Combine(dir, "SolidWorksQrAddIn.LiveProbe.exe"), "x");
                File.WriteAllText(Path.Combine(dir, "SomethingElse.dll"), "x");
                var problems = DeploymentPlan.VerifyFolder(dir);
                Assert.Contains(problems, p => p.Contains("SolidWorksQrAddIn.LiveProbe.exe"));
                Assert.Contains(problems, p => p.Contains("SomethingElse.dll"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---------------- registration scripts correspond to this add-in ----------------

        [Fact]
        public void Shipped_registration_scripts_target_this_addins_identity_and_never_InvenTree()
        {
            var root = RepoRoot();
            var scripts = DeploymentPlan.ShippedFiles
                .Where(f => f.DeployedPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(scripts);
            foreach (var script in scripts)
            {
                var text = File.ReadAllText(
                    Path.Combine(root, script.Source.Replace('/', '\\')));
                Assert.Contains(AddinIdentity.Clsid, text);
                Assert.DoesNotContain(InvenTreeClsid, text);
            }
        }

        [Fact]
        public void The_deployment_doc_names_this_addins_clsid_and_the_shipped_scripts()
        {
            var root = RepoRoot();
            var doc = DeploymentPlan.ShippedFiles
                .First(f => f.DeployedPath == "DEPLOYMENT.md");
            var text = File.ReadAllText(Path.Combine(root, doc.Source.Replace('/', '\\')));
            Assert.Contains(AddinIdentity.Clsid, text);
            foreach (var script in DeploymentPlan.ShippedFiles
                         .Where(f => f.DeployedPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)))
                Assert.Contains(script.DeployedPath, text);
        }

        // ---------------- helpers ----------------

        /// <summary>The repo root = nearest ancestor dir holding the solution file.</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SolidWorksQrAddIn.sln")))
                dir = dir.Parent;
            Assert.True(dir != null,
                "could not locate SolidWorksQrAddIn.sln above the test output directory");
            return dir!.FullName;
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "qr-deploy-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Creates every planned file as a stub so only content is absent, not names.</summary>
        private static void PopulateComplete(string dir)
        {
            foreach (var name in DeploymentPlan.RuntimeBinaries)
                File.WriteAllText(Path.Combine(dir, name), "stub");
            foreach (var file in DeploymentPlan.ShippedFiles)
            {
                var target = Path.Combine(dir, file.DeployedPath.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "stub");
            }
        }
    }
}
