using System.Globalization;
using RentalServer.Core.Rates;

namespace RentalServer.Core.Tests.Rates;

public sealed class RateRepositoryTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private RateRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-rates-{Guid.NewGuid():N}.db");
        _repository = new RateRepository(_databasePath);
        await _repository.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetCurrentVersion_returns_null_when_no_version_seeded()
    {
        Assert.Null(await _repository.GetCurrentVersionAsync());
        Assert.Null(await _repository.GetCurrentRatesAsync());
        Assert.Null(await _repository.GetCurrentPublishedAtAsync());
    }

    [Fact]
    public async Task EnsureSeeded_produces_version_1_current_with_full_sheet()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        Assert.Equal(1, await _repository.GetCurrentVersionAsync());

        var rates = await _repository.GetCurrentRatesAsync();
        Assert.NotNull(rates);
        // 3 device types × 4 denominations = 12 entries total.
        Assert.Equal(3, rates!.Count);
        foreach (var device in new[] { "pc", "phn", "tab" })
        {
            Assert.Equal(4, rates[device].Count);
            foreach (var denom in new[] { 1, 5, 10, 20 })
            {
                Assert.True(rates[device][denom] > 0);
            }
        }

        var rules = await _repository.GetCurrentInsertCoinRulesAsync();
        Assert.NotNull(rules);
        Assert.Equal(7, rules!["pc"].StarterMaxPesos);
        Assert.Equal(10, rules["pc"].NormalizedFromPesos);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1800)]
    [InlineData(8, 2160)]
    [InlineData(9, 2520)]
    [InlineData(10, 3600)]
    [InlineData(11, 3960)]
    [InlineData(20, 7200)]
    [InlineData(25, 9000)]
    [InlineData(30, 10800)]
    public async Task GetInsertCoinSecondsForTotal_uses_locked_total_peso_rule(
        int totalPesos,
        int expectedSeconds)
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        Assert.Equal(
            expectedSeconds,
            await _repository.GetInsertCoinSecondsForTotalAsync("pc", totalPesos));
    }

    [Theory]
    [InlineData("pc", 7, 2520)]
    [InlineData("phn", 7, 960)]
    [InlineData("tab", 7, 720)]
    [InlineData("pc", 20, 7200)]
    public async Task GetAddTimeSecondsForTotal_composes_arbitrary_peso_amounts_from_current_sheet(
        string deviceType,
        int totalPesos,
        int expectedSeconds)
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        Assert.Equal(
            expectedSeconds,
            await _repository.GetAddTimeSecondsForTotalAsync(deviceType, totalPesos));
    }

    [Fact]
    public async Task EnsureSeeded_is_idempotent()
    {
        var t1 = FormatUtcNow();
        await _repository.EnsureSeededAsync(t1);
        var firstVersion = await _repository.GetCurrentVersionAsync();

        await Task.Delay(20); // ensure timestamp would differ if a second seed wrote
        await _repository.EnsureSeededAsync(FormatUtcNow());

        // Same version, unchanged.
        Assert.Equal(firstVersion, await _repository.GetCurrentVersionAsync());
    }

    [Fact]
    public async Task PublishNewVersion_increments_and_flips_current()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var v2 = await _repository.PublishNewVersionAsync(
            BuildSheet(pc5: 400),
            FormatUtcNow());

        Assert.Equal(2, v2);
        Assert.Equal(2, await _repository.GetCurrentVersionAsync());

        // Old version's rates are NOT returned anymore.
        var current = await _repository.GetCurrentRatesAsync();
        Assert.NotNull(current);
        Assert.Equal(400, current!["pc"][5]);

        // GetSecondsAsync (pulse pipeline) sees the new value.
        Assert.Equal(400, await _repository.GetSecondsAsync("pc", 5));
    }

    [Fact]
    public async Task PublishNewVersion_supports_multiple_successive_versions()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var v2 = await _repository.PublishNewVersionAsync(BuildSheet(pc5: 400), FormatUtcNow());
        var v3 = await _repository.PublishNewVersionAsync(BuildSheet(pc5: 500), FormatUtcNow());
        var v4 = await _repository.PublishNewVersionAsync(BuildSheet(pc5: 600), FormatUtcNow());

        Assert.Equal(2, v2);
        Assert.Equal(3, v3);
        Assert.Equal(4, v4);
        Assert.Equal(4, await _repository.GetCurrentVersionAsync());
        Assert.Equal(600, await _repository.GetSecondsAsync("pc", 5));
    }

    [Fact]
    public async Task GetCurrentPublishedAt_returns_timestamp_of_current_version()
    {
        var t1 = FormatUtcNow();
        await _repository.EnsureSeededAsync(t1);
        Assert.Equal(t1, await _repository.GetCurrentPublishedAtAsync());

        var t2 = FormatUtcNow();
        await _repository.PublishNewVersionAsync(BuildSheet(), t2);
        Assert.Equal(t2, await _repository.GetCurrentPublishedAtAsync());
    }

    [Fact]
    public async Task PublishNewVersion_rejects_invalid_seconds_via_check_constraint()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        // Negative seconds violate the CHECK (seconds > 0) constraint on the rates table.
        await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await _repository.PublishNewVersionAsync(BuildSheet(pc5: -1), FormatUtcNow());
        });
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>> BuildSheet(int pc5 = 360)
    {
        return new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 60,  [5] = pc5, [10] = 750,  [20] = 1800 },
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720, [10] = 1500, [20] = 3600 },
            ["tab"] = new Dictionary<int, int> { [1] = 90,  [5] = 540, [10] = 1125, [20] = 2700 }
        };
    }

    private static string FormatUtcNow()
    {
        return DateTimeOffset.UtcNow.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
