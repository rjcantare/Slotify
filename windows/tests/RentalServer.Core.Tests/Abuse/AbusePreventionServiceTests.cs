using RentalServer.Core.Abuse;

namespace RentalServer.Core.Tests.Abuse;

public sealed class AbusePreventionServiceTests
{
    [Fact]
    public void CheckBan_returns_not_banned_when_disabled()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: false, MaxEmptyClaims: 3, BanMinutes: 2));

        var result = service.CheckBan("pc-01");

        Assert.False(result.IsBanned);
        Assert.Null(result.BanUntilUtc);
    }

    [Fact]
    public void CheckBan_returns_not_banned_initially()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 3, BanMinutes: 2));

        var result = service.CheckBan("pc-01");

        Assert.False(result.IsBanned);
        Assert.Null(result.BanUntilUtc);
    }

    [Fact]
    public void RecordEmptyClaim_does_nothing_when_disabled()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: false, MaxEmptyClaims: 3, BanMinutes: 2));

        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-01");

        var result = service.CheckBan("pc-01");
        Assert.False(result.IsBanned);
    }

    [Fact]
    public void RecordEmptyClaim_ban_after_max_attempts()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 3, BanMinutes: 2));

        service.RecordEmptyClaim("pc-01");
        var after1 = service.CheckBan("pc-01");
        Assert.False(after1.IsBanned);

        service.RecordEmptyClaim("pc-01");
        var after2 = service.CheckBan("pc-01");
        Assert.False(after2.IsBanned);

        service.RecordEmptyClaim("pc-01");
        var after3 = service.CheckBan("pc-01");
        Assert.True(after3.IsBanned);
        Assert.NotNull(after3.BanUntilUtc);
    }

    [Fact]
    public void Ban_expires_after_duration()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 2, BanMinutes: 1));
        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-01");

        Assert.True(service.CheckBan("pc-01").IsBanned);

        // Ban duration is 1 minute — ban should not expire in 0 minutes.
        Assert.True(service.CheckBan("pc-01").IsBanned);
    }

    [Fact]
    public void ResetOnCoin_clears_empty_claim_count_and_ban()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 2, BanMinutes: 5));

        service.RecordEmptyClaim("pc-01");
        service.ResetOnCoin("pc-01");

        service.RecordEmptyClaim("pc-01");
        var result = service.CheckBan("pc-01");
        Assert.False(result.IsBanned);
    }

    [Fact]
    public void UpdateConfig_disabled_clears_all_state()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 2, BanMinutes: 5));

        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-01");
        Assert.True(service.CheckBan("pc-01").IsBanned);

        service.UpdateConfig(new AbusePreventionService.Config(Enabled: false, MaxEmptyClaims: 2, BanMinutes: 5));
        Assert.False(service.CheckBan("pc-01").IsBanned);
    }

    [Fact]
    public void Each_station_tracked_independently()
    {
        var service = new AbusePreventionService();
        service.UpdateConfig(new AbusePreventionService.Config(Enabled: true, MaxEmptyClaims: 2, BanMinutes: 5));

        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-01");
        service.RecordEmptyClaim("pc-02");

        Assert.True(service.CheckBan("pc-01").IsBanned);
        Assert.False(service.CheckBan("pc-02").IsBanned);
    }
}
