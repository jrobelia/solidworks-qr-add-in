# Spec 21 — SW Properties live gate results (ticket #46)

Harness: `tools/LiveProbe` mode `swprops` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-21 probe surface
(`SwPropertiesProbe`, `SwPropertyResolveProbe`, the `swprops` content type
and the `viewTemplateNames`/`viewResolved` pane echoes from tickets
#44/#45).

## Status — ALL 129 CHECKS PASSED (exit 0)

Ran live against the deployed build
(`%LOCALAPPDATA%\SolidWorksQrAddIn\SolidWorksQrAddIn.dll`,
the Release build of this branch's review-fix commit) on
SolidWorks 2023 SP5 (revision 31.5.0, invisible spawned instance,
Windows NT 6.2.9200.0), 2026-10-21, with no prior `SLDWORKS.exe`
running and none left behind. Final line `ALL CHECKS PASSED` —
129 checks including gate 9's new destroy-notification check (the
original 128-check run of the #46 build passed on the same baseline,
sha256 `9438cec0…`).

The fixture the harness writes into a session-free part (via
`CustomPropertyManager.Add3`, `swCustomInfoText`,
`swCustomPropertyDeleteAndAdd`):

- Document store `""`: `PartNo=PN-0042`, `QrDocProp=DOC-VALUE-1`,
  `QrShared=doc-level`, `QrEmpty=` (present-but-blank),
  `QrSpaced=` (three spaces), `Qr Custom Label=LBL-7` (name with spaces).
- Seed configuration `Default`: `Revision=REV-A`, `QrShared=cfg-level`,
  `QrCfgProp=CFG-VALUE-1`.
- Second configuration `QrCfgB` (added by `AddConfiguration2`, which —
  verified live, `Delete2 rc=0` — inherits the active configuration's
  specific properties, so the fixture deletes the cloned `QrCfgProp`):
  `Revision=REV-B`, `QrShared=cfg-B-level`, `QrCfgOnlyB=CFG-B-ONLY`.
- Second document `Part2`: `PartNo=PN-7777`, `Revision=D1`,
  `QrDocBOnly=B-ONLY`.

Gate template `PART-$PRP:"PartNo"-$PRP:"Revision"` resolves to
`PART-PN-0042-REV-A` (cfgA), `PART-PN-0042-REV-B` (QrCfgB) and
`PART-PN-7777-D1` (Part2) — three distinct literals with distinct QR
matrices (segs 204/216/236, signatures `c4aee6aa…`, `22c77464…`,
`36117d63…`), so a stale resolved value cannot pass the
module-identical checks.

## Results

| Gate | Check | Result |
|------|-------|--------|
| — | Answering build exposes the spec-21 surface | PASS — `SwPropertiesProbe` discriminated the deployed build (`SWPROPS kind=None names=0` before any doc); `LoadAddIn` returned 0, `GetAddInObject` handed the IDispatch probe |
| 1 | Fixture part + property writes | PASS — `NewDocument` on the default part template (`G:\Shared drives\OA Templates\…\OA Part [mm].prtdot`); all 15 `Add3` calls returned `AddedOrChanged`; `AddConfiguration2` created `QrCfgB`; `Delete2` removed the inherited `QrCfgProp` (rc=0 — the clone behaviour itself verified); seed configuration re-activated and confirmed |
| 2 | Enumerated names echo | PASS — `SWPROPS kind=part doc='Part1' config='Default' names=8`, every fixture name present, `QrShared` deduplicated, configuration-store names precede document-only names, no `SW-*` built-ins |
| 3 | Per-name resolve verdicts | PASS — 10 verdicts exact: doc value verbatim, config value verbatim, config shadows doc (`QrShared` → `cfg-level`), name-with-spaces verbatim, whitespace-only value resolves (not empty), `EMPTY` distinct from `MISSING`, inactive configuration's name `MISSING` |
| 4 | Pane path — preset, template, echoes | PASS — pane opened via the command entry; `swprops` type opens `INVALID EmptyContent` (empty template composes to nothing); `viewTemplateNames=8` matches the enumerated count; `PART-$PRP:"PartNo"-$PRP:"Revision"` → `VALID cells=21` matching the module; `viewResolved=PART-PN-0042-REV-A`; preview signature `c4aee6aa…` equals the module's render of that literal |
| 5 | Create encodes exactly the resolved literal | PASS — `OK Sketch1`; ONE block instance at origin Scale=1; definition = 204 module edges (maxLenErr 0.000000 mm) + 8 construction (25 mm QR + 34.425 mm keep-out squares); exploded 1:1 contour match 26/26 against the module's sketch **of the resolved literal** — the encoded payload is proven, not assumed |
| 6 | Configuration switch re-resolves | PASS — `ShowConfiguration2("QrCfgB")` fired `ActiveConfigChangePostNotify` (harness-side watcher counted the event); enumerate reports `config='QrCfgB'` with the new 8-name set (cfgA-only `QrCfgProp` gone, `QrCfgOnlyB` present); `Revision`→`REV-B`, `QrShared`→`cfg-B-level`; `viewResolved=PART-PN-0042-REV-B`, dropdown re-enumerated, preview re-rendered (`22c77464…`); second create `OK Sketch2` verified module-identical against literalB — positions, not just counts, so a stale same-length payload cannot pass |
| 7 | Document switch re-resolves | PASS — `NewDocument` fired `ActiveDocChangeNotify`; `doc='Part2'` with its own 3 names, no fixture-part names leak; `PartNo`→`PN-7777`; `viewResolved=PART-PN-7777-D1`, preview `36117d63…`; reactivating the fixture fired another notification and restored `viewResolved=PART-PN-0042-REV-B` + its preview |
| 8 | Missing/empty reference refused before mutation | PASS — `$PRP:"QrNoSuchProp"` → `INVALID InvalidPayload` naming the reference; Resolved row carries `ERR:` reason; `NO-PICTURE`; create `ERROR` naming `QrNoSuchProp` with the feature tree (`[Sketch1, Sketch2]`) and sketch-edit state untouched. `$PRP:"QrEmpty"` → the distinct empty verdict ("resolves to an empty value … cannot be encoded"), refused identically. The valid template then restores `VALID` |
| 9 | No document | PASS — the last document's `CloseDoc` fires `DestroyNotify2` on the watched document (harness watcher counted 2 `Changed` events: the synchronous destroy plus the settle re-notification); resolve → `NODOC`; enumerate → `SWPROPS kind=None names=0`; `viewTemplateNames=0`; Resolved row explains "needs an active part document to resolve against" — **event-driven, no field-write fallback needed** |

## Re-run

Close SolidWorks, deploy the build under test
(`scripts\sync-dev-deploy.ps1` — it refuses while SolidWorks is open),
then:

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe swprops "$env:LOCALAPPDATA\SolidWorksQrAddIn"
```

Exit code 0 = every check passed. The harness kills only the invisible
instance it spawned (by PID, after `CloseAllDocuments`) — never
`ExitApp`, never `taskkill /IM` — and restores the captured
`AddInsStartup` flag. It refuses to run at all while any `SLDWORKS.exe`
exists, printing the fixture plan and module-side expectations first so
a blocked run still shows what the live half would check.

## Headless evidence (ran green)

- `dotnet build SolidWorksQrAddIn.sln` — **clean, 0 warnings, 0 errors**.
- `dotnet test tests\SolidWorksQrAddIn.Tests` — **641 passed, 0 failed**
  (the review-fix build adds two `SwPropertyTemplateTests` cases to the
  #46 baseline of 639).

## Limitations

- None observed in this run. The gate-9 `LIMIT` recorded on the #46 build
  — last-document close left the Resolved row stale because no
  `ActiveDocChangeNotify` fires for that transition — is **resolved**:
  the rev-31 `DPartDocEvents.DestroyNotify2` (also on `DAssemblyDocEvents`
  /`DDrawingDocEvents`) **does** fire headlessly on `CloseDoc`, and is now
  hooked in `SwHostStateWatcher`. Two honest subtleties were verified
  live: the synchronous `DestroyNotify2` callback still observes the dying
  document through `IActiveDoc2` (the immediate re-resolution re-reads
  pre-close state), and `DSldWorksEvents.FileCloseNotify` never fires
  headlessly for `CloseDoc` — so the watcher arms a one-shot ~400 ms
  settle re-notification after the destroy, whose re-read observes the
  true post-close active document (none) and lands the `ERR:` verdict
  event-driven inside the 3 s window.
- `SwPropertiesProbe`/`SwPropertyResolveProbe` answered through the
  `IDispatch` late-bound path (`GetAddInObject` returns `__ComObject`,
  not the `IAddInProbe` RCW) — the same dispatch the interactive pane
  surface exercises, verified by the discriminator call before any gate
  ran.
- Gate 1's `AddConfiguration2` inherits the active configuration's
  specific properties (verified: `QrCfgProp` arrived cloned, `Delete2`
  rc=0). The fixture deletes the clone so the post-switch name set
  differs cleanly; a `NotPresent` return (no clone — template-dependent)
  is accepted too, and the enumeration assertions, not the fixture
  script, decide what the name set must contain.
