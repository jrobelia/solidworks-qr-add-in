# Spec 11 — extended-settings live gate results (ticket #28)

Harness: `tools/LiveProbe` mode `xsettings` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-11 probe surface.

## Status — ALL 35 CHECKS PASSED

Ran live against the deployed build
(`%LOCALAPPDATA%\SolidWorksQrAddIn`, spec-11 integration tip) on
SolidWorks 2023 SP5 (revision 31.5.0) with no prior `SLDWORKS.exe` running.
Exit code 0; final line `# XSETTINGS ALL CHECKS PASSED`.

The gate fixture the spec calls for ("one interactive create with a
non-default setting"): the **WiFi preset**'s fields composed through the pane
probes (`ssid=GATE-NET-28`, `security=wpa`, `password=probe-pass-28`,
`hidden=true` → literal `WIFI:T:WPA;S:GATE-NET-28;P:probe-pass-28;H:true;`),
**ECC H**, **invert=true**, at the smallest ladder rung the module accepts —
**40 mm** (N=41, M=49 working matrix, 138 contours / 1242 segments, smallest
feature 0.92 mm — well above the 0.05 mm floor). Every live verdict is
asserted against the module's own computed verdict, never a guessed response.

## Results

| # | Check | Result |
|---|-------|--------|
| 1 | Answering build exposes the spec-11 surface | PASS — `WindowStateProbe` reports `preset= ecc= invert=` |
| 2 | `AddinStateProbe` | PASS — `CONNECTED group=20906` |
| 3 | `OpenQrWindowProbe` | PASS — `open=True windows=1` |
| 4 | Fresh session defaults | PASS — `preset=raw ecc=M invert=False` |
| 5 | `SetQrEccProbe("bogus")` | PASS — `ERROR unknown ECC level 'bogus'` — parser refuses before the session |
| 6 | `SetQrContentTypeProbe("bogus")` | PASS — `INVALID InvalidPayload`; `preset=` stays `raw` |
| 7 | `SetQrPresetFieldProbe("nonesuch","x")` | PASS — `INVALID` naming `nonesuch` |
| 8 | `SetQrContentTypeProbe("wifi")` | PASS — `INVALID InvalidPayload` (ssid required), `preset=wifi`, `NO-PICTURE` |
| 9 | `ssid` alone | PASS — `INVALID` — password still required for a secured network |
| 10 | `security=wpa` alone | PASS — `INVALID` — same |
| 11 | `password` completes the trio | PASS — live `VALID cells=29` == module |
| 12 | `SetQrSizeProbe(40)` | PASS — live `VALID cells=29` == module |
| 13 | `hidden=true` | PASS — live `VALID cells=33` == module |
| 14 | `SetQrContentProbe("SW-QR-MVP")` | PASS — manual edit reverts `preset=raw` |
| 15 | re-pick `wifi` | PASS — remembered fields recompose, `VALID cells=33` |
| 16 | `SetQrEccProbe("h")` | PASS — `VALID cells=41` — case-insensitive parse lands H |
| 17 | `SetQrInvertProbe(true)` | PASS — `VALID contours=138` — the module's inverted census |
| 18 | `WindowStateProbe` after settings | PASS — `preset=wifi ecc=H invert=True` **and** `viewEcc=H viewInvert=True` |
| 19 | `PreviewSignatureProbe` | PASS — sha `fd961dc3…` == module inverted+H render; differs from normal polarity `02734246…` |
| 20 | Create on Front Plane | PASS — `OK Sketch1` |
| 21 | Block census | PASS — one instance, Scale=1, at origin; host sketch 0 enumerable segments |
| 22 | Def census | PASS — `defSegs=1250` = 1242 material + 8 construction |
| 23 | Construction squares | PASS — 4 @ 40.000 mm QR square + 4 @ 47.735 mm keep-out, centred on content |
| 24 | **Polarity** | PASS — def material span 47.735 mm == module emitted extent = keep-out extent — inverted field |
| 25 | Module-identical edges | PASS — 1242/1242 def material edges match module lengths, maxLenErr 0.000000 mm |
| 26 | Re-enter committed sketch | PASS — still ONE block instance |
| 27 | Explode | PASS — instance gone; 1242 material segs + 8 construction-flagged host lines |
| 28 | **Module-identical contours** | PASS — every material `ISketchContour` matched a module contour bbox 1:1 (138/138, inverted field included) |
| 29 | `SetQrContentProbe(medium-101-char)` | PASS — `INVALID BelowInsertionLimit` == module at gate settings, `preset=raw` |
| 30 | `SetQrSizeProbe(40)` dense | PASS — `INVALID BelowInsertionLimit` — smallest feature 0.0452 mm vs 0.05 mm floor |
| 31 | Refused create | PASS — `ERROR`, feature tree unchanged, no sketch left open |
| 32 | `SetQrSizeProbe(60)` dense | PASS — `VALID cells=53` — the cleared rung |
| 33 | Dense create | PASS — `OK Sketch2` + full block/def census vs module dense expectation (2062 segs, span 68.979 mm) |
| 34 | Prior sketch untouched | PASS — `[Sketch1, Sketch2]`, no mutation of the first QR |
| 35 | Settings persist | PASS — `ecc=H invert=True` still reported after both creates |

To re-run: close SolidWorks, deploy the current build, then

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe xsettings $deploy
```

Exit code 0 = every check passed; the run ends by closing its document,
unloading the add-in, and `ExitApp` on the spawned instance only.

## Registration pitfall found by this run

The first attempts loaded a **stale** build: an installer-era HKLM
`Classes\CLSID\{clsid}\InprocServer32` CodeBase pointed at
`C:\Program Files\SolidWorks QR Add-In\` and shadowed the HKCU registration.
After uninstalling, activation still failed with `GetAddInObject -> null`
until the leftover **empty `InprocServer32\1.0.0.0` versioned subkey** under
HKCU was populated with the CodeBase — RegAsm `/u` had emptied it, and the
versioned subkey takes precedence over the flat `CodeBase` the
`register-addin-hkcu.ps1` script writes. `register-addin-hkcu.ps1` now writes
that subkey too, so a post-uninstall re-register is self-healing.

## Headless evidence (ran green)

- `dotnet build SolidWorksQrAddIn.sln` — **clean, 0 errors** (2 pre-existing
  CS8602 warnings in `tools/LiveProbe/Program.cs` DimExp code, pre-dating
  this ticket; 1 pre-existing CS8600 in `QrIconLocations.cs` shown in the
  test build only).
- `dotnet test tests\SolidWorksQrAddIn.Tests` — **424 passed, 0 failed**
  (was 403 before this ticket; +21 theory cases in `QrEccLevelsTests`).

Headless additions this ticket:

- `QrEccLevels.TryParse` (`Core/Geometry`) — the case-insensitive L/M/Q/H
  parser the pane and `SetQrEccProbe` share; invalid/empty input rejected.
  `SwAddin.Probes.SetQrEccProbe` now parses through it instead of its
  earlier inline switch — the probe-side text plumbing the live gate drives.
- The `xsettings` mode itself computes every expectation from the module
  (payload composition, per-rung guard verdicts, render signatures), so its
  assertions are module-identical, not hand-derived — and prints them all
  before the occupied-seat refusal so a blocked run is still self-diagnosing.

## Limitations

- The exploded-contour 1:1 check (gate 28) applied the blockqr-verified
  explode route to **inverted** output for the first time — the live run
  confirmed `GetSketchContours` enumerates the inverted field's
  hole/island loops identically (138/138 matched).
- `SetQrSizeProbe` at the dense refused rung re-set the size the session
  already held (the refused rung equals the gate size) — the verdict was
  still asserted against the module, but it was not a distinct edit.
- The dense create's feature-count check asserts no *new* feature from the
  refused create; it does not re-enter the first sketch a second time.
