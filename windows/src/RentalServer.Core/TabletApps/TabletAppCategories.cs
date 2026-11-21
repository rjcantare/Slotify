namespace RentalServer.Core.TabletApps;

public static class TabletAppCategories
{
    public const string Games = "games";
    public const string Browser = "browser";
    public const string VideoSocial = "video_social";
    public const string Tools = "tools";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        Games,
        Browser,
        VideoSocial,
        Tools,
        Other
    ];

    public static string Normalize(string? category)
    {
        var value = (category ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(value, StringComparer.Ordinal) ? value : Other;
    }

    public static bool IsKnown(string? category)
    {
        var value = (category ?? string.Empty).Trim().ToLowerInvariant();
        return All.Contains(value, StringComparer.Ordinal);
    }

    public static string ToDisplayName(string category)
    {
        return Normalize(category) switch
        {
            Games => "Games",
            Browser => "Browser",
            VideoSocial => "Video & Social",
            Tools => "Tools",
            _ => "Other"
        };
    }
}
