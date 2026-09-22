using System.Text.RegularExpressions;

namespace Screenshot.MyPowerTools;

public static partial class ScreenshotCaptureHelp
{
    public static IReadOnlyList<string> Parse(string? help)
    {
        if (string.IsNullOrWhiteSpace(help))
        {
            return [];
        }

        if (CaptureFlag().IsMatch(help))
        {
            return ["--capture"];
        }

        foreach (var line in help.Split('\n'))
        {
            var token = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(token, "gui", StringComparison.OrdinalIgnoreCase))
            {
                return ["gui"];
            }
        }

        return [];
    }

    [GeneratedRegex(@"(^|\s)--capture($|\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaptureFlag();
}
