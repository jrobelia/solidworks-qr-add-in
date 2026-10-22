# SolidWorks QR Add-In

Standalone SolidWorks add-in that generates a native QR sketch from text or a URL. Original MVP: GitHub issue #8. Design evidence and rationale: `docs/RESEARCH.md`.

## Agent skills

### Issue tracker

For specs and tickets, read the relevant GitHub issue, comments, child tickets, and blocking edges with `gh`. Follow `docs/agents/issue-tracker.md` for tracker operations.

### Triage labels

For triage, use the role-to-label mapping in `docs/agents/triage-labels.md`.

### Domain docs

Before exploring or changing a module, read `GLOSSARY.md` and relevant ADRs in `docs/adr/`; follow `docs/agents/domain.md` for domain-document rules.

## Repo guides

- Before changing Core, SolidWorks adapters, UI, or tests, read `docs/BUILDING.md` for module boundaries, build/test commands, and layout.
- Add-in missing/not loaded, registration, installer build/test/recovery, or debug log: read `docs/DEPLOYMENT.md` (especially Dev-machine setup).
- Before running LiveProbe, read the session-safety and invocation instructions in `docs/BUILDING.md`; use a session-free SolidWorks instance.
- For acceptance/QA, read `docs/ACCEPTANCE.md` and relevant `docs/*-GATES.md` records available on the branch under test; report automated results and manual QA separately.
- Proven `CreateLine` coordinate conventions → `docs/SKETCH-COORDINATES.md`
- Geometry insertion limits → `docs/INSERTION-GUARD.md`
- Ring-split topology (island-bearing holes, finder channels, module-aligned cell drops, quiet-zone egress) → `docs/RING-SPLIT-RESEARCH.md`
- Manual QA/build refresh: with SolidWorks closed, run `scripts/sync-dev-deploy.ps1` from the branch/worktree under test, then restart SolidWorks; details in `docs/DEPLOYMENT.md`.
- Dev registration setup/recovery after installer tests: `scripts/register-addin-dev.ps1` requests UAC and replaces this add-in's registration keys; get approval before running it. Details in `docs/DEPLOYMENT.md`.
