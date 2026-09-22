using System.Text.Json;

namespace Screenshot.MyPowerTools;

public static class ScreenshotSnowShotHotkey
{
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
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
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

    public static IEnumerable<string> CandidateConfigPaths(string executablePath)
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
