using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Screenshot.MyPowerTools;

public sealed record ScreenshotInstallStatus(string State, string Message)
{
    public static readonly ScreenshotInstallStatus Idle = new("idle", "");
    public bool InProgress => State == "installing";
}

/// <summary>
/// Downloads the official Snow Shot build from the publisher's fixed setup URLs and installs it
/// for the current user, so a missing backend does not leave the user with manual steps.
/// Every download is checked against the SHA-256 the publisher lists next to it.
/// </summary>
public sealed class ScreenshotSnowShotInstaller
{
    public const string SetupBaseUrl = "https://snowshot.top/setup/";
    public const string MacDiskImage = "snow-shot_macos-arm64.dmg";
    public const string WindowsPortableArchive = "snow-shot_windows-x64-portable.zip";
    public const string WindowsChecksums = WindowsPortableArchive + ".sha256";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(3);

    private readonly ScreenshotPlatform _platform;
    private readonly string? _userProfile;
    private readonly string? _localAppData;
    private readonly Func<HttpClient> _httpFactory;
    private readonly object _gate = new();
    private Task<ScreenshotInstallStatus>? _running;
    private ScreenshotInstallStatus _status = ScreenshotInstallStatus.Idle;

    public ScreenshotSnowShotInstaller(
        ScreenshotPlatform platform,
        string? userProfile,
        string? localAppData,
        Func<HttpClient>? httpFactory = null)
    {
        _platform = platform;
        _userProfile = userProfile;
        _localAppData = localAppData;
        _httpFactory = httpFactory ?? (() => new HttpClient { Timeout = DownloadTimeout });
    }

    public static ScreenshotSnowShotInstaller CreateReal()
    {
        return new ScreenshotSnowShotInstaller(
            SystemScreenshotEnvironment.DetectPlatform(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    public ScreenshotInstallStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool IsSupported(out string reason)
    {
        switch (_platform)
        {
            case ScreenshotPlatform.MacOs when IsAppleSilicon():
                reason = "";
                return true;
            case ScreenshotPlatform.MacOs:
                reason = "Snow Shot 目前只发布 Apple 芯片（M 系列）的 Mac 版本，这台 Intel Mac 无法自动安装。";
                return false;
            case ScreenshotPlatform.Windows when !string.IsNullOrWhiteSpace(_localAppData):
                // The x64 build also runs under Windows on Arm emulation.
                reason = "";
                return true;
            case ScreenshotPlatform.Windows:
                reason = "找不到当前用户的程序目录，无法自动安装 Snow Shot。";
                return false;
            default:
                reason = "Linux 请用发行版的包管理器安装 flameshot。";
                return false;
        }
    }

    /// <summary>
    /// Starts an install unless one is already running. Returns true only for the call that
    /// started it; every caller gets the shared task.
    /// </summary>
    public bool TryStartInstall(out Task<ScreenshotInstallStatus> install)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                install = _running;
                return false;
            }

            if (!IsSupported(out var reason))
            {
                _status = new ScreenshotInstallStatus("unsupported", reason);
                install = Task.FromResult(_status);
                return false;
            }

            _status = new ScreenshotInstallStatus("installing", "正在下载并安装 Snow Shot，完成后会自动启动。");
            _running = Task.Run(RunInstallAsync);
            install = _running;
            return true;
        }
    }

    private async Task<ScreenshotInstallStatus> RunInstallAsync()
    {
        ScreenshotInstallStatus result;
        var workRoot = Path.Combine(Path.GetTempPath(), "mpt-snowshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workRoot);
            var installedPath = _platform == ScreenshotPlatform.MacOs
                ? await InstallMacAsync(workRoot).ConfigureAwait(false)
                : await InstallWindowsAsync(workRoot).ConfigureAwait(false);
            result = new ScreenshotInstallStatus("installed", $"Snow Shot 已安装到 {installedPath}。");
        }
        catch (Exception exception)
        {
            result = new ScreenshotInstallStatus(
                "failed",
                $"自动安装 Snow Shot 失败：{exception.Message} 可以稍后在截图页面点「安装 Snow Shot」重试，或从 https://snowshot.top/ 手动安装。");
        }
        finally
        {
            TryDeleteDirectory(workRoot);
        }

        lock (_gate)
        {
            _status = result;
        }

        return result;
    }

