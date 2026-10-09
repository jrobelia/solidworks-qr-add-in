# Third-party notices

Runtime libraries shipped with the add-in (deployed beside SolidWorksQrAddIn.dll):

| Package | Version | License | Used for |
|---|---|---|---|
| QRCoder | 1.8.0 | MIT | QR encoding → cell matrix (`ModuleMatrix`) |
| Clipper2 | 2.0.0 | BSL-1.0 | Integer polygon union + nested contours (PolyTree64) |

QRCoder license: https://github.com/Shane32/QRCoder/blob/master/LICENSE
Clipper2 license: https://github.com/AngusJohnson/Clipper2/blob/main/License.txt
(Boost Software License 1.0)

Development/test-only packages (not shipped with the add-in):

| Package | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |

The add-in references the SolidWorks interop assemblies at build time only
(`EmbedInteropTypes=true` — no interop DLLs are copied or redistributed).
