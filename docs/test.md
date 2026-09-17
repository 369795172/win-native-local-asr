# test.md — WinLocalASR Verification

Four layers, all agent-executable. Real-machine items run via runbook with results recorded back here (anonymized: "reference test machine (Win11 x64)").

## Layer 1 — Unit tests (xunit)

```bash
dotnet test src/WinLocalASR.sln
```

- Cross-platform pure-logic cases run on any host and on CI.
- Windows-only cases (NAudio, clipboard, hotkey registration, real registry, WinForms/GDI+) carry platform guards: skipped on non-Windows, executed on windows-latest CI and real machines.
- Coverage as shipped: state machine transitions (incl. double-toggle race, Esc scope, error auto-recovery, feedback windows), WAV encoding/resampling (byte-level RIFF parse), timeout formula, WAV duration parsing, hotkey serialization round-trip and MOD_/VK contract pins, settings round-trip + corruption/atomic-write/autostart-merge matrix, clipboard retry policy, versions manifest, setup runner (fresh-machine simulation, resume, mirrors, checksum failure, cancellation), inference client against fake HTTP/process servers (ready/transcribe/restart-exhaustion/strip fixtures), tray/bootstrapper/single-instance/GUI-degrade, settings dialog presenter, HUD mapping/placement/metrics, ControlServer endpoints.

**Status**: green. Latest main run (windows-latest): **354 passed / 1 skipped / 0 failed** (the single skip is a macOS-only platform guard; all Windows-only SkippableFacts executed for real) — https://github.com/369795172/win-native-local-asr/actions/runs/35203550100. Local macOS: 342 passed / 13 skipped / 0 failed.

## Layer 2 — CI e2e (windows-latest)

`.github/workflows/windows.yml` is the full three-job delivery pipeline on every push to main (plus workflow_dispatch and v* tags):

- **build-chain** (main / dispatch / tags): build Release → full test suite → self-contained single-file publish → self-contained verification (`tests/verify-selfcontained.ps1`) → ControlServer-driven e2e (`tests/e2e.ps1` against the publish output: fake-configured synthetic-audio full cycle, transcript + clipboard + phaseHistory assertions) → pinned llama.cpp binary verification (`tests/verify-llama-binaries.ps1`, `llama-server.exe --version`) → Inno Setup installer (`packaging/installer.iss`) → portable zip → silent-install regression (`tests/install-regression.ps1`, `/VERYSILENT` install-back + unconfigured-phase assertion + uninstall).
- **smoke** (dispatch / v* tags only): downloads the pinned engine + Q8_0 GGUF pair (SHA256-verified, hf-mirror fallback), runs llama-server, and transcribes both committed spike samples (`tests/assets/spike-sample.wav`, `spike-sample-10s.wav`) with stripped-text assertions — real Windows inference, not fake. A release cannot ship on a red smoke run (`release` job `needs: [build-chain, smoke]`).
- **release** (v* tags only): uploads installer + portable zip as prerelease.

**Status**: green on main (latest: https://github.com/369795172/win-native-local-asr/actions/runs/35203550100, e2e clipboard assertion non-degraded). v0.1.0 tag run: build-chain + smoke + release all green — https://github.com/369795172/win-native-local-asr/actions/runs/35204079952. Anti-fake-green red-proof documented in working.md (deliberate failure branch went red and was deleted).

## Layer 3 — Inference contract spike

Platform-independent protocol verification of llama-server's Qwen3-ASR audio endpoint: done 2026-09-17 (macOS arm64, brew llama.cpp 0.4.1 `b10964`). Request fields, verbatim EN/ZH responses, artifact prefix and strip rule, `/health` / `/shutdown` behavior, pinned tag + SHA256s all live in `docs/rfc.md` §Inference Contract. The Windows smoke job (Layer 2) re-proves the contract on windows-latest per tag.

**Status**: complete and continuously re-validated per tag.

## Layer 4 — Real-machine acceptance

**Status: PENDING Task 14 — blocked on machine availability. No real-machine results exist yet; nothing below is filled in.**

Reference test machine (Win11 x64): install from Release assets → SmartScreen path → first setup (mirror fallback observed) → hotkey dictation → clipboard paste correctness → offline re-test (network disabled) → Esc / limit auto-stop / device switch → autostart → clean uninstall. Latency numbers recorded (10 s audio, soft target < 10 s wall clock).

### Real-machine checklist

Deferred visual/interaction items proven only on a real display (CI runners are headless at 96 dpi; results land here during Task 14 acceptance — PENDING, blocked on machine availability):

- [ ] HUD at extreme DPI (150% / 200%): text not clipped, bars and timer readable.
- [ ] HUD multi-monitor: overlay anchors to the primary screen's working area bottom-right (not the mouse's screen).
- [ ] Esc pressed in another application while WinLocalASR records cancels the recording; idle Esc reaches other apps normally.
- [ ] VCRT independence on first install (`%LOCALAPPDATA%\WinLocalASR\bin\` DLL inventory).
- [ ] Offline dictation after disabling networking.
- [ ] Latency: 10 s of speech → wall-clock transcription time ×3.
