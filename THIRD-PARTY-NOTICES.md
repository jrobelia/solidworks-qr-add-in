<!-- Installer/LICENSE.txt appends a plain-text copy of this file below the
MIT license for the Inno license page (the installer cannot render
markdown) — keep it in sync when this file changes. -->

# Third-party notices

Runtime libraries shipped with the add-in (deployed beside SolidWorksQrAddIn.dll):

| Package | Version | License | Selected NuGet asset (net48) | Used for |
|---|---|---|---|---|
| QRCoder | 1.8.1 (pinned) | MIT | `lib/net40/QRCoder.dll` — no package dependencies | QR encoding → cell matrix (`ModuleMatrix`) |
| Clipper2 | 2.0.0 (pinned) | BSL-1.0 | `lib/netstandard2.0/Clipper2Lib.dll` — no package dependencies | Integer polygon union + nested contours (PolyTree64) |

Full license texts ship in the deployment folder under `licenses\` (and live
in this repo at `licenses/`):

- `licenses/QRCoder-LICENSE.txt` — MIT License, © 2013–2025 Raffael Herrmann,
  © 2024–2025 Shane Krueger.
  Upstream: https://github.com/Shane32/QRCoder/blob/master/LICENSE
- `licenses/Clipper2-LICENSE.txt` — Boost Software License 1.0.
  Upstream: https://github.com/AngusJohnson/Clipper2/blob/main/License.txt

Selected-asset note: NuGet resolves `lib/net40/QRCoder.dll` for the net48
target and `lib/netstandard2.0/Clipper2Lib.dll` (net48 supports .NET Standard
2.0). The resolved versions and package content hashes are locked in the
committed `packages.lock.json` files (`RestorePackagesWithLockFile`) and
verified during restore with `dotnet restore --locked-mode` — a drifted
project file fails the restore/pack rather than pulling a different asset.
No SolidWorks assemblies are redistributed: the add-in references the
installed SDK interop assemblies with `EmbedInteropTypes=true`
at build time only — nothing is copied or shipped (RESEARCH.md §2).

Development/test-only packages (not shipped with the add-in):

| Package | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |
