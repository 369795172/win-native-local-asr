# RFC — WinLocalASR Architecture

## Overview

Three-layer layout:

- **WinLocalASR.Core** (`net10.0;net10.0-windows` class library) — everything testable: audio capture pipeline (NAudio), inference client (llama-server HTTP), settings store, app state machine, setup runner. Windows-only APIs are reached only through injectable interfaces so pure logic stays cross-platform testable.
- **WinLocalASR.App** (`net10.0-windows` WinForms) — tray shell (NotifyIcon), HUD overlay, global hotkeys, setup/settings dialogs. Degrades to log warnings if GUI creation fails; the process never exits just because there is no Explorer shell (CI).
- **WinLocalASR.Tests** (xunit, `net10.0`) — cross-platform logic tests; Windows-only tests carry platform guards and run on windows-latest CI or real machines.

Control flow: hotkey → AppController (state machine in Core) → AudioCaptureManager → WAV → LlamaServerClient → ClipboardWriter → HUD feedback. An opt-in ControlServer (`--enable-control-server`, localhost:17846) exposes `/status` (phase, HUD control value, phaseHistory) and control endpoints for CI e2e.

## Inference Contract

> Pending Task 1 spike backfill. This section will pin: llama.cpp release tag, `llama-server` CLI command line (mmproj, ctx, port), the audio endpoint path and multipart field names, the context/hotwords parameter name, verbatim raw response samples (including any prefix artifacts and their normalization rules), `/health` and `/shutdown` endpoint availability, model file names + SHA256, and the fallback ruling if no audio endpoint exists.

Anchor decisions already made:

- Serving: resident `llama-server.exe` spawned by the app; port probed from 18100 upward, avoiding ControlServer's 17846.
- Model: Qwen3-ASR-1.7B, Q4_K_M GGUF + mmproj, downloaded by setup (~1.9 GB total) from Hugging Face with hf-mirror.com fallback.
- Timeout: `max(90, 3×duration + 20)` seconds per transcription.
- Crash recovery: automatic restart with 1/2/4 s backoff, at most 3 attempts, then `RestartFailed`.

## Parity

Feature-by-feature against grapeot/mac-native-local-asr. "Known deviation" captures accepted behavioral differences (not bugs).

| 功能 | Mac 行为 | Win 计划 | 与 Mac 的已知偏差 |
|---|---|---|---|
| 托盘 (tray) | Menu bar app via MenuBarItem-style `MenuBarExtra`, no dock icon; menu: status / Setup / Settings / copy-last / restart engine / quit | `NotifyIcon` + bilingual `ContextMenuStrip` with the same menu set; icon switches by phase; `Global\WinLocalASR` mutex for single instance; balloon notifications for errors | None (menu structure equivalent) |
| 热键 (hotkey) | Global toggle, default ⌘⇧Space; user-configurable | Global toggle via `RegisterHotKey`, default `Ctrl+Shift+Space`; configurable; serialization round-trips `Ctrl+Shift+Space`-style strings | Modifier mapping only (⌘⇧ → Ctrl+Shift) |
| Esc 取消 (cancel) | `Esc` registered only while recording (`setCancelShortcutActive` pattern); leaves state clean | `RegisterHotKey` id=2 registered only in Recording phase, unregistered on exit | Esc is globally swallowed by the app during recording (other apps' Esc also cancels) — same tradeoff as Mac, accepted |
| 录音上限 (limit) | Configurable max recording duration, auto-stop | `NumericUpDown` 10–120 s, default 120 s, auto-stop callback | **Hard cap at 120 s** — llama.cpp #21847 affects >2 min audio; Mac has no such cap |
| 电平表 (level meter) | RMS level, `(dB+80)/70` clamped to [0,1], fed to HUD | Same formula over NAudio capture buffers, same HUD consumption | None |
| 热词 (hotwords) | Context prompt passed to the recognition bridge | Context prompt mapped to the llama-server parameter pinned in §Inference Contract (exact field name from spike) | Wire format differs (JSONL bridge → HTTP multipart) |
| HUD | `DictationHUD` presentation states: recording (red, timer, level), processing (yellow, spinner), copied (green ✓ 1.5 s), cancelled (grey ↯ 1.5 s), error (message, 3 s); clipboard failure renders red ✗ inside error | Borderless topmost `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` overlay, bottom-right above taskbar, DPI-aware, same presentation state machine driven by Core | None |
| ControlServer | Opt-in `--enable-control-server`, localhost:17844, `/status` + `/setup` | Opt-in, localhost:17846, `/status` (phase, HUD control value, `phaseHistory` array) + `/setup` + CI-only endpoints (`/control/toggle`, `/control/quit`, `/fake-transcript`) and `--fake-configured` mode | Superset for CI e2e; port differs to avoid coexistence confusion |
| 双语 (i18n) | EN + zh-Hans following system locale | `Strings.resx` + `Strings.zh-Hans.resx`, `CurrentUICulture` | None |
| Setup | SetupRunner creates venv, installs mlx-qwen3-asr, writes bridge script; one click | Six-step SetupRunner: dirs → llama-server.exe (+DLLs, SHA256-verified) → GGUF + mmproj (~1.9 GB, mirrors + resume) → checksums → versions.json + configured → initialize | Different backend artifacts (Python venv → pinned llama.cpp binaries + GGUF); installer via Inno Setup with `/AUTOSTART` pending-flag pattern |

## Decisions log

- **D1 — C# .NET 10 WinForms shell, platform-neutral Core**: smallest GUI footprint on Windows; Core multi-targeting exposes Windows-only API gaps at compile time while keeping logic tests cross-platform.
- **D2 — MIT + NOTICE attribution to grapeot/mac-native-local-asr**: the port derives product behavior and documentation structure from the upstream; attribution is duplicated in README Credits as a second surface.
- **D3 — Q4_K_M default quant**: latency/size balance on CPU AVX2; other quants remain available via manual swap.
- **D4 — Windows CI from day one**: Windows-only tests (NAudio/WinForms/clipboard/hotkeys) get per-push feedback instead of failing late.
- **D5 — settings.json is SSOT, HKCU Run key derived**: installs write only the Run key + pending flag; the app merges settings on first launch so upgrades never clobber user configuration.
