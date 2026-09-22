# Screenshot

MyPowerTools launches an existing open-source screenshot program and leaves the capture UI there.

- Windows and macOS: [Snow Shot](https://github.com/mg-chao/snow-shot) (`snow_shot.exe`, or `snowshot.exe` on older builds)
- Linux: [Flameshot](https://github.com/flameshot-org/flameshot) (`flameshot`, capture via `flameshot gui`)

Both programs are GPL-3.0. This module does not link or vendor them. It looks up a local install, can start it when the module starts, and stops only the process it started itself. Capture shortcuts stay registered by Snow Shot or Flameshot. Snow Shot has no capture CLI on current Windows/macOS builds (`--help` is silent; a second instance only shows the main window). **立即截图** therefore ensures Snow Shot is running, then sends the screenshot hotkey from Snow Shot's own `portable/config.json` (`global_shortcuts.screenshot`, for example `Alt+A`).

Snow Shot is not installed by this module. On Windows the usual package is `winget install --id mg-chao.snow-shot -e`. A 4K or multi-monitor display that renders poorly should use Snow Shot's fixedRuntime build, then set that executable path in Screenshot settings.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\screenshot\build.ps1 -MyPowerToolsRepoRoot .
```