    private async Task<string> InstallMacAsync(string workRoot)
    {
        if (string.IsNullOrWhiteSpace(_userProfile))
        {
            throw new InvalidOperationException("找不到当前用户目录。");
        }

        using var http = _httpFactory();
        var image = Path.Combine(workRoot, MacDiskImage);
        var downloadedName = await DownloadAsync(http, SetupBaseUrl + MacDiskImage, image).ConfigureAwait(false);
        var expected = ParseChecksum(
            await http.GetStringAsync(SetupBaseUrl + MacDiskImage + ".sha256").ConfigureAwait(false),
            MacDiskImage, downloadedName);
        VerifyChecksum(image, expected);

        var mountPoint = Path.Combine(workRoot, "mount");
        Directory.CreateDirectory(mountPoint);
        Run("/usr/bin/hdiutil", ["attach", image, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mountPoint]);
        try
        {
            var source = Directory.EnumerateDirectories(mountPoint, "*.app")
                .OrderByDescending(path => Path.GetFileName(path).Equals("Snow Shot.app", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw new InvalidOperationException("安装镜像里没有 Snow Shot.app。");

            var applications = Path.Combine(_userProfile, "Applications");
            Directory.CreateDirectory(applications);
            var target = Path.Combine(applications, "Snow Shot.app");
            var staging = Path.Combine(applications, ".Snow Shot.app.mpt-install");
            TryDeleteDirectory(staging);
            // ditto keeps symlinks, permissions and the code signature seal of the bundle.
            Run("/usr/bin/ditto", [source, staging]);
            TryDeleteDirectory(target);
            Directory.Move(staging, target);
            // Downloaded bytes carry no quarantine flag from HttpClient, but clear any inherited one.
            TryRun("/usr/bin/xattr", ["-dr", "com.apple.quarantine", target]);
            return target;
        }
        finally
        {
            TryRun("/usr/bin/hdiutil", ["detach", mountPoint, "-force"]);
        }
    }

    private async Task<string> InstallWindowsAsync(string workRoot)
    {
        using var http = _httpFactory();
        var archive = Path.Combine(workRoot, WindowsPortableArchive);
        var downloadedName = await DownloadAsync(http, SetupBaseUrl + WindowsPortableArchive, archive).ConfigureAwait(false);
        // The aggregate SHA256SUMS can lag behind the stable URL's release redirect.
        // Use the publisher's per-asset checksum, which follows the current release.
        var expected = ParseChecksum(
            await http.GetStringAsync(SetupBaseUrl + WindowsChecksums).ConfigureAwait(false),
            WindowsPortableArchive, downloadedName);
        VerifyChecksum(archive, expected);

        // Extract next to the destination so the final move never crosses volumes.
        var programs = Path.Combine(_localAppData!, "Programs");
        Directory.CreateDirectory(programs);
        var extracted = Path.Combine(programs, ".snow-shot-mpt-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(archive, extracted);
            return MoveWindowsPayload(extracted, programs);
        }
        finally
        {
            TryDeleteDirectory(extracted);
        }
    }

    internal static string MoveWindowsPayload(string extracted, string programs, Action<string, string>? moveDirectory = null)
    {
        moveDirectory ??= Directory.Move;
        var executable = Directory.EnumerateFiles(extracted, ScreenshotBackendResolver.SnowShotExecutable, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"下载的压缩包里没有 {ScreenshotBackendResolver.SnowShotExecutable}。");
        var payloadRoot = Path.GetDirectoryName(executable)!;
        if (string.Equals(Path.GetFileName(payloadRoot), "bin", StringComparison.OrdinalIgnoreCase))
        {
            payloadRoot = Path.GetDirectoryName(payloadRoot)!;
        }

        var target = Path.Combine(programs, "Snow Shot");
        // Prepare the replacement before changing the current install. A repair may be missing
        // the executable but still contain the user's only copy of their portable settings.
        PreserveWindowsUserData(target, payloadRoot, Path.GetDirectoryName(executable)!);
        var backup = Path.Combine(programs, ".snow-shot-mpt-backup-" + Guid.NewGuid().ToString("N"));
        var hadTarget = Directory.Exists(target);
        if (hadTarget)
        {
            moveDirectory(target, backup);
        }

        try
        {
            moveDirectory(payloadRoot, target);
        }
        catch (Exception installError)
        {
            if (hadTarget)
            {
                try
                {
                    moveDirectory(backup, target);
                }
                catch (Exception rollbackError)
                {
                    // Never clean up this backup: it is the surviving copy of the user's data.
                    throw new IOException($"安装替换失败，原安装和设置已保留在 {backup}。", new AggregateException(installError, rollbackError));
                }
            }
            throw;
        }

        TryDeleteDirectory(backup);
        return target;
    }

    private static void PreserveWindowsUserData(string target, string payloadRoot, string executableDirectory)
    {
        RejectUserDataLink(target);
        RejectUserDataLink(Path.Combine(target, "bin"));
        // Keep both supported layouts, including companion portable data and root-level config.
        foreach (var relative in new[] { "portable", "config.json", Path.Combine("bin", "portable"), Path.Combine("bin", "config.json") })
        {
            CopyUserData(Path.Combine(target, relative), Path.Combine(payloadRoot, relative));
        }

        // If a release switches between root and bin layouts, copy settings beside the new
        // executable only when that location has no existing user data of its own.
        var oldBin = Path.Combine(target, "bin");
        var oldExecutableDirectory = File.Exists(Path.Combine(target, ScreenshotBackendResolver.SnowShotExecutable))
            ? target
            : File.Exists(Path.Combine(oldBin, ScreenshotBackendResolver.SnowShotExecutable)) ||
              File.Exists(Path.Combine(oldBin, "portable", "config.json")) || File.Exists(Path.Combine(oldBin, "config.json"))
                ? oldBin
                : target;
        if (Path.GetRelativePath(target, oldExecutableDirectory) != Path.GetRelativePath(payloadRoot, executableDirectory))
        {
            var previousDestination = Path.Combine(target, Path.GetRelativePath(payloadRoot, executableDirectory));
            foreach (var name in new[] { "portable", "config.json" })
            {
                if (!Path.Exists(Path.Combine(previousDestination, name)))
                    CopyUserData(Path.Combine(oldExecutableDirectory, name), Path.Combine(executableDirectory, name));
            }
        }
    }

    private static void CopyUserData(string source, string destination)
    {
        if (!Path.Exists(source)) return;
        // Do not follow a junction/symlink outside the install tree. Fail before replacing the
        // old install so unusual layouts retain their original data for manual repair.
        RejectUserDataLink(source);
        var attributes = File.GetAttributes(source);

        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                CopyUserData(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static void RejectUserDataLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"无法安全迁移链接形式的 Snow Shot 设置：{path}");
    }

    private static async Task<string> DownloadAsync(HttpClient http, string url, string destination)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Keep a deadline alive
        // for stream acquisition and every body read as well, including a stalled response.
        var timeout = http.Timeout == Timeout.InfiniteTimeSpan || http.Timeout > DownloadTimeout
            ? DownloadTimeout
            : http.Timeout;
        using var deadline = new CancellationTokenSource(timeout);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, deadline.Token).ConfigureAwait(false);
        // Stable aliases redirect to versioned assets. Match the checksum to the name
        // supplied by that response, never to an arbitrary same-platform release.
        // This name is used only for comparison, never as a filesystem destination.
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        return !string.IsNullOrWhiteSpace(name)
            ? name.Trim('"')
            : Uri.UnescapeDataString((response.RequestMessage?.RequestUri ?? new Uri(url)).Segments.Last());
    }

