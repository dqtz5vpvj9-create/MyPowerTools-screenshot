# Screenshot

MyPowerTools launches an existing open-source screenshot program and leaves the capture UI there.

- Windows and macOS: [Snow Shot](https://github.com/mg-chao/snow-shot) (`snow_shot.exe`, or `snowshot.exe` on older builds)
- Linux: [Flameshot](https://github.com/flameshot-org/flameshot) (`flameshot`, capture via `flameshot gui`)

Both programs are GPL-3.0. This module does not link or vendor them. It looks up a local install, can start it when the module starts, and stops only the process it started itself. Capture shortcuts stay registered by Snow Shot or Flameshot. Snow Shot has no capture CLI on current Windows/macOS builds (`--help` is silent; a second instance only shows the main window). **立即截图** therefore ensures Snow Shot is running, then sends the screenshot hotkey from Snow Shot's own `portable/config.json` (`global_shortcuts.screenshot`, for example `Alt+A`).

On macOS, **立即截图** sends the same hotkey through the MyPowerTools `keyboard.shortcut` platform service (CoreGraphics `CGEvent`), so MyPowerTools needs Accessibility permission (System Settings › Privacy & Security › Accessibility). The hotkey is read from `~/Library/Application Support/SnowShot/snow_shot/config.json`; without a saved value Snow Shot's macOS default `Control+1` is used. Qt writes Command as `Ctrl` and Control as `Meta` on macOS; the module swaps them into MyPowerTools notation.

Snow Shot is never downloaded automatically. When it is missing, the Screenshot page shows a **下载并安装 Snow Shot** button (command `screenshot.install`). Clicking it downloads the official build from `https://snowshot.top/setup/` (`snow-shot_macos-arm64.dmg` on Apple silicon Macs, `snow-shot_windows-x64-portable.zip` on Windows), checks the SHA-256 the publisher lists next to it (`.sha256` / `SHA256SUMS`), installs it for the current user (`~/Applications/Snow Shot.app`, `%LOCALAPPDATA%\Programs\Snow Shot`) and starts it. Snow Shot publishes no Intel Mac build, so the button is hidden there. On Windows the usual package is `winget install --id mg-chao.snow-shot -e`. A 4K or multi-monitor display that renders poorly should use Snow Shot's fixedRuntime build, then set that executable path in Screenshot settings.

Build from the repository root:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\tools\screenshot\build.ps1 -MyPowerToolsRepoRoot .
```

Installation regression coverage in the parent repository uses synthetic HTTP responses and disposable destinations: verified payload/config installation, checksum and missing-executable rejection, ZIP traversal rejection with partial extraction cleanup, HTTP failure/retry, unsupported environments, and concurrent request deduplication. Run `npx e2e run tests/e2e/screenshot.e2e.ts` after preparing the test build. These checks never download or launch Snow Shot. Each extraction has a unique staging directory and cleanup covers extraction failures.
