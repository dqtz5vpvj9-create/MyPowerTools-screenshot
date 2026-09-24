using System.Text.Json;

namespace Screenshot.MyPowerTools;

public static class ScreenshotSnowShotHotkey
{
    // Snow Shot's schema default for global_shortcuts/screenshot on macOS. Qt portable
    // text "Meta" is the physical Control key on Apple platforms, so this is Control+1.
    public const string MacDefaultPortableShortcut = "Meta+1";

    public static string? ReadFromConfigJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("global_shortcuts", out var shortcuts) ||
                !shortcuts.TryGetProperty("screenshot", out var screenshot) ||
                screenshot.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in screenshot.EnumerateArray())
            {
                // Current builds write {"portable": "Meta+1", "physical_keys": {...}};
                // shortcut lists may also hold plain portable strings.
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var plain = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        return plain.Trim();
                    }

                    continue;
                }

                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (entry.TryGetProperty("portable", out var portable) &&
                    portable.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(portable.GetString()))
                {
                    return portable.GetString()!.Trim();
                }

                foreach (var property in entry.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var gesture = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(gesture))
                        {
                            return gesture.Trim();
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Converts Snow Shot's Qt portable shortcut text to MyPowerTools gesture notation.
    /// On macOS Qt maps "Ctrl" to Command and "Meta" to Control, while MyPowerTools maps
    /// "Ctrl" to Control and "Cmd"/"Meta" to Command, so the two modifiers swap there.
    /// </summary>
    public static string ToGesture(string portableText, ScreenshotPlatform platform)
    {
        var tokens = portableText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => MapToken(token, platform));
        return string.Join('+', tokens);
    }

    private static string MapToken(string token, ScreenshotPlatform platform)
    {
        if (platform == ScreenshotPlatform.MacOs)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                return "Cmd";
            }

            if (token.Equals("Meta", StringComparison.OrdinalIgnoreCase))
            {
                return "Ctrl";
            }
        }

        if (token.Equals("PgDown", StringComparison.OrdinalIgnoreCase))
        {
            return "PageDown";
        }

        return token;
    }

    public static IEnumerable<string> CandidateConfigPaths(string executablePath)
    {
        return CandidateConfigPaths(executablePath, ScreenshotPlatform.Windows, null, null);
    }

    public static IEnumerable<string> CandidateConfigPaths(
        string executablePath,
        ScreenshotPlatform platform,
        string? userProfile,
        string? roamingAppData)
    {
        foreach (var path in PortableConfigPaths(executablePath))
        {
            yield return path;
        }

        // Without a portable marker Snow Shot keeps config.json in Qt's AppDataLocation,
        // <organization>/<application> = SnowShot/snow_shot.
        if (platform == ScreenshotPlatform.MacOs && !string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Library", "Application Support", "SnowShot", "snow_shot", "config.json");
        }
        else if (platform == ScreenshotPlatform.Windows && !string.IsNullOrWhiteSpace(roamingAppData))
        {
            yield return Path.Combine(roamingAppData, "SnowShot", "snow_shot", "config.json");
        }
    }

    private static IEnumerable<string> PortableConfigPaths(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            yield break;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            yield break;
        }

        yield return Path.Combine(directory, "portable", "config.json");
        yield return Path.Combine(directory, "config.json");

        var parent = Directory.GetParent(directory)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            yield return Path.Combine(parent, "portable", "config.json");
            yield return Path.Combine(parent, "config.json");
        }
    }
}
