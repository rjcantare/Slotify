using RentalClient.Windows.Locking;

namespace RentalClient.Windows.Tests.Locking;

public sealed class WindowsAppClosePolicyTests
{
    [Fact]
    public void Skips_current_process()
    {
        var candidate = new ProcessCloseCandidate(
            ProcessId: 10,
            ProcessName: "game",
            SessionId: 1,
            HasMainWindow: true);

        Assert.False(WindowsAppClosePolicy.ShouldClose(candidate, currentProcessId: 10, currentSessionId: 1));
    }

    [Fact]
    public void Skips_system_and_shell_processes()
    {
        var explorer = new ProcessCloseCandidate(
            ProcessId: 11,
            ProcessName: "explorer",
            SessionId: 1,
            HasMainWindow: true);

        Assert.False(WindowsAppClosePolicy.ShouldClose(explorer, currentProcessId: 10, currentSessionId: 1));
    }

    [Fact]
    public void Skips_processes_without_main_window()
    {
        var helper = new ProcessCloseCandidate(
            ProcessId: 11,
            ProcessName: "helper",
            SessionId: 1,
            HasMainWindow: false);

        Assert.False(WindowsAppClosePolicy.ShouldClose(helper, currentProcessId: 10, currentSessionId: 1));
    }

    [Fact]
    public void Targets_visible_user_process_in_current_session()
    {
        var game = new ProcessCloseCandidate(
            ProcessId: 11,
            ProcessName: "gta5",
            SessionId: 1,
            HasMainWindow: true);

        Assert.True(WindowsAppClosePolicy.ShouldClose(game, currentProcessId: 10, currentSessionId: 1));
    }
}
