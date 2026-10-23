# Spec 67 — GateHost consolidation live smoke-run record (ticket #64)

Harness: `tools/LiveProbe` — all twelve modes dispatch through the
GateHost registry (`Program.cs` is lookup + execute; `GateHost.cs` /
`SessionCeremony.cs` own the live-session ceremony). Required
environment: SolidWorks closed (no `SLDWORKS.exe` process — the uniform
occupied-seat refusal; no mode opts out) so the harness can spawn its own
session-free invisible instance, plus a deployed `SolidWorksQrAddIn.dll`
built from this branch (`scripts\sync-dev-deploy.ps1` first).

## Status — AWAITING LIVE RUN

> **Awaiting live verification — reserved for the human.** The smoke run
> is one production gate end-to-end on the consolidated harness
> (`circledims` preferred, `units` the alternative), proving the ceremony
> — expectations → occupied-seat refusal → launch → probe acquisition →
> check sequence → pass/fail summary → teardown — on a real seat:
>
> ```powershell
> # SolidWorks closed throughout. Deploy the build under test (Release
> # pack + mirror to the registered dev folder), build the harness, run
> # one production gate:
> .\scripts\sync-dev-deploy.ps1
> dotnet build tools\LiveProbe -c Release
> & tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe circledims "$env:LOCALAPPDATA\SolidWorksQrAddIn"
> ```
>
> Expected: `# CIRCLEDIMS ALL PASSED`, exit 0. Fill in the row below.

## Smoke run

| Date | Build/commit | SW revision | Mode | Result | Exit |
|------|--------------|-------------|------|--------|------|
| _pending_ | _branch `integration/spec-67-probe-gatehost`_ | — | `circledims` | — | — |

## Headless verification (done — no SolidWorks)

Verified on this branch (Debug|x64 build):

- `dotnet build tools/LiveProbe` — **clean: 0 warnings, 0 errors**.
- `dotnet test tests/SolidWorksQrAddIn.Tests` — **765 passed, 0 failed**.
- `LiveProbe.exe list` — exit 0, prints the registry without touching
  COM: **12 modes, 12 hosted by GateHost, 0 legacy** — 8 gates
  (`(default)`, `circledims`, `circles`, `xsettings`, `swprops`,
  `ringsplit`, `units`, `perf`), 3 spikes (`undoapi`, `pattern`,
  `circleladder`), 1 diagnostic (`circlediag`).

## What the smoke run proves — and what it doesn't re-prove

The consolidation moved every mode's per-`*Main` body onto the registry:
each hosted `Run` is the legacy check sequence verbatim and the
ceremony's hooks (`Expectations`, `ProbeFirstTry`, `ProbeSurface`,
`ProbeCheckName`/`ProbeCheckDetail`, `UnloadCandidates`, `SummaryLine`)
keep each gate's expectations, discovery check and summary line at their
legacy positions — so the recorded `*-GATES.md` verdicts
(`XSETTINGS-GATES.md`, `CIRCLE-GATES.md`, `SWPROPS-GATES.md`,
`RINGSPLIT-GATES.md`) stand; this run does not re-derive them.

Intentional semantic changes the consolidation introduced (none affect a
recorded verdict): `perf`, `undoapi`, `circledims` and the default suite
now enforce the pre-attach occupied-seat refusal their legacy bodies
skipped — they kept only the post-attach visible/open-documents backstop
(`perf` even treated it as a skip; `circledims`' docstring claimed the
discipline outright); the stale-build `LoadAddIn` retry reports a
PASS/FAIL `LoadAddIn` check where some legacy bodies printed an
informational line; `units`' probe-acquisition verdict
("installed add-in probe available") moved from mid-sequence to the
ceremony position ahead of its check sequence — `units` has no
`*-GATES.md` record.

The one thing no inspection proves is the consolidated harness itself
against a live seat — that is this smoke run's job.
