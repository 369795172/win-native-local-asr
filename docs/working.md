# working.md — WinLocalASR

Changelog and lessons learned. Newest first. Every meaningful change gets an entry before its commit.

## Changelog

### 2026-09-17 — audio capture module (Task 3)

- `feat(audio): 24khz mono capture, wav writer, level meter`: ported the capture semantics of `AudioCaptureManager.swift` into `src/WinLocalASR.Core/Audio/`. Public surface maps 1:1 to Swift (`SampleRate` 24000, `OnMaximumDuration`, `OnAudioLevel`, `SelectedDeviceId`, `MaximumDuration` default 60 s, `ListInputDevices`, `StartRecording(deviceId?)`, `StopRecording` → `%TEMP%\WinLocalASR-<guid>.wav`, `CancelRecording`), and `AudioCaptureException` subclasses mirror the six `AudioCaptureError` cases for the later state-machine port.
- Seam design: all device access goes through `IAudioDeviceFactory` (`WindowsAudioDeviceFactory` = MMDeviceEnumerator + WasapiCapture shared mode); format conversion goes through `IPcmResamplerFactory` (`MediaFoundationResamplerFactory` = MediaFoundationResampler over a BufferedWaveProvider, float→int16 done in managed code so MF only resamples PCM16). Same-format input takes an identity path that never builds a resampler. Level formula is the Swift lines 219-227 port in `AudioLevelMeter` ((dB+80)/70 clamped to [0,1]); WAV encoding is the canonical 44-byte RIFF header in `Pcm16WavWriter` — the interchange format consumed by `POST /v1/audio/transcriptions`.
- Test inventory (21 total): 19 cross-platform (440 Hz sine injected through the fake factory into accumulation + WAV writer, parsed at byte level: RIFF/24000 Hz/mono/16-bit/1 s ±100 ms; level-formula values including the quarter-scale-square exact-math case; QA-: null factory → `NoInputDeviceException`, empty buffer → `NoAudioCapturedException`, plus not-recording/cancel/double-start/selected-device-id behaviors) + 2 Windows-only `SkippableFact` (full MediaFoundationResampler chain 48 kHz stereo float → 24 kHz mono WAV, no hardware needed; device enumeration tolerates a zero-capture-device runner). Local macOS: 19 passed / 2 skipped.
- QA+ local evidence: `dotnet test src/WinLocalASR.sln` → passed 19, skipped 2, failed 0. Windows CI (windows-latest, run 35174803639, 1m16s): passed 21, skipped 0, failed 0 — both Windows-only tests executed for real: https://github.com/369795172/win-native-local-asr/actions/runs/35174803639
- First CI attempt (35173958134) hung in the MF chain test and was cancelled: `BufferedWaveProvider.ReadFully` defaults to true and zero-pads empty reads, so the resampler drain loop emitted endless silence. Fix in `b66509a`: `ReadFully = false` (read-loop now terminates on genuine exhaustion).

### 2026-09-17 — inference contract backfill (Task 1)

- `docs: backfill inference contract from spike`: filled `docs/rfc.md` §Inference Contract from the macOS spike — server command line (`llama-server -m <main.gguf> --mmproj <mmproj.gguf> --host 127.0.0.1 --port <P>`), multipart field table for `POST /v1/audio/transcriptions` (`file` required; `model`/`prompt`/`temperature` optional; `response_format` must be `json` or omitted), non-OpenAI response shape with verbatim EN/ZH samples and the `^language\s+[^<]*<asr_text>` artifact-strip rule, `GET /health` ready-poll, `/shutdown` absent → Stop() = graceful attempt then process kill after 2 s, model pins + SHA256, quant ruling (no official Q4_K_M exists → Q8_0 pair ~2.5 GB supersedes the ~1.9 GB estimate), validation context (brew llama.cpp 0.4.1 `b10964-b29c606e2`; Windows pin = any release tagged after the 2026-04-12 qwen3-asr merge), 120 s cap kept as conservative guardrail (#21847 NOT reproduced on b10964), and the chat-completions base64 route documented as fallback only.
- Committed `tests/assets/spike-sample.wav` (1.4 s EN) + `tests/assets/spike-sample-10s.wav` (8.7 s EN) as binary-exact spike copies — future fixtures for Task 4 artifact-stripping unit tests and the Task 13 Windows inference smoke job.
- Corrected all stale Q4_K_M / ~1.9 GB references repo-wide (README, AGENTS.md decision 4, prd.md, rfc.md anchor decisions / Parity Setup row / D3) to the Q8_0 / ~2.5 GB ruling; Parity rows updated for the honest #21847 finding and the now-known `prompt` hotword field.

### 2026-09-17 — bootstrap

- `chore: bootstrap win-native-local-asr` (cccf0c9): repository skeleton — `windows.yml` CI (checkout → setup-dotnet 10.x → build + test on windows-latest), MIT LICENSE (project contributors), NOTICE crediting grapeot/mac-native-local-asr, bilingual README (EN + zh-CN, SmartScreen install guidance placeholder, Credits), AGENTS.md, `docs/{prd,rfc,working,test}.md`, and the solution: Core (multi-target `net10.0;net10.0-windows` class library), App (`net10.0-windows` WinForms with `EnableWindowsTargeting`), Tests (xunit + smoke test).
- Verified: local `dotnet build src/WinLocalASR.sln` + `dotnet test` green on macOS (Core builds both TFMs; App builds cross-platform via `EnableWindowsTargeting`); first CI run green on windows-latest: https://github.com/369795172/win-native-local-asr/actions/runs/35171064497
- QA+: fresh `gh repo clone` structure check — all 16 tracked files present:

  ```
  .github/workflows/windows.yml   .gitignore      AGENTS.md    LICENSE
  NOTICE                          README.md       docs/prd.md  docs/rfc.md
  docs/test.md                    docs/working.md
  src/WinLocalASR.sln
  src/WinLocalASR.Core/WinLocalASR.Core.csproj
  src/WinLocalASR.App/{Program.cs, WinLocalASR.App.csproj}
  src/WinLocalASR.Tests/{SmokeTests.cs, WinLocalASR.Tests.csproj}
  ```

- QA-: deliberately broke `TargetFrameworks` (`net99.0`) → build fails with readable `NETSDK1045` naming the offending project/property; restored and rebuilt green.

## Lessons learned

- .NET 10 `dotnet new sln` defaults to the new `.slnx` XML format. Pass `--format sln` when the classic solution file is required (CI paths, older tooling, editor support).
- xunit types are not covered by `ImplicitUsings`; test files need an explicit `using Xunit;`.
- Referencing the NAudio metapackage from a project that multi-targets `net10.0-windows` requires `<EnableWindowsTargeting>true</EnableWindowsTargeting>` on non-Windows hosts: `NAudio.Winsows` carries a `Microsoft.WindowsDesktop.App.WindowsForms` FrameworkReference that cross-platform builds cannot resolve otherwise (NETSDK1073).
