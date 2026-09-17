# PRD — WinLocalASR

## What

Offline voice dictation for Windows 10+ (x64): a system tray app where the user presses a global hotkey, speaks, and receives the transcript on the clipboard. Fully local inference (Qwen3-ASR-1.7B GGUF on llama.cpp, CPU AVX2 sufficient). Windows port of grapeot/mac-native-local-asr with feature parity.

## Why

- Dictation without cloud round-trips: privacy, latency, and offline availability.
- The macOS original proves the model and UX; Windows has no equivalent bundled experience for this workflow.
- llama.cpp is currently the only verified, single-exe, CPU-capable serving path for Qwen3-ASR on Windows.

## User goals

1. Press `Ctrl+Shift+Space`, speak, press again → text is on the clipboard within seconds.
2. Works with networking disabled after first-time setup.
3. Zero configuration beyond a one-click first-run setup (engine + model download) and optional settings (hotkey, device, hotwords, recording limit, autostart).

## In scope

- Tray-resident app with phase-aware icon and bilingual (EN / 简体中文) menu
- Global toggle hotkey (default `Ctrl+Shift+Space`); `Esc` cancels mid-recording (registered only while recording)
- Recording auto-stop at the configured limit (default 120 s, range 10–120 s)
- 24 kHz mono Int16 capture with live level meter; selectable input device
- Offline transcription via a resident llama.cpp server (Qwen3-ASR-1.7B, Q8_0 default)
- Hotwords / context prompt passed to the recognizer
- Clipboard-only output (paste with `Ctrl+V`; no simulated keystrokes)
- Six-step first-run setup: directories → engine binary → model download (with China-friendly mirrors and resume) → checksums → manifest + configured flag → app initialization
- Settings dialog: hotkey, input device, hotwords, recording limit, autostart
- HUD overlay feedback (recording / processing / copied / cancelled / error)
- Opt-in ControlServer on localhost for automated testing
- Inno Setup installer + portable zip; GitHub Actions CI and Releases
- Single-user target machine assumption (per-user install paths, HKCU autostart)

## Out of scope / Must-NOT-have

- No cloud APIs anywhere; runtime network allowlist is localhost-only (setup-period downloads are the sole exception)
- No VAD, push-to-talk, auto-segmentation, or real-time streaming (llama.cpp does not support streaming for this model family)
- No LLM post-processing, speaker diarization, or simulated-keystroke output (clipboard only)
- No changes to the macOS upstream (read-only reference)
- No Win7/8, x86, or ARM64 support (Win10+ x64 only)
- No code-signing certificate (README documents the SmartScreen "More info → Run anyway" path)
- No winget/store distribution (future option)
- Installer does not bundle model weights (setup downloads ~2.5 GB; self-contained installer size ~50–70 MB accepted)
- No Whisper substitution for Qwen3-ASR

## Known tradeoffs

- **Esc is globally swallowed while recording**: during an active recording, the app registers a system-wide bare-Esc hotkey, so pressing Esc in ANY application (not just WinLocalASR) cancels the recording. Outside recording, Esc is never registered and reaches other applications normally. This replicates the macOS original's behavior and is accepted; the Task 14 real-machine checklist verifies both sides (Esc in another app cancels the recording; Esc behaves normally when idle).
- **Recording limit hard cap at 120 s**: see `docs/rfc.md` §Parity (llama.cpp #21847 guardrail).

## Success criteria

1. A fresh Windows 10+ x64 machine: install → one-click setup → hotkey dictation → paste correct text; network disabled mid-session does not break dictation.
2. 10 s of speech transcribes in under 10 s wall clock on the reference test machine (Win11 x64) — soft target; misses become tuning issues, not hidden.
3. CI (build + tests + e2e + silent-install verification) stays green on main; Release assets exist per tag.
4. Feature parity with the macOS original per `docs/rfc.md` §Parity, with deviations explicitly recorded there (first one: recording limit hard cap at 120 s).
