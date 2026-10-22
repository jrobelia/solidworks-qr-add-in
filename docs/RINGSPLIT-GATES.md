# Spec 51 — open nested rings live gate results (ticket #55)

Harness: `tools/LiveProbe` mode `ringsplit` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-51 probe surface
(`SetQrRingSplitProbe`, `ringsplit=` in `WindowStateProbe`).

## Status — ALL CHECKS PASSED (0 failures, 1 documented limitation)

Ran live against the deployed build (`%LOCALAPPDATA%\SolidWorksQrAddIn`)
on SolidWorks 2023 SP5 (revision 31.5.0) with no prior `SLDWORKS.exe`
running — **138 checks, 0 failures**, exit code 0, final line
`# RINGSPLIT ALL CHECKS PASSED`. The deployed add-in's HKCU registration
(`InprocServer32`/`CodeBase`) had been stripped by an earlier installer
test and was restored with the installed folder's
`register-addin-hkcu.ps1` (per-user, no admin) before the run;
`GetAddInObject` then resolved the add-in after `LoadAddIn` and answered
through the IDispatch fallback.

**Re-verified 2026-10-22 on the post-review-fix build** (merge `c0152dd`,
deployed via `scripts\sync-dev-deploy.ps1`): the same 138 checks, 0
failures, exit 0 — `# RINGSPLIT ALL CHECKS PASSED`. The review fixes were
behaviour-preserving for the gates; this run covers the shipped artefact.

