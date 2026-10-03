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
    public const string WindowsChecksums = "SHA256SUMS";

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
                $"自动安装 Snow Shot 失败：{exception.Message} 可以稍后在截图页面点「安装 Snow Shot」重试，或从 https://snowshot.top/download/ 手动安装。");
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
        await DownloadAsync(http, SetupBaseUrl + MacDiskImage, image).ConfigureAwait(false);
        var expected = ParseChecksum(
            await http.GetStringAsync(SetupBaseUrl + MacDiskImage + ".sha256").ConfigureAwait(false),
            MacDiskImage);
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
        await DownloadAsync(http, SetupBaseUrl + WindowsPortableArchive, archive).ConfigureAwait(false);
        var expected = ParseChecksum(
            await http.GetStringAsync(SetupBaseUrl + WindowsChecksums).ConfigureAwait(false),
            WindowsPortableArchive);
        VerifyChecksum(archive, expected);

        // Extract next to the destination so the final move never crosses volumes.
        var programs = Path.Combine(_localAppData!, "Programs");
        Directory.CreateDirectory(programs);
        var extracted = Path.Combine(programs, ".snow-shot-mpt-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Extraction can fail after writing some entries (including a rejected traversal).
            // Keep it inside the cleanup scope and isolate simultaneous installer instances.
            ZipFile.ExtractToDirectory(archive, extracted);
            return MoveWindowsPayload(extracted, programs);
        }
        finally
        {
            TryDeleteDirectory(extracted);
        }
    }

    private static string MoveWindowsPayload(string extracted, string programs)
    {
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
        TryDeleteDirectory(target);
        Directory.Move(payloadRoot, target);
        return target;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destination)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a lowercase SHA-256 either from a single-hash file or from a "hash  name" list.
    /// </summary>
    public static string ParseChecksum(string text, string fileName)
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
                    name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
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
