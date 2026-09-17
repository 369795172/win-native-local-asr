# working.md — WinLocalASR

Changelog and lessons learned. Newest first. Every meaningful change gets an entry before its commit.

## Changelog

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
