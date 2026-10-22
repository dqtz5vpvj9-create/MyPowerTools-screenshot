using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MyPowerTools.Abstractions;
using Screenshot.MyPowerTools;

static class InstallerRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("repair preserves portable settings and companion data over archive defaults", PreserveRepairAsync),
        ("root settings follow a new bin executable layout", () => PreserveLayoutAsync(oldBin: false)),
        ("bin settings follow a new root executable layout", () => PreserveLayoutAsync(oldBin: true)),
        ("fresh install retains archive default settings", FreshInstallAsync),
        ("versioned attachment uses current per-asset checksum instead of stale aggregate", () => VersionedDownloadAsync(header: true)),
        ("redirected asset name matches checksum without attachment header", () => VersionedDownloadAsync(header: false)),
        ("checksum for another release is rejected without changing installation", WrongReleaseAsync),
        ("macOS versioned checksum matches only the downloaded image", MacChecksumAsync),
        ("checksum failure leaves existing settings and install untouched", ChecksumFailureAsync),
        ("missing archive executable leaves old install and no extraction residue", MissingExecutableAsync),
        ("settings copy conflict leaves original installation untouched", CopyConflictAsync),
        ("failed replacement restores original installation and settings", RollbackAsync),
        ("failed rollback retains original installation in the reported backup", FailedRollbackAsync),
        ("settings symlink fails safely without following or replacing it", SymlinkAsync),
        ("stalled archive body times out, releases shared install and permits retry", () => StalledBodyAsync(checksum: false)),
        ("stalled checksum body times out and permits retry", () => StalledBodyAsync(checksum: true))
    ];

    private static async Task PreserveRepairAsync()
    {
        await using var fixture = new Fixture(new()
        {
            ["Snow Shot/snow_shot.exe"] = "new inert executable",
            ["Snow Shot/portable/config.json"] = "publisher defaults",
            ["Snow Shot/portable/new-default.txt"] = "new default",
            ["Snow Shot/config.json"] = "publisher root defaults"
        });
        fixture.Old("portable/config.json", "user settings 未改动\r\n");
        fixture.Old("portable/nested/history.dat", "private local history");
        fixture.Old("config.json", "user root settings");
        fixture.Old("bin/config.json", "user bin settings");
        fixture.Old("obsolete.dll", "old inert application file");
        Equal("installed", (await fixture.InstallAsync()).State);
        Equal("user settings 未改动\r\n", fixture.Read("portable/config.json"));
        Equal("private local history", fixture.Read("portable/nested/history.dat"));
        Equal("new default", fixture.Read("portable/new-default.txt"));
        Equal("user root settings", fixture.Read("config.json"));
        Equal("user bin settings", fixture.Read("bin/config.json"));
        Equal(false, File.Exists(Path.Combine(fixture.Target, "obsolete.dll")));
        fixture.NoInstallResidue();
    }

    private static async Task PreserveLayoutAsync(bool oldBin)
    {
        var newExe = oldBin ? "snow_shot.exe" : "bin/snow_shot.exe";
        await using var fixture = new Fixture(new() { [newExe] = "new inert executable" });
        var oldPrefix = oldBin ? "bin/" : "";
        var newPrefix = oldBin ? "" : "bin/";
        fixture.Old(oldPrefix + "snow_shot.exe", "old inert executable");
        fixture.Old(oldPrefix + "portable/config.json", "active portable settings");
        fixture.Old(oldPrefix + "config.json", "active fallback settings");
        Equal("installed", (await fixture.InstallAsync()).State);
        Equal("active portable settings", fixture.Read(newPrefix + "portable/config.json"));
        Equal("active fallback settings", fixture.Read(newPrefix + "config.json"));
    }

    private static async Task FreshInstallAsync()
    {
        await using var fixture = new Fixture(new() { ["snow_shot.exe"] = "inert", ["portable/config.json"] = "defaults" });
        Equal("installed", (await fixture.InstallAsync()).State);
        Equal("defaults", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static async Task ChecksumFailureAsync()
    {
        await using var fixture = new Fixture();
        fixture.Handler.BadChecksum = true;
        fixture.Old("snow_shot.exe", "old inert executable");
        fixture.Old("portable/config.json", "old settings");
        Equal("failed", (await fixture.InstallAsync()).State);
        Equal("old inert executable", fixture.Read("snow_shot.exe"));
        Equal("old settings", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static async Task VersionedDownloadAsync(bool header)
    {
        await using var fixture = new Fixture();
        fixture.Handler.AssetName = "snow-shot-1.3.5-windows-x64-portable.zip";
        fixture.Handler.ChecksumName = fixture.Handler.AssetName;
        fixture.Handler.UseAttachmentHeader = header;
        Equal("installed", (await fixture.InstallAsync()).State);
        Equal(true, fixture.Handler.UsedPerAssetChecksum);
        fixture.NoInstallResidue();
    }

    private static async Task WrongReleaseAsync()
    {
        await using var fixture = new Fixture();
        fixture.Handler.AssetName = "snow-shot-1.3.5-windows-x64-portable.zip";
        fixture.Handler.ChecksumName = "snow-shot-1.3.4-windows-x64-portable.zip";
        fixture.Old("portable/config.json", "keep settings");
        Equal("failed", (await fixture.InstallAsync()).State);
        Equal("keep settings", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static Task MacChecksumAsync()
    {
        var hash = new string('a', 64);
        const string downloaded = "snow-shot-1.3.5-macos-arm64.dmg";
        Equal(hash, ScreenshotSnowShotInstaller.ParseChecksum(hash + "  " + downloaded,
            ScreenshotSnowShotInstaller.MacDiskImage, downloaded));
        try
        {
            ScreenshotSnowShotInstaller.ParseChecksum(hash + "  snow-shot-1.3.4-macos-arm64.dmg",
                ScreenshotSnowShotInstaller.MacDiskImage, downloaded);
        }
        catch (InvalidOperationException) { return Task.CompletedTask; }
        throw new InvalidOperationException("A checksum for another release was accepted.");
    }

    private static async Task MissingExecutableAsync()
    {
        await using var fixture = new Fixture(new() { ["portable/config.json"] = "archive defaults" });
        fixture.Old("portable/config.json", "old settings");
        Equal("failed", (await fixture.InstallAsync()).State);
        Equal("old settings", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static async Task CopyConflictAsync()
    {
        await using var fixture = new Fixture(new() { ["snow_shot.exe"] = "inert", ["portable"] = "conflicting file" });
        fixture.Old("snow_shot.exe", "old inert executable");
        fixture.Old("portable/config.json", "old settings");
        Equal("failed", (await fixture.InstallAsync()).State);
        Equal("old inert executable", fixture.Read("snow_shot.exe"));
        Equal("old settings", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static async Task RollbackAsync()
    {
        await using var fixture = new Fixture();
        fixture.Old("snow_shot.exe", "old inert executable");
        fixture.Old("portable/config.json", "old settings");
        var extracted = fixture.MakeExtracted();
        var replacementAttempted = false;
        try
        {
            ScreenshotSnowShotInstaller.MoveWindowsPayload(extracted, fixture.Programs, (source, destination) =>
            {
                if (source == extracted)
                {
                    replacementAttempted = true;
                    throw new IOException("synthetic replacement failure");
                }
                Directory.Move(source, destination);
            });
            throw new InvalidOperationException("Expected replacement failure.");
        }
        catch (IOException exception) when (exception.Message == "synthetic replacement failure") { }
        Equal(true, replacementAttempted);
        Equal("old inert executable", fixture.Read("snow_shot.exe"));
        Equal("old settings", fixture.Read("portable/config.json"));
        fixture.NoInstallResidue();
    }

    private static async Task FailedRollbackAsync()
    {
        await using var fixture = new Fixture();
        fixture.Old("snow_shot.exe", "old inert executable");
        fixture.Old("portable/config.json", "old settings");
        var extracted = fixture.MakeExtracted();
        string? backup = null;
        IOException? failure = null;
        try
        {
            ScreenshotSnowShotInstaller.MoveWindowsPayload(extracted, fixture.Programs, (source, destination) =>
            {
                if (source == fixture.Target)
                {
                    backup = destination;
                    Directory.Move(source, destination);
                    return;
                }
                throw new IOException("synthetic move and restore failure");
            });
        }
        catch (IOException exception) { failure = exception; }
        Equal(true, failure is not null && backup is not null && failure.Message.Contains(backup, StringComparison.Ordinal));
        Equal("old settings", File.ReadAllText(Path.Combine(backup!, "portable", "config.json")));
        Equal("old inert executable", File.ReadAllText(Path.Combine(backup!, "snow_shot.exe")));
    }

    private static async Task SymlinkAsync()
    {
        await using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside-settings");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "config.json"), "outside settings");
        Directory.CreateDirectory(fixture.Target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(fixture.Target, "portable"), outside);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() &&
            (exception is UnauthorizedAccessException || exception.HResult == unchecked((int)0x80070522)))
        {
            throw new RegressionSkippedException("This Windows account lacks symbolic-link privilege; enable Developer Mode or run this case with an authorized symlink-capable account.");
        }
        Equal("failed", (await fixture.InstallAsync()).State);
        Equal("outside settings", File.ReadAllText(Path.Combine(outside, "config.json")));
        Equal(true, (File.GetAttributes(Path.Combine(fixture.Target, "portable")) & FileAttributes.ReparsePoint) != 0);
    }

    private static async Task StalledBodyAsync(bool checksum)
    {
        await using var fixture = new Fixture(timeout: TimeSpan.FromMilliseconds(300));
        fixture.Handler.StallArchiveOnce = !checksum;
        fixture.Handler.StallChecksumOnce = checksum;
        var environment = new FakeEnvironment(fixture.Root);
        var module = new ScreenshotModule(new ScreenshotSession(environment), (_, _) => Task.CompletedTask, fixture.Installer);
        try
        {
            var result = await module.ExecuteCommandAsync(new CommandRequest("install", "screenshot.install"), default);
            Equal(true, result.Success);
            await fixture.Handler.Stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(false, fixture.Installer.TryStartInstall(out var shared));
            Equal(false, fixture.Installer.TryStartInstall(out var sameShared));
            Equal(true, ReferenceEquals(shared, sameShared));
            var completed = await Task.WhenAny(shared, Task.Delay(TimeSpan.FromSeconds(2))) == shared;
            // Always release the fake body, so a failing regression also terminates cleanly.
            fixture.Handler.Stalled.Release();
            await shared.WaitAsync(TimeSpan.FromSeconds(5));
            await module.InstallationCompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(true, completed);
            Equal("failed", fixture.Installer.Status.State);
            Equal(true, fixture.Handler.Stalled.CancellationObserved);
            Equal(true, fixture.Handler.Stalled.Disposed);
            Equal(0, environment.Starts);
            fixture.NoInstallResidue();

            result = await module.ExecuteCommandAsync(new CommandRequest("retry", "screenshot.install"), default);
            Equal(true, result.Success);
            await module.InstallationCompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("installed", fixture.Installer.Status.State);
            Equal(1, environment.Starts);
        }
        finally
        {
            fixture.Handler.Stalled.Release();
            await module.InstallationCompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
            await module.DisposeAsync(default);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "screenshot-installer-test-" + Guid.NewGuid().ToString("N"));
        public string Programs => Path.Combine(Root, "Programs");
        public string Target => Path.Combine(Programs, "Snow Shot");
        public Handler Handler { get; }
        public ScreenshotSnowShotInstaller Installer { get; }
        private Task<ScreenshotInstallStatus>? _task;
        public Fixture(Dictionary<string, string>? entries = null, TimeSpan? timeout = null)
        {
            Handler = new Handler(entries ?? new() { ["snow_shot.exe"] = "inert synthetic executable; never run" });
            Installer = new ScreenshotSnowShotInstaller(ScreenshotPlatform.Windows, Root, Root,
                () => new HttpClient(Handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(5) });
        }
        public void Old(string relative, string text)
        {
            var path = Path.Combine(Target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        public string Read(string relative) => File.ReadAllText(Path.Combine(Target, relative));
        public async Task<ScreenshotInstallStatus> InstallAsync()
        {
            Equal(true, Installer.TryStartInstall(out _task));
            return await _task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public string MakeExtracted()
        {
            var extracted = Path.Combine(Root, "extracted");
            Directory.CreateDirectory(extracted);
            File.WriteAllText(Path.Combine(extracted, "snow_shot.exe"), "new inert executable");
            return extracted;
        }
        public void NoInstallResidue()
        {
            if (Directory.Exists(Programs))
                Equal(0, Directory.EnumerateDirectories(Programs, ".snow-shot-mpt-*").Count());
        }
        public async ValueTask DisposeAsync()
        {
            Handler.Stalled.Release();
            if (_task is not null) await _task.WaitAsync(TimeSpan.FromSeconds(5));
            Handler.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly byte[] _archive;
        private readonly string _checksum;
        public bool BadChecksum { get; set; }
        public string AssetName { get; set; } = ScreenshotSnowShotInstaller.WindowsPortableArchive;
        public string ChecksumName { get; set; } = ScreenshotSnowShotInstaller.WindowsPortableArchive;
        public bool UseAttachmentHeader { get; set; }
        public bool UsedPerAssetChecksum { get; private set; }
        public bool StallArchiveOnce { get; set; }
        public bool StallChecksumOnce { get; set; }
        public StalledStream Stalled { get; } = new();
        public Handler(Dictionary<string, string> entries)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, contents) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                    writer.Write(contents);
                }
            _archive = buffer.ToArray();
            _checksum = Convert.ToHexString(SHA256.HashData(_archive));
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content;
            var responseRequest = request;
            if (request.RequestUri?.AbsoluteUri == ScreenshotSnowShotInstaller.SetupBaseUrl + ScreenshotSnowShotInstaller.WindowsPortableArchive)
            {
                content = StallArchiveOnce ? new StreamContent(Stalled) : new ByteArrayContent(_archive);
                StallArchiveOnce = false;
                responseRequest = new HttpRequestMessage(HttpMethod.Get, "https://foruda.gitee.com/attach_file/123/" + AssetName + "?token=synthetic");
                if (UseAttachmentHeader)
                {
                    // The attachment name also works when the redirected path is opaque.
                    responseRequest.RequestUri = new Uri("https://foruda.gitee.com/attach_file/123/download");
                    content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"" + AssetName + "\"" };
                }
            }
            else if (request.RequestUri?.AbsoluteUri == ScreenshotSnowShotInstaller.SetupBaseUrl + "snow-shot_windows-x64-portable.zip.sha256")
            {
                UsedPerAssetChecksum = true;
                content = StallChecksumOnce ? new StreamContent(Stalled) : new StringContent(
                    (BadChecksum ? new string('0', 64) : _checksum) + "  " + ChecksumName);
                StallChecksumOnce = false;
            }
            else if (request.RequestUri?.AbsoluteUri == ScreenshotSnowShotInstaller.SetupBaseUrl + "SHA256SUMS")
                content = new StringContent(new string('0', 64) + "  " + ScreenshotSnowShotInstaller.WindowsPortableArchive);
            else throw new InvalidOperationException("Unexpected fake HTTP route.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = responseRequest });
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _firstRead = true;
        public bool CancellationObserved { get; private set; }
        public bool Disposed { get; private set; }
        public void Release() => _release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                buffer.Span[0] = 0;
                return 1;
            }
            Entered.TrySetResult();
            try { await _release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
