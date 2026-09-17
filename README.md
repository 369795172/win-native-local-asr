# WinLocalASR

[![windows](https://github.com/369795172/win-native-local-asr/actions/workflows/windows.yml/badge.svg)](https://github.com/369795172/win-native-local-asr/actions/workflows/windows.yml) [![Release](https://img.shields.io/badge/release-v0.1.0--prerelease-blue)](https://github.com/369795172/win-native-local-asr/releases/tag/v0.1.0)

Offline voice dictation for Windows 10+ (x64). Press a global hotkey, speak, and your words are copied to the clipboard — no cloud, no internet after first-time setup.

WinLocalASR 是一款 Windows 10+（x64）离线语音听写工具。按下全局热键、说话，转写文本自动进剪贴板；首次配置后完全离线运行，不依赖任何云服务。

## Features

- Lives in the system tray — no window, no taskbar clutter（常驻系统托盘，无主窗口）
- Global hotkey (default `Ctrl+Shift+Space`) toggles recording; `Esc` cancels mid-recording（全局热键切换录音，录音中 Esc 取消）
- 24 kHz mono capture with a live level meter（24 kHz 单声道采集 + 实时电平表）
- Local transcription via Qwen3-ASR-1.7B (GGUF, Q8_0) on llama.cpp — CPU with AVX2 is enough, no GPU required（本地 llama.cpp 推理，AVX2 CPU 即可运行，无需 GPU）
- Transcript goes to the clipboard — paste with `Ctrl+V` anywhere（转写结果进剪贴板，随处粘贴）
- HUD overlay feedback: recording timer + level bars, processing spinner, copied / cancelled / error states（悬浮 HUD 实时反馈）
- Configurable: hotkey, input device, hotwords/context prompt, recording limit (10–120 s), autostart（热键/设备/热词/录音上限/开机自启均可配置）
- Bilingual UI: English + 简体中文, following system locale（界面双语，跟随系统语言）
- First-run setup downloads the engine + model once (~2.5 GB) with automatic China-friendly mirrors and interrupted-download resume（首装自动下载，国内网络自动走镜像，支持断点续传）

**Status**: v0.1.0 prerelease. The full pipeline (build, tests, end-to-end run, real-inference smoke, installer regression) is verified on every push by GitHub Actions. Validation on physical hardware is still in progress — see [docs/test.md](docs/test.md) §Real-Machine.

## Requirements

- Windows 10 or later, x64
- CPU with AVX2
- ~2.6 GB free disk space (engine ~18 MB + model ~2.5 GB)

## Install

### Option A — installer (recommended)

1. Download `WinLocalASR-Setup-vX.Y.Z-x64.exe` from [Releases](https://github.com/369795172/win-native-local-asr/releases).
2. Run it. The binary is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**.（未购买代码签名证书；SmartScreen 提示「Windows 已保护你的电脑」时，点击「更多信息 → 仍要运行」。）
3. Launch WinLocalASR from the Start menu. First launch opens a guided setup that downloads the engine + speech model (~2.5 GB; a China-friendly mirror is used automatically when the direct download fails).（首次启动引导下载模型，国内网络自动走镜像，支持断点续传。）
4. Grant microphone permission when Windows asks.（按系统提示授权麦克风。）

### Option B — portable zip

1. Download `WinLocalASR-vX.Y.Z-portable-win-x64.zip` from [Releases](https://github.com/369795172/win-native-local-asr/releases) and extract it anywhere.
2. Run `WinLocalASR.bat` (or `WinLocalASR.App.exe` directly). SmartScreen guidance applies the same as above.
3. The same first-run setup runs on first launch.

The app is fully offline after setup completes — you can disconnect the network.

## Usage

1. WinLocalASR sits in the system tray. Left-click the tray icon to open the menu:
   - **Setup…** — run/repair first-time setup (bold while not configured)
   - **Settings…** — hotkey, input device, hotwords, recording limit, autostart
   - **Copy last transcript** — re-copy the previous transcript
   - **Restart engine** — restart the local inference server
   - **Exit**（托盘菜单：Setup / 设置 / 复制上次转写 / 重启引擎 / 退出）
2. Press `Ctrl+Shift+Space` (or your custom hotkey). A red HUD appears in the bottom-right corner with a timer and level bars — speak now.（按下热键，右下角出现红色 HUD 与电平表，开始说话。）
3. Press the hotkey again to stop. The HUD turns amber while transcribing, then green ✓ — the text is on the clipboard, paste with `Ctrl+V`.（再按一次停止；转写完成显示绿色 ✓，Ctrl+V 粘贴。）
4. Press `Esc` while recording to cancel and discard. Recording also auto-stops at the configured limit (default 120 s).（录音中按 Esc 取消；达到上限自动停止。）
5. Tip: put names or jargon into Settings → hotwords (context prompt) to bias recognition.（把专有名词填进「热词」可提高识别准确率。）

Note: while recording, `Esc` is captured system-wide (pressing it in any application cancels the recording). Outside recording, Esc is never touched. This mirrors the macOS original — see [docs/prd.md](docs/prd.md) "Known tradeoffs".

## Troubleshooting

- **Transcription fails with a microphone error / level meter stays flat**（麦克风被拒/电平表无反应）:
  Windows may be blocking microphone access for desktop apps. Open **Settings → Privacy & security → Microphone** and enable both "Let apps access your microphone" and "Let desktop apps access your microphone", then restart WinLocalASR. Check that the correct input device is selected in **Settings**.
- **The hotkey does nothing / a balloon says the hotkey is occupied**（热键无效/托盘气泡提示冲突）:
  Another application has claimed the combination. WinLocalASR keeps the previously working hotkey and shows the conflict as a balloon plus a red hint in Settings. Open **Settings → hotkey**, press a different combination (any `Ctrl/Alt/Shift/Win + key`), Apply.（其他程序占用了组合键；在设置里换一个组合即可，冲突会在设置页标红提示。）
- **Transcription is slow on a low-end CPU**（低配 CPU 转写慢）:
  All transcription runs locally on your CPU; on low-end machines the wait after stop will be longer than on the reference machine. Close CPU-heavy applications (browsers with many tabs, games, video encoders) while dictating. The model ships **only** in the Q8_0 quantization (the official GGUF release provides no smaller quant), so there is no "lighter" official variant to switch to; expect proportionally longer waits for longer recordings.（转写完全在本地 CPU 进行；关闭高负载程序可明显改善。官方仅提供 Q8_0 量化，没有更小的官方版本可换。）
- **First-run setup download fails or is very slow**（首装下载失败/极慢）:
  The setup automatically falls back to China-friendly mirrors (hf-mirror.com for the model, ghproxy.cn for the engine) and resumes interrupted downloads — just run Setup again from the tray menu; finished pieces are not re-downloaded.（下载中断后重跑 Setup 即可续传，已完成的文件不会重复下载。）
- **Uninstall keeps ~2.5 GB on disk**（卸载后仍有约 2.5 GB 占用）:
  By design the uninstaller leaves the downloaded model directory (`%LOCALAPPDATA%\WinLocalASR`) in place so reinstalls don't re-download; delete that folder manually if you want the space back.（卸载保留模型目录以便重装免下载；需要空间可手动删除 `%LOCALAPPDATA%\WinLocalASR`。）

## FAQ

- **Does it send my voice anywhere?** No. After first-run setup, the network allowlist is localhost-only; audio is processed by a local llama.cpp process. You can verify by dictating with networking disabled.（完全离线；可断网验证。）
- **Where are files stored?** Engine + models: `%LOCALAPPDATA%\WinLocalASR`. Settings: `%APPDATA%\WinLocalASR\settings.json`.（引擎与模型在 %LOCALAPPDATA%\WinLocalASR，设置在 %APPDATA%\WinLocalASR。）
- **Why does recording stop at 2 minutes?** The recording limit is capped at 120 s as a guardrail against a known upstream issue with >2 min audio; 10–120 s is configurable in Settings.（录音上限 120 秒，可在 10–120 秒内调整。）
- **Is there a GPU build / streaming mode?** Not currently. CPU-only inference, batch transcription per utterance — no streaming, no VAD, by design.（暂无 GPU 构建；无流式/自动分段，属设计取舍。）
- **Which languages?** The underlying Qwen3-ASR model is multilingual (English and Chinese are the tested configurations); recognition quality for other languages follows the model itself.（模型多语种；英文与中文为已验证配置。）

## Development

```bash
dotnet build src/WinLocalASR.sln
dotnet test src/WinLocalASR.sln
```

The App target is `net10.0-windows` WinForms and builds cross-platform via `EnableWindowsTargeting`; Windows-only tests run on windows-latest CI. CI also runs the ControlServer-driven e2e (`tests/e2e.ps1`), a real-inference smoke job on tags, and installer regression — see [AGENTS.md](AGENTS.md) and [docs/test.md](docs/test.md).

## Credits

- **[grapeot/mac-native-local-asr](https://github.com/grapeot/mac-native-local-asr)** — the macOS original this project ports to Windows (MIT). Its design shaped the hotkey flow, setup experience, HUD behavior, and documentation structure. See [NOTICE](NOTICE).
- [llama.cpp](https://github.com/ggml-org/llama.cpp) — local inference backend serving Qwen3-ASR.
- Qwen3-ASR (Alibaba Qwen Team) — the speech recognition model, [ggml-org/Qwen3-ASR-1.7B-GGUF](https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF) quantized release.

## License

MIT — see [LICENSE](LICENSE).
