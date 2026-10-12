# Spec 11 — extended-settings live gate results (ticket #28)

Harness: `tools/LiveProbe` mode `xsettings` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-11 probe surface.

## Status at commit — BLOCKED on an occupied seat

`tasklist /FI "IMAGENAME eq SLDWORKS.exe"` showed `SLDWORKS.exe` PID 23736 on
an interactive session (`RDP-Tcp#0`, session 2) throughout the ticket's work.
The seat was left untouched — the harness refuses to attach to or mutate an
occupied session, and the process was never killed. **No live check below has
run; every SolidWorks-side gate in this file is UNRUN, not passed.** The mode
was exercised to its refusal point: all module-side expectations computed,
then the occupied-seat refusal printed and exited 1 without touching
SolidWorks.

Refusal-run transcript (the whole module half still executes before refusing):

```
# spec-11 live gates — WiFi fields -> 'WIFI:T:WPA;S:GATE-NET-28;P:probe-pass-28;H:true;'
#   interim (hidden default) -> 'WIFI:T:WPA;S:GATE-NET-28;P:probe-pass-28;;'
# gate request: 40mm ECC=H invert=true N=41 M=49 contours=138 segs=1242 keepOut=47.735mm minEdge=0.9157mm minGap=0.9157mm
# interim verdicts: interim@M/25mm -> VALID cells=29; interim@M/40mm -> VALID cells=29; wifi@M/40mm -> VALID cells=33
# rung table 'medium' (101 chars @H+invert): 25mm:INVALID BelowInsertionLimit 40mm:INVALID BelowInsertionLimit 60mm:VALID cells=53 80mm:VALID cells=53 120mm:VALID cells=53 160mm:VALID cells=53
# dense payload: refused <=40mm (BelowInsertionLimit), accepted 60mm (N=53); at gate size -> INVALID BelowInsertionLimit
# FATAL: extended-settings verification requires SolidWorks closed — a SLDWORKS.exe process is already running. Refusing to attach; the probe never mutates or kills an occupied seat.
EXIT=1
```

## What the live run checks (deferred — none have run)

The gate fixture the spec calls for ("one interactive create with a
non-default setting"): the **WiFi preset**'s fields composed through the pane
probes (`ssid=GATE-NET-28`, `security=wpa`, `password=probe-pass-28`,
`hidden=true` → literal `WIFI:T:WPA;S:GATE-NET-28;P:probe-pass-28;H:true;`),
**ECC H**, **invert=true**, at the smallest ladder rung the module accepts —
**40 mm** (N=41, M=49 working matrix, 138 contours / 1242 segments, smallest
feature 0.92 mm — well above the 0.05 mm floor). Every live verdict below is
asserted against the module's own computed verdict, never a guessed response.

### Deferred check list

| # | Check | Expected |
|---|-------|----------|
| 1 | Answering build exposes the spec-11 surface | `WindowStateProbe` contains `preset=`/`ecc=`/`invert=` (stale builds are unloaded and the deployed DLL loaded first) |
| 2 | `AddinStateProbe` | `CONNECTED` |
| 3 | `OpenQrWindowProbe` | `open=True windows=1` |
| 4 | Fresh session defaults | `preset=raw ecc=M invert=False` |
| 5 | `SetQrEccProbe("bogus")` | `ERROR unknown ECC level` — parser refuses before the session |
| 6 | `SetQrContentTypeProbe("bogus")` | `INVALID InvalidPayload`; `preset=` stays `raw` |
| 7 | `SetQrPresetFieldProbe("nonesuch","x")` | `INVALID` naming `nonesuch` |
| 8 | `SetQrContentTypeProbe("wifi")` | `INVALID InvalidPayload` (ssid required), `preset=wifi`, `NO-PICTURE` |
| 9 | `ssid` alone | `INVALID` — password still required for a secured network |
| 10 | `security=wpa` alone | `INVALID` — same |
| 11 | `password` completes the trio | live verdict == module verdict at current size (`VALID cells=29` here) |
| 12 | `SetQrSizeProbe(40)` | live verdict == module (`VALID cells=29`) |
| 13 | `hidden=true` | live verdict == module (`VALID cells=33`) |
| 14 | `SetQrContentProbe("SW-QR-MVP")` | manual edit reverts `preset=raw` |
| 15 | re-pick `wifi` | remembered fields recompose — live verdict == module |
| 16 | `SetQrEccProbe("h")` | `VALID cells=41` — the case-insensitive parser lands H live |
| 17 | `SetQrInvertProbe(true)` | `VALID contours=138` — the module's inverted census |
| 18 | `WindowStateProbe` after settings | `preset=wifi ecc=H invert=True` **and** `viewEcc=H viewInvert=True` (rendered controls follow the session) |
| 19 | `PreviewSignatureProbe` | sha equals the module's inverted+H render; differs from the same request at normal polarity |
| 20 | Create on Front Plane | `OK` |
| 21 | Block census | one instance, Scale=1, at origin; host sketch has 0 enumerable segments |
| 22 | Def census | `defSegs = 1242+8`, 8 construction-flagged |
| 23 | Construction squares | 4 segs @ QR-square side (40.000 mm), 4 @ keep-out side (47.735 mm), centred on content |
| 24 | **Polarity** | def material span == module emitted extent = keep-out extent (~47.7 mm) — inverted field, distinctly wider than the 40 mm QR square |
| 25 | Module-identical edges | sorted def material lengths == module contour-edge lengths 1:1 |
| 26 | Re-enter committed sketch | still ONE block instance |
| 27 | Explode | instance gone; 1242 material segs + 8 construction-flagged host lines |
| 28 | **Module-identical contours** | every material `ISketchContour` matches a module contour bbox 1:1 at ≤1e-9 m (positions, real sketch coords) |
| 29 | `SetQrContentProbe(medium-101-char)` | live verdict == module at gate settings (`INVALID BelowInsertionLimit`), `preset=raw` |
| 30 | `SetQrSizeProbe(40)` dense | `INVALID BelowInsertionLimit` — matches the module's rung table |
| 31 | Refused create | `ERROR`, feature tree unchanged, no sketch left open |
| 32 | `SetQrSizeProbe(60)` dense | `VALID cells=53` — the cleared rung |
| 33 | Dense create | `OK` + full block census vs module dense expectation |
| 34 | Prior sketch untouched | first QR feature still present; exactly one new feature added |
| 35 | Settings persist | `ecc=H invert=True` still reported after both creates |

To run: close SolidWorks, deploy the current build, then

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe xsettings $deploy
```

Exit code 0 = every check passed; the run ends by closing its document,
unloading the add-in, and `ExitApp` on the spawned instance only.

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

- **No live evidence exists yet.** The occupied seat made every
  SolidWorks-side check above unrunnable; this file records intent and the
  refusal path, not results.
- The exploded-contour 1:1 check (gate 28) applies the blockqr-verified
  explode route to **inverted** output for the first time — the module's
  expectation is computed, but whether `GetSketchContours` enumerates the
  inverted field's hole/island loops identically is exactly what the live
  run must establish. A failure there is evidence, not a waived check.
- `SetQrSizeProbe` at the dense refused rung may re-set the size the session
  already holds (when the refused rung equals the gate size) — the verdict
  is still asserted against the module, but it is not a distinct edit.
- The dense create's feature-count check asserts no *new* feature from the
  refused create; it does not re-enter the first sketch a second time.