**Re-verified again 2026-10-22 on the inner-edge locus build** (merge
`93d4ffa` — user-requested change moving the finder slits to the ring
corners, hugging the void's inner edge): first run failed gate 1's
preview-parity check — the check over-asserted "split signature must
differ from unsplit", but the 0.05 mm slit is sub-pixel at the 240 px
preview raster so the signatures legitimately coincide. The check was
corrected to assert only the parity contract (preview == module render);
re-run: 138 checks, 0 failures, exit 0 — `# RINGSPLIT ALL CHECKS PASSED`.

**Re-verified again 2026-10-22 on the thin quiet-zone egress build**
(`c6a85f2` — user-requested change replacing the inverted drop path's
whole-cell egress leg through the quiet zone with the thin derived-width
corridor): 138 checks, 0 failures, exit 0 — `# RINGSPLIT ALL CHECKS
PASSED`. The inverted-split census self-adapted (segs 266 → 276) since
the corridor adds walls instead of removing whole cells.

Module expectations printed before attach (the refusal-tolerant header):

- baseline `SW-QR-MVP` @ 25 mm / 3% ECC M: contours=27 segs=252
  regions=8 holes=16 islands=3 islandBearing=3 material=11
  sketchRegions=27 sig=1d3476fed3c5
- split twin: contours=24 segs=264 regions=11 holes=13 islands=0
  material=11 sketchRegions=24 opened=3 dropped=0 channel=0.0501 mm
  (3 wall pairs) sig=a5fa39bddd06
- dense 200-char `K…` payload @ 40 mm / 10%: unsplit bearing=10
  islands=10 → split regions=20 segs=1902 opened=10 dropped=12
  channel=0.0500 mm (3 pairs) material=20 sketchRegions=201
  sig=2c34f54946be
- inverted split twin: contours=28 segs=266 regions=6 sketchRegions=28
- circle twin (inert): dots=228 opened=0 — the ECC gate is inert under
  circle too (ECC L verdict: VALID)
- expected refusals: split@ECC=L → `INVALID InvalidEccLevel`;
  split@10 mm → `INVALID BelowInsertionLimit`

## Gate summary

| Gate | What it proves | Result |
|---|---|---|
| pre-flight | `LoadAddIn` the deployed DLL, probe surface discriminator (`ringsplit=` in `WindowStateProbe`), connect, part template | PASS (4/4) |
| 1 plumbing + preview | pane opens through the command entry once (refocus, no duplicate); option defaults OFF with the control visible; toggle lands pre-content under the `INVALID EmptyContent` contract; option-off census is the historical baseline; `PreviewSignatureProbe` renders the module's UNSPLIT sketch, then exactly the module's SPLIT sketch (preview/create parity), and toggling off restores the unsplit picture byte-identically | PASS (13/13) |
| 2 ECC refusal verdict | ECC L + option → `INVALID InvalidEccLevel` naming `EccLevel`, refused request carries no request and no picture; ECC M restores the module's split verdict | PASS (3/3) |
| 3 circle inert | option stored under Circle emits exactly the module's dot plan; flag stored while the control hides; ECC L under circle stays VALID (dots spend no correction budget); back on square the stored flag applies | PASS (5/5) |
| 4 inverted split | invert + split reproduces the module's opened-pocket census (contours=28); polarity back restores the split census | PASS (2/2) |
| 5 option-on create + censuses | pane-path create lands ONE block instance at Scale 1 on the origin, zero enumerable host segments; def = 264 material + 8 construction, squares at real size centred on content, edges 1:1 by length vs module; def carries exactly 3 channel wall-pairs at the derived 0.0501 mm width; exploded sketch: 264 material segs + 8 construction, every exploded contour bbox-matched 1:1, ISketchRegion total == nesting census (24 = 11 material + 13 void), channels at the module mouth loci, all 11 material regions object-select via `ISketchRegion.Select`; extrudability proof — backing plate boss, QR boss + shallow cut from the picked regions | PASS (41/41) |
| 6 named undo | `UndoRedoQrProbe` on its own document: API undo presses remove the split create exactly, prior user work survives, the toggle reinserts it | PASS (3/3) |
| 7 refusals before mutation | 10 mm split → `INVALID BelowInsertionLimit`, create refuses, feature tree + sketch-edit state untouched; ECC L → `INVALID InvalidEccLevel`, create refuses, no partial sketch; both fields restore cleanly | PASS (8/8) |
| 8 dense cell-drop | 200-char payload @ 40 mm/10%: session verdict matches module (opened=10 dropped=12, segs=1902); same block + exploded census suite at scale — def 1902+8, 3 channel pairs @ 0.0500 mm, ISketchRegion census 201 (20 material + 181 void), all 20 material regions object-select | PASS (30/30) |
| 9 option-off regression | historical 25 mm/3% verdict + signature return with the option off; plain create carries the closed `ring → hole → island` census (27 contours, islandBearing=3 stays closed), no channel pairs — byte-identical topology; nested material regions still object-select; all gate sketches co-exist; the option stays off after the regression create | PASS (29/29) |

## What the first live run found (1 failure → root cause)

The 2026-10-21 first run failed exactly one of 138 checks:

- **HARNESS — "all three gate sketches co-exist untouched".** The check
  asserted `sketchCount == feats0 + 3`, but the extrudability proof in
  gate 5 legitimately adds the backing-plate sketch — the document holds
  the QR sketch + plate sketch from gate 5 plus one create each from
  gates 8–9. The check now snapshots the feature count before the
  regression create and asserts `+1` plus all three QR sketch names
  still present. Re-run: all green.

## Residual limitations

- Automatic profile selection is interactive-only — SolidWorks does not
  expose "click → auto-profile → extrude" to a headless driver. The
  gate's proxy route is the exploded sketch census plus
  `ISketchRegion.Select` object-selection of every material region
  (which feeds real boss + cut extrusions in gate 5). The human
  click-and-extrude row is `docs/ACCEPTANCE.md` **D21**.
- `MaterialPoints`-style bounding-box-centre picks are never used for
  region selection — a split finder ring is C-shaped, so its bbox centre
  lands in the void. `SelectQrMaterialRegions` enumerates region
  objects only.
- Channel mouths are compared at their sketch-space loci on the exploded
  host sketch; the block definition's sketch reports an offset frame, so
  only the frame-independent channel WIDTHS are compared there
  (`mouthsComparable: false`).

## Headless evidence

- `dotnet build SolidWorksQrAddIn.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test tests\SolidWorksQrAddIn.Tests` — **697 passed** (this
  branch adds `QrNestingCensusTests` — 8 cases incl. the dense-payload
  census where module-aligned drops may merge adjacent voids, so the
  honest invariant is `split holes ≤ plain holes` with zero
  island-bearing holes — and `QrChannelGapsTests` — 7 cases covering
  wall-pair detection, width measurement and the finder-mouth locus).
- New pure helpers under test: `QrNestingCensus` (region/hole/island
  topology counts → `SketchRegionCount`) and `QrChannelGaps`
  (facing-wall pair detection → channel width + mouth locus).

To re-run: close SolidWorks, `scripts\sync-dev-deploy.ps1`, then

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe ringsplit $deploy
```
