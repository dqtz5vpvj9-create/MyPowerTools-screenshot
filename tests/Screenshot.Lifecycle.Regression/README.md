# Screenshot lifecycle regression

Run with .NET 10, from this directory:

```sh
dotnet build Screenshot.Lifecycle.Regression.csproj --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet bin/Debug/net10.0/Screenshot.Lifecycle.Regression.dll
```

This dependency-free console regression links the current production lifecycle, session,
resolver, installer, and environment sources. Minimal SDK contract stand-ins allow it to
run without package feeds. It does not validate the production SDK package ABI or UI.

All downloads use an in-memory HTTP handler. Installation uses unique temporary directories
and inert text files named `snow_shot.exe`; no executable, platform input, registry probe,
credential source, real HTTP endpoint, or desktop service is invoked. A fake environment
counts resident starts and stops. The tests await the actual install continuation, covering
stop, disable, dispose, settings changes, fresh requests, and preservation of user processes.

Installer safety cases also exercise repair with existing portable and root/bin settings,
archive-default precedence, executable layout changes, checksums, missing payloads, copy
conflicts, replacement rollback, retained backups after a failed rollback, and refusing to
follow settings links. Filesystem changes are limited to unique synthetic temp directories.
An internal move delegate injects deterministic replacement/rollback failures; the default
production path uses `Directory.Move`.
The symlink fixture reports `SKIP` (not `PASS`) if Windows denies the required symbolic-link
privilege. It does not request elevation or change Developer Mode.

Stalled streams return a partial body and then wait until cancellation. A short HTTP timeout
verifies that an archive or checksum stall ends the shared install, closes the stream, leaves
the resident stopped, and permits a successful explicit retry. The test always releases the
fake stream, even against an unfixed baseline. No live network request is possible through
these handlers. `--list` prints all case names without creating or executing any fixture.

For source-linked baseline comparison, `-p:ScreenshotInstallerSource=/absolute/path/to/source.cs`
overrides just the installer. An older baseline needs only the internal optional move-delegate
test seam (the original deletion and installation behavior must remain unchanged). This test
project does not validate native Windows file-lock/ACL behavior, macOS installation, UI,
publisher endpoints, or production SDK package ABI.