    /// <summary>
    /// Reads a lowercase SHA-256 either from a single-hash file or from a "hash  name" list.
    /// </summary>
    public static string ParseChecksum(string text, string fileName, string? downloadedFileName = null)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !IsSha256(parts[0]))
            {
                continue;
            }

            if (parts.Length == 1 && lines.Length == 1)
            {
                return parts[0].ToLowerInvariant();
            }

            if (parts.Length == 2)
            {
                var name = parts[1].TrimStart('*').Trim();
                if (name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(downloadedFileName) &&
                     (name.Equals(downloadedFileName, StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith("/" + downloadedFileName, StringComparison.OrdinalIgnoreCase))))
                {
                    return parts[0].ToLowerInvariant();
                }
            }
        }

        throw new InvalidOperationException($"发布方没有提供 {fileName} 的 SHA-256 校验值。");
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static void VerifyChecksum(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("下载文件的 SHA-256 校验不一致，已放弃安装。");
        }
    }

    private static bool IsAppleSilicon()
    {
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return true;
        }

        // An x64 process under Rosetta can still install the arm64 build.
        try
        {
            return Run("/usr/sbin/sysctl", ["-n", "hw.optional.arm64"]).Trim() == "1";
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Run(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 超时。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 失败（{process.ExitCode}）：{error.Result.Trim()}");
        }

        return output.Result;
    }

    private static void TryRun(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            Run(fileName, arguments);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
