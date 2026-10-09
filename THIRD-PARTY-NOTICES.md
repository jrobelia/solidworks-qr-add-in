# Third-party notices

Ticket 1 ships no third-party runtime libraries (no QR/geometry deps yet —
QRCoder (MIT) and Clipper2 (BSL-1.0) arrive in later tickets and must be listed
here when they do).

Development/test-only packages (not shipped with the add-in):

| Package | Version | License |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.0.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |

The add-in references the SolidWorks interop assemblies at build time only
(`EmbedInteropTypes=true` — no interop DLLs are copied or redistributed).
