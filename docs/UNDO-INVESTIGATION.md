# Named-undo investigation — FinishRecordingUndoObject2 refused (D17)

Status: **open — diagnostic built, live run pending a session-free window.**

Branch `investigate/named-undo`. Baseline: SW 2023 SP5 (rev 31.5.0), all 238
headless tests green. This document supersedes the D17 "limitation" verdict
only if the live matrix below produces a working arrangement.

## Recap of the verified symptom

`IModelDocExtension.StartRecordingUndoObject()` is accepted (no throw), but
`FinishRecordingUndoObject2("Create QR Sketch", false)` returns `false` in
every arrangement previously tried, so no named undo step registers and one
`EditUndo2` does not remove the generated sketch (and never eats prior user
work — the safety invariant holds, asserted live 2026-10-09).

## NEW finding since D17: the call context was never the real one

All prior evidence came through `IAddInProbe` invoked by LiveProbe over COM —
per `src/SolidWorksQrAddIn/UI/AddInWpfThread.cs` (lines 9–13), **probe calls
arrive on MTA COM worker threads**. The real user path is different: the
menu callback opens the window, and the Create button runs
`session.CreateSketch(host)` — including `StartRecordingUndoObject` /
`FinishRecordingUndoObject2` — **on the add-in's dedicated STA thread**
(`AddInWpfThread` dispatcher, `SwAddin.CreateQrWindow` → `_wpf.Invoke`).

`CreateQrFromWindowProbe` (SwAddin.Probes.cs) calls `session.CreateSketch`
directly on the probe's calling thread — the MTA worker. So the "every
arrangement" evidence in D17 was collected in a thread context the product
never uses. SolidWorks undo recording is almost certainly bound to the
state of the thread/apartment that issues the model mutations; an MTA
COM-worker call context is a plausible reason for the recording to be
dead at finish time. **This is the primary open hypothesis (H1) and the
product code path has never actually been tested for undo.**

## Reference implementations

- The sibling InvenTree add-in (`C:\SoftwareProjects\Solidworks Inventree
  Add-In`) contains **no** `StartRecordingUndoObject`/`FinishRecordingUndoObject`/
  `EditUndo` usage — no guidance there.
- Public examples (cnblogs SolidWorks API series) show the pattern working
  for *dimension* changes recorded around `SetValue3` + `EditRebuild3` — i.e.
  model mutations that produce normal undoable feature edits, called from an
  add-in's own context.
- Forum evidence (SOLIDWORKS forum thread 56742) records that "some processes
  throw off the undo mechanism" inside a recording — operations that bypass
  the normal command path produce nothing to record.

## Best-supported explanation so far (composite)

Two effects probably stack:

1. **`AddToDB` produces no undoable edits.** `ISketchManager.AddToDB` writes
   entities directly into model storage, bypassing the UI-driven creation
   path that generates undo entries (documented purpose: entities skip the
   interface; codestack "create sketch segments error" troubleshooting doc).
   A recording that captured only AddToDB writes has nothing undoable to
   commit, and `FinishRecordingUndoObject2` returns false. The product needs
   AddToDB — the interactive path sporadically refuses coincident-endpoint
   lines in a ~250-segment QR contour set (SwSketchInsertion.cs comment) —
   so "just drop AddToDB" is not a fix.
2. **Possibly also the MTA probe thread** — untested until the matrix runs
   on a session-free instance (arrangements 7/12/13 vs their MTA twins).

## Experiment matrix — ready to run

New probe: `IAddInProbe.UndoArrangementProbe(int)` (SwAddin.Probes.cs). Each
arrangement creates a **fresh throwaway part document**, draws a 2-segment
sketch on Front Plane under the given arrangement, exits edit mode, presses
`EditUndo2(1)`, reports `finish=<TRUE|false|EX>` and `undoRemoved=<bool>`,
then closes the doc. `-1` is a no-op ping reporting the executing thread.

| Arr | recording opens | AddToDB | finish called | finish API | thread |
|-----|-----------------|---------|---------------|------------|--------|
| 9 | n/a | n/a | empty rec (no doc mutation) | v2 | probe/MTA |
| 0 | — none (control) | off | — | — | MTA |
| 1 | before InsertSketch | off | after exit | v2 | MTA |
| 2 | before InsertSketch | on | after exit | v2 | MTA |
| 3 | inside edit mode | off | after exit | v2 | MTA |
| 4 | inside edit mode | on | after exit | v2 | MTA (= product shape) |
| 5 | inside edit mode | on | while sketch open | v2 | MTA |
| 6 | inside edit mode | on | after exit | **v1** | MTA |
| 8 | before InsertSketch | on | while open | v2 | MTA |
| 10 | mid-sketch (after lines) | on | after exit | v2 | MTA |
| 11 | inside edit mode | on | after exit | v2, MakeHidden=**true** | MTA |
| 14 | before the selection | on | after exit | v2 | MTA |
| 13 | — none (control) | off | — | — | **WPF STA** |
| 12 | before InsertSketch | off | after exit | v2 | **WPF STA** |
| 7 | inside edit mode | on | after exit | v2 | **WPF STA** (= real Create path) |

Run when no interactive session is open:

```powershell
.\tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe undo `
  ".\src\SolidWorksQrAddIn\bin\x64\Release\net48" "SldWorks.Application.31"
```

## Hypothesis ledger

| H | hypothesis | status |
|---|------------|--------|
| H1 | MTA probe context is not a legitimate recording context; STA (real path) may work | **strengthened** — the prior evidence was all MTA; pending arr 7/12/13 |
| H2 | timing/scope (before vs after InsertSketch, finish open vs exited, wrap selection) | covered by arr 1,2,3,5,8,10,14 — pending |
| H3 | v1 vs v2 / MakeHidden | covered by arr 6, 11 — pending |
| H4 | AddToDB/DisplayWhenAdded makes edits unrecordable | supported by docs + forum evidence; arr 1 vs 2 (and 12 vs 7) isolates it — pending |
| H5 | genuine build limitation | fallback verdict if even arr 1/9/12 fail |

## Decision tree for the pending live run

- **arr 7 or 12 returns TRUE and `undoRemoved=True`** → the limitation was a
  probe artifact; the product path (STA) already works. Fix = none in
  product code; adjust LiveProbe's undo check to invoke through the WPF
  thread and re-verify D17.
- **arr 1/12 TRUE, arr 2/7 false** → AddToDB is the blocker (H4 confirmed).
  Options: (a) accept limitation (current state), (b) implement
  `IModelDocExtension.SetApiUndoObject` — a custom undo object that deletes
  the created sketch — heavier but a genuine fix, (c) investigate whether a
  two-phase insert (interactive-mode lines only) can replace AddToDB —
  unlikely, it was needed for reliability.
- **arr 9 also false** → recording is entirely dead in this context/build
  even with nothing to record → points to context/build, not AddToDB.
- **everything false incl. STA control (arr 12)** → H5: accept the
  limitation permanently; keep the existing best-effort code and D17
  wording.

## Attempted live run log

- 2026-10-09+ (this branch): SLDWORKS.exe PID 35564 = **visible user
  session** (SW 2023 SP5, `CBASS_Assembly.SLDASM` open) — untouchable.
  `SldWorks.Application.30` and CLSID `{b2f1524f-…}` both resolve to the
  *same* 2023 server (alias; only one SW version installed — the
  exploration-notes claim of a second SW 2022 install does not hold on this
  machine). Activation attached to the running session; the probe's
  visible/docs guard correctly refused (`visible=True docs=192`). **No
  session-free window exists; matrix deferred.**
