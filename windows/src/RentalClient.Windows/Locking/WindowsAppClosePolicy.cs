namespace RentalClient.Windows.Locking;

public sealed record ProcessCloseCandidate(
    int ProcessId,
    string ProcessName,
    int SessionId,
    bool HasMainWindow);

public static class WindowsAppClosePolicy
{
    private static readonly HashSet<string> DefaultExcludedProcessNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost",
        "csrss",
        "ctfmon",
        "dwm",
        "explorer",
        "fontdrvhost",
        "lockapp",
        "logonui",
        "lsass",
        "rentalclient.windows",
        "runtimebroker",
        "searchhost",
        "services",
        "shellexperiencehost",
        "sihost",
        "smartscreen",
        "smss",
        "startmenuexperiencehost",
        "svchost",
        "system",
        "taskhostw",
        "textinputhost",
        "userinit",
        "wininit",
        "winlogon",
        "winstore.app",
        "wmiprvse"
    };

    public static IReadOnlySet<string> DefaultExclusions => DefaultExcludedProcessNames;

    public static bool ShouldClose(
        ProcessCloseCandidate candidate,
        int currentProcessId,
        int currentSessionId,
        IReadOnlySet<string>? extraExcludedProcessNames = null)
    {
        if (candidate.ProcessId == currentProcessId)
        {
            return false;
        }

        if (candidate.SessionId != currentSessionId)
        {
            return false;
        }

        if (!candidate.HasMainWindow)
        {
            return false;
        }

        if (DefaultExcludedProcessNames.Contains(candidate.ProcessName))
        {
            return false;
        }

        if (extraExcludedProcessNames?.Contains(candidate.ProcessName) == true)
        {
            return false;
        }

        return true;
    }
}
