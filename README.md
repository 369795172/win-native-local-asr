# WinLocalASR

[![windows](https://github.com/369795172/win-native-local-asr/actions/workflows/windows.yml/badge.svg)](https://github.com/369795172/win-native-local-asr/actions/workflows/windows.yml)

Offline voice dictation for Windows 10+ (x64). Press a global hotkey, speak, and your words are copied to the clipboard — no cloud, no internet after first-time setup.

WinLocalASR 是一款 Windows 10+（x64）离线语音听写工具。按下全局热键、说话，转写文本自动进剪贴板；首次配置后完全离线运行，不依赖任何云服务。

- Lives in the system tray — no window, no taskbar clutter（常驻系统托盘）
- Global hotkey (default: `Ctrl+Shift+Space`) toggles recording; `Esc` cancels mid-recording（全局热键切换录音，录音中 Esc 取消）
- 24 kHz mono capture with a live level meter（24 kHz 单声道采集 + 实时电平表）
- Local transcription via Qwen3-ASR-1.7B (GGUF, Q8_0) on llama.cpp — CPU (AVX2) is enough（本地 llama.cpp 推理，CPU 即可运行）
- Transcript goes to the clipboard — paste with `Ctrl+V` anywhere（转写结果进剪贴板，随处粘贴）
- Bilingual UI: English + 简体中文, following system locale（界面双语，跟随系统语言）

## Status

Early bootstrap: repository skeleton, CI, and project docs. The app itself arrives over the coming milestones — see [docs/prd.md](docs/prd.md) for scope and [docs/rfc.md](docs/rfc.md) for architecture.

## Install

Releases are not published yet. Once the first installer ships:

1. Download `WinLocalASR-Setup-x64.exe` from [Releases](https://github.com/369795172/win-native-local-asr/releases).
2. The binary is not code-signed, so Windows SmartScreen may warn. Click **More info → Run anyway**（未购买代码签名证书，SmartScreen 提示时选择「更多信息 → 仍要运行」）。
3. First launch runs a guided setup that downloads the speech model (~2.5 GB; a China-friendly mirror is used automatically when needed)（首次启动引导下载模型，国内网络自动走镜像）。
4. Grant microphone permission when prompted.

## Requirements

- Windows 10 or later, x64
- CPU with AVX2
- ~2.5 GB free disk space (model included)

## Development

```bash
dotnet build src/WinLocalASR.sln
dotnet test src/WinLocalASR.sln
```

The App target is `net10.0-windows` WinForms and builds cross-platform via `EnableWindowsTargeting`. See [AGENTS.md](AGENTS.md) for conventions.

## Credits

- **[grapeot/mac-native-local-asr](https://github.com/grapeot/mac-native-local-asr)** — the macOS original this project ports to Windows (MIT). Its design shaped the hotkey flow, setup experience, HUD behavior, and documentation structure. See [NOTICE](NOTICE).
- [llama.cpp](https://github.com/ggml-org/llama.cpp) — local inference backend.
- Qwen3-ASR (Alibaba Qwen Team) — the speech recognition model.

## License

MIT — see [LICENSE](LICENSE).
