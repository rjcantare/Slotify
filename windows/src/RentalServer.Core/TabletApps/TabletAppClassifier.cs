namespace RentalServer.Core.TabletApps;

public static class TabletAppClassifier
{
    private static readonly string[] GameKeywords =
    [
        // Action / shooter
        "call of duty", "codm", "callofduty", "free fire", "freefire", "pubg",
        "fortnite", "apex legends", "valorant", "counter strike", "csgo",
        // MOBA / strategy
        "mobile legends", "mobilelegends", "wild rift", "wildrift",
        "arena of valor", "honor of kings",
        // Supercell
        "clash of clans", "clashofclans", "clash royale", "clashroyale",
        "brawl stars", "brawlstars", "hay day", "hayday", "boom beach", "boombeach",
        "squad busters", "supercell",
        // Casual / puzzle / arcade
        "8 ball", "8ball", "block blast", "blockblast", "candy crush", "candycrush",
        "fruit ninja", "fruitninja", "angry birds", "angrybirds",
        "among us", "amongus", "fall guys", "fallguys", "stumble guys", "stumbleguys",
        "geometry dash", "geometrydash", "subway surf", "subwaysurf",
        "temple run", "templerun", "crossy road", "crossyroad",
        "flappy bird", "flappybird", "sudoku", "tile", "pizza",
        // Racing / sports
        "racing", "hill climb", "hillclimb", "asphalt", "need for speed", "needforspeed",
        "real racing", "realracing", "fifa", "pes", "efootball",
        "nba", "madden", "mlb", "shadow fight", "shadowfight",
        // Strategy / war
        "last war", "lastwar", "rise of kingdoms", "riseofkingdoms",
        "state of survival", "stateofsurvival", "whiteout survival", "whiteoutsurvival",
        // Sandbox / sim
        "minecraft", "roblox", "melon", "gta", "terraria",
        "stardew valley", "stardewvalley",
        // General RPG / action
        "genshin", "honkai", "zenless", "tower of fantasy",
        "pokemon", "pokémon", "legend of zelda", "diablo", "soul knight",
        // Fighting
        "mortal kombat", "mortalkombat", "tekken", "brawlhalla",
        // Card / board
        "hearthstone", "monopoly", "uno", "chess", "solitaire",
        // Catch-all — these are broad but checked last in this list
        "game",
        "legends"
    ];

    private static readonly string[] VideoSocialKeywords =
    [
        "facebook",
        "instagram",
        "lazada",
        "messenger",
        "music",
        "netflix",
        "reels",
        "shopee",
        "shopping",
        "tiktok",
        "video",
        "youtube"
    ];

    private static readonly string[] BrowserKeywords =
    [
        "browser",
        "brave",
        "chrome",
        "edge",
        "firefox",
        "opera",
        "search"
    ];

    private static readonly string[] ToolKeywords =
    [
        "calculator",
        "calendar",
        "camera",
        "clock",
        "docs",
        "download",
        "drive",
        "files",
        "gallery",
        "gmail",
        "maps",
        "meet",
        "notes",
        "office",
        "photos",
        "settings",
        "translate",
        "weather",
        "zoom"
    ];

    private static readonly string[] GamePublisherPatterns =
    [
        // Publisher domains embedded in Android package names
        "supercell",    // Clash of Clans, Clash Royale, Brawl Stars, Hay Day, Boom Beach, Squad Busters
        "halfbrick",    // Fruit Ninja, Jetpack Joyride
        "rovio",        // Angry Birds series
        "king",         // Candy Crush Saga (com.king.candycrushsaga)
        "innersloth",   // Among Us
        "tencent",      // PUBG Mobile, Call of Duty Mobile
        "moonton",      // Mobile Legends: Bang Bang (com.mobile.legends)
        "garena",       // Free Fire
        "mediocre",     // Does Not Commute, PinOut
        "hutch",        // Top Drives, F1 Manager
        "naturalmotion",// CSR Racing
        "gameloft",     // Asphalt series
        "minecraftpe",  // Bedrock edition
        "mojang",       // Minecraft
        "netease",      // LifeAfter, Identity V
        "yzgamedev",    // Block Blast (com.yzgamedev.blockblast)
        "playgendary",  // Various hypercasual games
        "voodoo",       // Hypercasual publisher
        "ketchapp",     // Hypercasual publisher
        "saygames",     // Hypercasual publisher
        "supersonic",   // Hypercasual publisher
        "casualazure",  // Hypercasual publisher
        "fingersoft",   // Hill Climb Racing
        "firstfun",     // Last War: Survival (com.firstfun.lastwar)
        "tapblaze",     // Good Pizza, Great Pizza
        "tile",         // Tile-matching games (com.puzzle.game.tile...)
    ];

    private static readonly HashSet<string> ProtectedPackages = new(StringComparer.Ordinal)
    {
        "android",
        "com.android.settings",
        "com.android.systemui",
        "com.android.vending",
        "com.google.android.gms",
        "com.google.android.gsf",
        "com.google.android.packageinstaller",
        "com.miui.home",
        "com.miui.securitycenter",
        "com.rentalsystem.android"
    };

    public static string InferCategory(string displayName, string packageName)
    {
        var normalizedPackage = (packageName ?? string.Empty).Trim().ToLowerInvariant();
        var haystack = $"{(displayName ?? string.Empty)} {normalizedPackage}".ToLowerInvariant();

        // Publisher domain in package name is a very strong signal — check first.
        if (GamePublisherPatterns.Any(p => normalizedPackage.Contains(p, StringComparison.Ordinal)))
        {
            return TabletAppCategories.Games;
        }

        if (GameKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
        {
            return TabletAppCategories.Games;
        }

        if (BrowserKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
        {
            return TabletAppCategories.Browser;
        }

        if (VideoSocialKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
        {
            return TabletAppCategories.VideoSocial;
        }

        if (ToolKeywords.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
        {
            return TabletAppCategories.Tools;
        }

        return TabletAppCategories.Other;
    }

    public static bool IsProtectedPackage(string packageName)
    {
        var normalized = (packageName ?? string.Empty).Trim();
        return ProtectedPackages.Contains(normalized) ||
               normalized.StartsWith("com.android.launcher", StringComparison.Ordinal) ||
               normalized.StartsWith("com.miui.", StringComparison.Ordinal);
    }
}
