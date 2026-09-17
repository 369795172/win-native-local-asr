# test.md — WinLocalASR Verification

Four layers, all agent-executable. Real-machine items run via runbook with results recorded back here (anonymized: "reference test machine (Win11 x64)").

## Layer 1 — Unit tests (xunit)

```bash
dotnet test src/WinLocalASR.sln
```

- Cross-platform pure-logic cases run on any host and on CI.
- Windows-only cases (NAudio, clipboard, hotkey registration, WinForms) carry platform guards: skipped on non-Windows, executed on windows-latest CI and real machines.
- Planned coverage: state machine transitions (incl. double-toggle race, Esc scope, error auto-recovery), WAV encoding/resampling, timeout formula, hotkey serialization round-trip, settings round-trip, inference client against a fake HTTP server (success/timeout/restart paths).

**Status**: skeleton only — one smoke test; coverage grows with Core modules.

## Layer 2 — CI e2e (windows-latest)

`.github/workflows/windows.yml` currently builds + tests the solution on every push/PR. Later milestones extend it with: publish, ControlServer-driven e2e (`tests/e2e.ps1`, `--fake-configured` synthetic audio), pinned-engine binary verification, inference smoke job on tags, installer build + `/VERYSILENT` install-back verification, and Release publishing.

**Status**: build + test active from bootstrap; e2e pending.

## Layer 3 — Inference contract spike

Platform-independent protocol verification of llama-server's Qwen3-ASR audio endpoint (request fields, verbatim responses, artifacts, health/shutdown endpoints, pinned tag + SHA256). Results land in `docs/rfc.md` §Inference Contract.

**Status**: pending spike backfill.

## Layer 4 — Real-machine acceptance

Reference test machine (Win11 x64): install from Release assets → SmartScreen path → first setup (mirror fallback observed) → hotkey dictation → clipboard paste correctness → offline re-test (network disabled) → Esc / limit auto-stop / device switch → autostart → clean uninstall. Latency numbers recorded (10 s audio, soft target < 10 s wall clock).

**Status**: pending first Release.
