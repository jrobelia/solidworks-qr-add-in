# SolidWorks QR Add-In

Standalone SolidWorks add-in that generates a native QR sketch from text or a URL. Specification: issue #8 (working copy: `.scratch/MVP-SPEC.md`). Evidence and rationale: `docs/RESEARCH.md`.

## Agent skills

### Issue tracker

Issues live in GitHub Issues on this repo (`gh` CLI). Specs and tickets are published as issues with native sub-issue and blocked-by edges; ticket #1–#7 are sub-issues of spec issue #8. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical roles use identical label strings: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: `GLOSSARY.md` at the repo root, ADRs in `docs/adr/`, both created lazily. See `docs/agents/domain.md`.

## Repo guides

- Build/test, repo layout, LiveProbe live-verify harness → `docs/BUILDING.md`
- Add-in missing/not loaded, registration, installer recovery, or debug log: read `docs/DEPLOYMENT.md` (especially Dev-machine setup).
- Live acceptance gates (need exclusive SolidWorks access) → `docs/ACCEPTANCE.md`
- Proven `CreateLine` coordinate conventions → `docs/SKETCH-COORDINATES.md`
- Geometry insertion limits → `docs/INSERTION-GUARD.md`
- Manual QA/build refresh: with SolidWorks closed, run `scripts/sync-dev-deploy.ps1` from the branch/worktree under test, then restart SolidWorks; details in `docs/DEPLOYMENT.md`.
- Dev registration setup/recovery after installer tests: `scripts/register-addin-dev.ps1` requests UAC and replaces this add-in's registration keys; get approval before running it. Details in `docs/DEPLOYMENT.md`.
