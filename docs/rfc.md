# RFC — WinLocalASR Architecture

## Overview

Three-layer layout:

- **WinLocalASR.Core** (`net10.0;net10.0-windows` class library) — everything testable: audio capture pipeline (NAudio), inference client (llama-server HTTP), settings store, app state machine, setup runner, global-hotkey manager (`RegisterHotKey` id=1 user toggle + recording-scoped id=2 Esc behind an injectable registrar). Windows-only APIs are reached only through injectable interfaces so pure logic stays cross-platform testable.
- **WinLocalASR.App** (`net10.0-windows` WinForms) — tray shell (NotifyIcon), HUD overlay, the WM_HOTKEY message-only window, setup/settings dialogs. Degrades to log warnings if GUI creation fails; the process never exits just because there is no Explorer shell (CI).
- **WinLocalASR.Tests** (xunit, `net10.0`) — cross-platform logic tests; Windows-only tests carry platform guards and run on windows-latest CI or real machines.

Control flow: hotkey → AppController (state machine in Core) → AudioCaptureManager → WAV → LlamaServerClient → ClipboardWriter → HUD feedback. An opt-in ControlServer (`--enable-control-server`, localhost:17846) exposes `/status` (phase, HUD control value, phaseHistory) and control endpoints for CI e2e.

## Inference Contract

