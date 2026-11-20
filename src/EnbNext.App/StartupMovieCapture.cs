namespace EnbNext.App;

internal static class StartupMovieCapture
{
    public static (string? Prefix, string[] Remaining) Parse(string[] args)
    {
        string? prefix = null;
        var remaining = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            const string option = "-CREATEMOVIE";
            if (!args[i].StartsWith(option, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(args[i]);
                // Credential values are opaque, even if they resemble an option.
                if ((args[i] is "--username" or "--password" or "--character" or "--server" or "--group" or "--member" or "--launch-pipe") && i + 1 < args.Length) remaining.Add(args[++i]);
                continue;
            }
            // 9CF860 matches the option prefix; 9CFCE0 accepts its suffix or next argument.
            string value = args[i][option.Length..].TrimStart();
            if (value.Length == 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-')) value = args[++i].TrimStart();
            prefix ??= value.Length == 0 ? "Frames" : value;
        }
        return (prefix, remaining.ToArray());
    }
}
