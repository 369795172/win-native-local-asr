# AGENTS.md — WinLocalASR

## Project Role

A Windows 10+ (x64) system tray app for offline voice-to-text dictation, using Qwen3-ASR-1.7B (GGUF) served by llama.cpp. Public GitHub repo. A Windows port of grapeot/mac-native-local-asr — the macOS project is the read-only behavioral reference; this repo holds fresh C# code, not a line-by-line translation.

## Language

- **Working language**: English for all docs, code, and commit messages.
- **UI strings**: bilingual (English + Simplified Chinese), following system locale.
- The Parity table in `docs/rfc.md` keeps bilingual row labels; cell content is English.

## Structure

- `src/` — Solution and project sources
  - `src/WinLocalASR.sln` — solution
  - `src/WinLocalASR.Core/` — platform-neutral class library (multi-targets `net10.0;net10.0-windows`), additive subdirectories: `Audio/` (capture, WAV, level), `Inference/` (llama-server client), `Settings/` (store + autostart SSOT), `Output/` (clipboard), `Versions/` (manifest), `State/` (AppController state machine), `Setup/` (six-step runner + presenter), `Hotkeys/` (manager + HotkeyString), `SettingsUi/`, `Shell/` (bootstrapper, tray presenter, single instance), `Hud/`, `Control/` (ControlServer + fake bridge)
  - `src/WinLocalASR.App/` — WinForms shell (`net10.0-windows`): tray, HUD form, hotkey message window, dialogs
  - `src/WinLocalASR.Resources/` — resx string tables (English neutral + zh-Hans satellite)
  - `src/WinLocalASR.Tests/` — xunit tests (`net10.0`; Windows-only cases are platform-guarded)
- `docs/` — Product and engineering docs
  - `prd.md` — product scope, non-goals, success criteria
  - `rfc.md` — architecture, inference contract, Mac/Win parity table
  - `working.md` — changelog and lessons learned
  - `test.md` — layered verification strategy and results
- `tests/` — e2e + CI drivers: `e2e.ps1` (ControlServer-driven end-to-end), `inference-smoke.ps1` (real-inference tag job), `install-regression.ps1` (silent install/uninstall), `verify-selfcontained.ps1`, `verify-llama-binaries.ps1`, `assets/` (committed spike WAV fixtures)
- `packaging/` — `installer.iss` (Inno Setup; fixed AppId — never regenerate) + `WinLocalASR.bat` (portable launcher)
- `versions.json` — pin manifest template (llama.cpp tag/asset/sha256, GGUF pair pins, mirror URLs); setup writes the installed copy
- `tools/generate_tray_icons.py` — regenerates the four embedded tray ICOs
- `.github/workflows/windows.yml` — Windows CI (full three-job delivery pipeline: build+test+e2e+installer on main; real-inference smoke on dispatch/tags; release on tags)

## Git Rules

- Branch: `main`
- **Repo-local identity is mandatory**: commits must use `369795172@users.noreply.github.com` (already set in `.git/config`; if recreating the clone, run `git config user.email 369795172@users.noreply.github.com && git config user.name marvi` before the first commit). Commit metadata must never carry a personal email.
- Commit in small, reversible units; conventional-commit style (`feat:`, `fix:`, `ci:`, `docs:` …)
- Update `docs/working.md` after each meaningful change
- Do not commit secrets, model weights, build artifacts, or the local adhoc spike notes
- This is a public repo — no personal identifiers (real names of non-contributors, machine identifiers, home network details) in any committed file. Test machines are referred to as "reference test machine (Win11 x64)".

## Build & Test

```bash
dotnet build src/WinLocalASR.sln -c Debug
dotnet test src/WinLocalASR.sln
```

- The App project builds on non-Windows hosts via `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; do not remove it.
- Core multi-targets `net10.0;net10.0-windows` on purpose: Windows-only asset gaps (Wasapi/MediaFoundation surface) must surface at compile time, while pure-logic tests still run cross-platform.
- Windows-only runtime tests use platform guards and only execute on windows-latest CI or a real Windows machine.
- End-to-end (requires a published Windows exe — normally CI's job):

```powershell
dotnet publish src/WinLocalASR.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeSatelliteAssembliesInSingleFile=true -o staging/app
tests/e2e.ps1 -ExePath staging/app/WinLocalASR.App.exe
```

- Installer: `ISCC packaging/installer.iss` (version overridable via `/DMyAppVersion=`). Release builds are produced by the CI pipeline only — do not hand-publish releases.

## Key Architecture Decisions

1. **WinForms tray shell + platform-neutral Core** — `NotifyIcon` shell keeps the app tiny; all logic lives in Core with injectable interfaces (timer, clock, dispatcher, audio device factory, HTTP handler) so the state machine is unit-testable without Windows.
2. **Toggle hotkey only** — default `Ctrl+Shift+Space`; `Esc` is registered only while recording (mirrors the macOS `setCancelShortcutActive` semantics). No VAD, no push-to-talk, no streaming.
3. **NAudio capture → 24 kHz Int16 mono WAV** — WasapiCapture shared mode, MediaFoundation resampler, one WAV file per utterance.
4. **llama.cpp inference** — `llama-server.exe` stays resident; audio is POSTed per utterance. Qwen3-ASR-1.7B Q8_0 (official pair) is the default quant. Exact wire contract lives in `docs/rfc.md` §Inference Contract (spike-derived; treat it as the source of truth for the client).
5. **Clipboard-only output** — text goes to the clipboard; the user pastes with `Ctrl+V`. No simulated keystrokes.
6. **Settings SSOT** — `settings.json` is the single source of truth (HKCU Run key is derived); installs never edit JSON directly.
7. **Recording limit capped at 120 s** — llama.cpp issue #21847 affects >2 min audio; the cap is a deliberate deviation from the macOS original, recorded in the Parity table.
8. **Opt-in ControlServer for automated testing** — `--enable-control-server` opens a localhost test API (port 17846). Normal launches open no socket.
9. **No cloud at runtime** — network allowlist is localhost-only after setup; setup downloads limited to GitHub/Hugging Face (+ mirrors ghproxy.cn / hf-mirror.com for China networks).

## What NOT to do

- Do not add cloud APIs or any runtime network dependency beyond localhost.
- Do not add VAD, push-to-talk, auto-segmentation, or streaming.
- Do not add LLM post-processing, speaker diarization, or SendInput typing.
- Do not swap Qwen3-ASR for Whisper.
- Do not support Win7/8, x86, or ARM64.
- Do not bundle model weights into the installer (setup downloads them).
- Do not purchase code-signing certificates; SmartScreen guidance in README is the accepted UX.

## Maintenance

- After each milestone: commit, update `docs/working.md`, confirm build + tests still pass (CI is the Windows authority).
- Privacy scan before any public push: `rg -n "real\.email|real\.phone|op://|internal\.path" .` must return zero matches; commit emails must be noreply only (`git log --format='%ae %ce' | sort -u`).