Spike-validated 2026-09-17 on macOS arm64 (brew llama.cpp 0.4.1, build `b10964-b29c606e2`). Endpoint semantics are platform-independent (HTTP) and model quality is identical (same GGUF files), so this contract carries over to the Windows pin: any official llama.cpp release tagged after the qwen3-asr merge (2026-04-12, llama.cpp PR #19441). This section is the authoritative wire spec for `LlamaServerClient`.

### Server command line

```
llama-server -m <main.gguf> --mmproj <mmproj.gguf> --host 127.0.0.1 --port <P>
```

- Bind `127.0.0.1` only, never `0.0.0.0`; any free port works.
- No extra flags required: default context is n_ctx 65536 per slot, `--jinja` defaults on. Load-to-listening was ~6 s on the spike machine (Mac Metal).

### Model pins (official `ggml-org/Qwen3-ASR-1.7B-GGUF`)

| File | Bytes | SHA256 |
|---|---|---|
| `Qwen3-ASR-1.7B-Q8_0.gguf` | 2,165,034,944 (~2.1 GB) | `58e22d0532d4eacaf034cfac17a6fed159f37c41390c710186783be439d1fc57` |
| `mmproj-Qwen3-ASR-1.7B-Q8_0.gguf` | 355,709,344 (~340 MB) | `46c1d533af3f354ceb37ce855dbceff7da7fa7cf1e6a523df3b13440bd164c0d` |

**Quant ruling**: the official repo ships **no Q4_K_M** (Q8_0 + bf16 only). The `-hf` one-liner advertises Q4_K_M as default but silently resolves to the first file in the repo, i.e. Q8_0. Pinned quant = the **Q8_0 pair, ~2.5 GB total** (supersedes the earlier ~1.9 GB / Q4_K_M estimate; Q4_K_M exists only in third-party community repos and is rejected for provenance).

### Primary endpoint: `POST /v1/audio/transcriptions` (multipart/form-data)

| Field | Required | Behavior |
|---|---|---|
| `file` | YES | Audio file part; WAV 24 kHz mono PCM16 confirmed working. Missing → 400 `invalid_request_error` |
| `model` | NO | Accepted, value ignored |
| `prompt` | NO | Optional context/hotwords hint slot (OpenAI-compat naming), passed through |
| `temperature` | NO | Accepted; server default is ≈ 0 (greedy) |
| `response_format` | — | MUST be `json` or omitted; `text` → hard 400 ("Only 'json' response_format is supported for transcription") |

### Response shape (NOT OpenAI's `{"text": ...}`)

Verbatim success sample (1.4 s English, `tests/assets/spike-sample.wav`):

```json
{"type":"transcript.text.done","text":"language English<asr_text>The weather is sunny today.","usage":{"type":"tokens","input_tokens":41,"output_tokens":10,"total_tokens":51,"input_tokens_details":{"cached_tokens":0}}}
```

Chinese confirms the same artifact prefix, verbatim: `"language Chinese<asr_text>今天的天气晴朗，气温二十六度，适合外出散步。"`.

`text` carries the artifact prefix `language <Lang><asr_text>` (confirmed verbatim EN + ZH); the transcript follows `<asr_text>` directly. The client strips the prefix with the regex `^language\s+[^<]*<asr_text>`; if the pattern is absent, return the raw text unchanged (never empty-out unmatched text). Task 4 unit tests quote these fixtures; the matching WAVs live in `tests/assets/spike-sample.wav` (1.4 s EN) and `tests/assets/spike-sample-10s.wav` (8.7 s EN).

### Control plane

- `GET /health` → `200 {"status":"ok"}` — the ready-poll target.
- **`/shutdown` does not exist** (GET and POST both 404). `Stop()` therefore attempts graceful shutdown, then kills the process after 2 s (`taskkill` on Windows; SIGTERM elsewhere, verified clean).

### Windows binary pin (2026-09-17, Task 5)

- **Tag**: `b10964` — the same build number the spike validated (`b10964-b29c606e2`), published as an official llama.cpp pre-release. Pinned in `versions.json` (`llamaCpp.tag`).
- **Asset**: `llama-b10964-bin-win-cpu-x64.zip` (18,427,629 bytes), confirmed to contain `llama-server.exe`. Runtime dependency DLLs shipped in the zip: `llama-server-impl.dll`, `llama.dll`, `llama-common.dll`, `mtmd.dll`, `ggml.dll`, `ggml-base.dll`, CPU-variant dispatch DLLs (`ggml-cpu-alderlake/cannonlake/cascadelake/cooperlake/haswell/icelake/ivybridge/piledriver/sandybridge/sapphirerapids/skylakex/sse42/x64/zen4.dll`), `ggml-rpc.dll`, `libomp.dll` (+ `LICENSE-LLVM-OpenMP`). The downloader must extract the whole zip (the CPU dispatch DLLs are selected at load time).
- **SHA256 policy**: `official` — GitHub's release-asset digest (`917f39c076402c421224824607397af20f53625a60defc20e8dd22446bf4c5d7`) was independently verified against a local download (byte-exact). Not TOFU. Recorded in `versions.json` (`llamaCpp.sha256` / `sha256Policy`).

### Validation context

- Quality gate PASS on the pinned-equivalent build: exact EN+ZH transcripts across 1.4 s–142.5 s duration tiers; 87.4 s audio transcribed in 4.4 s and 142.5 s in 7.8 s (Mac Metal). Windows CPU AVX2 will be slower but retains ~2 orders of magnitude headroom against the 10 s → <10 s soft target.
- **120 s recording cap retained as a conservative guardrail**: llama.cpp issue #21847 (>2 min audio) was NOT reproduced on `b10964` (a 142.5 s file transcribed in full, correctly). The cap stays because the upstream bug may be content-, sample-rate-, or version-dependent. Also recorded in §Parity.
- Secondary route `POST /v1/chat/completions` with a base64 `input_audio` content part also works (same artifact prefix in `choices[0].message.content`) — documented as **fallback only**; the multipart route is primary (no ~33% base64 inflation, purpose-built).

Anchor decisions already made:

- Serving: resident `llama-server.exe` spawned by the app; port probed from 18100 upward, avoiding ControlServer's 17846.
- Model: Qwen3-ASR-1.7B, official Q8_0 GGUF pair (main + mmproj, SHA256 above), downloaded by setup (~2.5 GB total) from Hugging Face with hf-mirror.com fallback.
- Timeout: `max(90, 3×duration + 20)` seconds per transcription.
- Crash recovery: automatic restart with 1/2/4 s backoff, at most 3 attempts, then `RestartFailed`.

## Parity

Feature-by-feature against grapeot/mac-native-local-asr. "Known deviation" captures accepted behavioral differences (not bugs).

| 功能 | Mac 行为 | Win 计划 | 与 Mac 的已知偏差 |
|---|---|---|---|
| 托盘 (tray) | Menu bar app via MenuBarItem-style `MenuBarExtra`, no dock icon; menu: status / Setup / Settings / copy-last / restart engine / quit | `NotifyIcon` + bilingual `ContextMenuStrip` with the same menu set; icon switches by phase; `Global\WinLocalASR` mutex for single instance; balloon notifications for errors | None (menu structure equivalent) |
| 热键 (hotkey) | Global toggle, default ⌘⇧Space; user-configurable | Global toggle via `RegisterHotKey`, default `Ctrl+Shift+Space`; configurable; serialization round-trips `Ctrl+Shift+Space`-style strings | Modifier mapping only (⌘⇧ → Ctrl+Shift) |
| Esc 取消 (cancel) | `Esc` registered only while recording (`setCancelShortcutActive` pattern); leaves state clean | `RegisterHotKey` id=2 (bare `VK_ESCAPE`) registered only in Recording phase via `CancelHotkeyAvailabilityChanged`, unregistered on exit (Task 8 `HotkeyManager`, message-only window) | Esc is globally swallowed by the app during recording (other apps' Esc also cancels) — same tradeoff as Mac, accepted; Task 14 real-machine checklist line: Esc in another app cancels the recording, idle Esc untouched |
| 录音上限 (limit) | Configurable max recording duration, auto-stop | `NumericUpDown` 10–120 s, default 120 s, auto-stop callback | **Hard cap at 120 s** — retained as a conservative guardrail: llama.cpp #21847 (>2 min audio) was NOT reproduced on build `b10964` (142.5 s transcribed in full), but the bug may be content-/version-dependent; Mac has no such cap |
| 电平表 (level meter) | RMS level, `(dB+80)/70` clamped to [0,1], fed to HUD | Same formula over NAudio capture buffers, same HUD consumption | None |
| 热词 (hotwords) | Context prompt passed to the recognition bridge | Context prompt mapped to the multipart `prompt` field of `POST /v1/audio/transcriptions` (§Inference Contract) | Wire format differs (JSONL bridge → HTTP multipart) |
| HUD | `DictationHUD` presentation states: recording (red, timer, level), processing (yellow, spinner), copied (green ✓ 1.5 s), cancelled (grey ↯ 1.5 s), error (message, 3 s); clipboard failure renders red ✗ inside error | Borderless topmost `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` overlay, bottom-right above taskbar, DPI-aware, same presentation state machine driven by Core | None |
| ControlServer | Opt-in `--enable-control-server`, localhost:17844, `/status` + `/setup` | Opt-in, localhost:17846, `/status` (phase, HUD control value, `phaseHistory` array) + `/setup` + CI-only endpoints (`/control/toggle`, `/control/quit`, `/fake-transcript`) and `--fake-configured` mode | Superset for CI e2e; port differs to avoid coexistence confusion |
| 双语 (i18n) | EN + zh-Hans following system locale | `Strings.resx` + `Strings.zh-Hans.resx`, `CurrentUICulture` | None |
| Setup | SetupRunner creates venv, installs mlx-qwen3-asr, writes bridge script; one click | Six-step SetupRunner: dirs → llama-server.exe (+DLLs, SHA256-verified) → GGUF + mmproj (~2.5 GB official Q8_0 pair, mirrors + resume) → checksums → versions.json + configured → initialize | Different backend artifacts (Python venv → pinned llama.cpp binaries + GGUF); installer via Inno Setup with `/AUTOSTART` pending-flag pattern |

## Decisions log

- **D1 — C# .NET 10 WinForms shell, platform-neutral Core**: smallest GUI footprint on Windows; Core multi-targeting exposes Windows-only API gaps at compile time while keeping logic tests cross-platform.
- **D2 — MIT + NOTICE attribution to grapeot/mac-native-local-asr**: the port derives product behavior and documentation structure from the upstream; attribution is duplicated in README Credits as a second surface.
- **D3 — Q8_0 default quant** (supersedes the original Q4_K_M plan): the official `ggml-org/Qwen3-ASR-1.7B-GGUF` ships no Q4_K_M, so the official minimum pair is Q8_0 (~2.5 GB); spike-validated for quality including Chinese. Full ruling in §Inference Contract.
- **D4 — Windows CI from day one**: Windows-only tests (NAudio/WinForms/clipboard/hotkeys) get per-push feedback instead of failing late.
- **D5 — settings.json is SSOT, HKCU Run key derived**: installs write only the Run key + pending flag; the app merges settings on first launch so upgrades never clobber user configuration.
