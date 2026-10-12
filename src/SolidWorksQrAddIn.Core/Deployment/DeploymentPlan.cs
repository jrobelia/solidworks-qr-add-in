using System;
using System.Collections.Generic;
using System.IO;

namespace SolidWorksQrAddIn.Core.Deployment
{
    /// <summary>
    /// One non-binary file the deployment folder carries: <see cref="Source"/>
    /// is the repo-relative path the pack script copies FROM;
    /// <see cref="DeployedPath"/> is where it lands inside the deployment
    /// folder (forward slashes; may include a subfolder).
    /// </summary>
    public sealed class DeploymentFile
    {
        public DeploymentFile(string source, string deployedPath)
        {
            Source = source;
            DeployedPath = deployedPath;
        }

        /// <summary>Repo-relative source path (forward slashes).</summary>
        public string Source { get; }

        /// <summary>Path inside the deployment folder (forward slashes).</summary>
        public string DeployedPath { get; }
    }

    /// <summary>
    /// The deployment folder's required contents as pure data — the single
    /// source of truth. `scripts/pack-deploy.ps1` materializes it from a
    /// Release build, `scripts/verify-deploy.ps1` reports
    /// <see cref="VerifyFolder"/>, and the headless suite pins the contract.
    ///
    /// What belongs: the add-in's own assemblies plus its pinned third-party
    /// runtime dependencies (QRCoder MIT, Clipper2 BSL-1.0) and the required
    /// license notices. What never belongs: SolidWorks interop DLLs (interop
    /// types are embedded per docs/RESEARCH.md §2 — redist.txt permits
    /// redistribution but embedding makes it unnecessary), dev tooling
    /// (LiveProbe, test assemblies), and anything else unexpected.
    /// </summary>
    public static class DeploymentPlan
    {
        /// <summary>
        /// Binaries copied from the add-in's Release build output, in copy
        /// order — own assemblies first, then the two pinned dependencies.
        /// </summary>
        public static IReadOnlyList<string> RuntimeBinaries { get; } = new[]
        {
            "SolidWorksQrAddIn.dll",
            "SolidWorksQrAddIn.Core.dll",
            "QRCoder.dll",
            "Clipper2Lib.dll",
        };

        /// <summary>
        /// The add-in's own license, third-party notices, the
        /// install/acceptance docs, the registration scripts, and the full
        /// dependency license texts shipped beside the binaries.
        /// </summary>
        public static IReadOnlyList<DeploymentFile> ShippedFiles { get; } = new[]
        {
            new DeploymentFile("LICENSE", "LICENSE.txt"),
            new DeploymentFile("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
            new DeploymentFile("docs/DEPLOYMENT.md", "DEPLOYMENT.md"),
            new DeploymentFile("docs/ACCEPTANCE.md", "ACCEPTANCE.md"),
            new DeploymentFile("scripts/register-addin-hkcu.ps1", "register-addin-hkcu.ps1"),
            new DeploymentFile("scripts/unregister-addin-hkcu.ps1", "unregister-addin-hkcu.ps1"),
            new DeploymentFile("licenses/QRCoder-LICENSE.txt", "licenses/QRCoder-LICENSE.txt"),
            new DeploymentFile("licenses/Clipper2-LICENSE.txt", "licenses/Clipper2-LICENSE.txt"),
        };

        /// <summary>
        /// File-name prefixes that must never appear in the deployment folder:
        /// embedded SolidWorks interop and dev/test tooling.
        /// </summary>
        public static IReadOnlyList<string> ForbiddenFilePrefixes { get; } = new[]
        {
            "SolidWorks.Interop.",
            "SolidWorksQrAddIn.LiveProbe",
            "SolidWorksQrAddIn.Tests",
            "testhost",
            "xunit",
        };

        /// <summary>
        /// Every file the folder is expected to hold (deployed-path form).
        /// </summary>
        public static IEnumerable<string> ExpectedDeployedPaths()
        {
            foreach (var name in RuntimeBinaries)
                yield return name;
            foreach (var file in ShippedFiles)
                yield return file.DeployedPath;
        }

        /// <summary>
        /// Checks a deployment folder against the plan: every required file
        /// present, no forbidden names anywhere inside it, and no unexpected
        /// root-level binaries. Returns the problems found; an empty list
        /// means the folder is deployable. Read-only — never writes into or
        /// deletes from the folder (safe on the REGISTERED folder too).
        /// </summary>
        public static IReadOnlyList<string> VerifyFolder(string deploymentDir)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(deploymentDir) || !Directory.Exists(deploymentDir))
            {
                problems.Add($"deployment folder not found: {deploymentDir ?? "(null)"}");
                return problems;
            }

            var root = Path.GetFullPath(deploymentDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var deployedPath in ExpectedDeployedPaths())
            {
                var full = Path.Combine(root, deployedPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                    problems.Add($"missing required file: {deployedPath}");
            }

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                var rel = path.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                foreach (var prefix in ForbiddenFilePrefixes)
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"forbidden file deployed: {rel}");

                // Any root-level binary must be one of the planned runtime
                // binaries — catches stray dependencies and interop copies.
                var isRootBinary =
                    string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) &&
                    (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (isRootBinary)
                {
                    bool known = false;
                    foreach (var expected in RuntimeBinaries)
                        if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase))
                            known = true;
                    if (!known)
                        problems.Add($"unexpected binary in deployment folder: {name}");
                }
            }

            return problems;
        }
    }
}
