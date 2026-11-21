using System.Globalization;
using RentalServer.Core.Rates;

namespace RentalServer.Core.Tests.Rates;

public sealed class RateServiceTests : IAsyncLifetime
{
    private string _databasePath = string.Empty;
    private RateRepository _repository = null!;
    private FakeRatesConfigPublisher _publisher = null!;
    private RateService _service = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"rental-test-rateservice-{Guid.NewGuid():N}.db");
        _repository = new RateRepository(_databasePath);
        await _repository.InitializeAsync();

        _publisher = new FakeRatesConfigPublisher();
        _service = new RateService(_repository, _publisher);
    }

    public Task DisposeAsync()
    {
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PublishCurrent_skips_when_no_version_exists()
    {
        await _service.PublishCurrentAsync();
        Assert.Equal(0, _publisher.PublishCount);
    }

    [Fact]
    public async Task PublishCurrent_emits_retained_sheet_after_seed()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        await _service.PublishCurrentAsync();

        Assert.Equal(1, _publisher.PublishCount);
        Assert.Equal(1, _publisher.LastMessage!.Version);
        // Wire format must use STRING-keyed denominations per JSON object semantics.
        Assert.True(_publisher.LastMessage.Rates["pc"].ContainsKey("5"));
        Assert.Equal(1800, _publisher.LastMessage.Rates["pc"]["5"]);
        Assert.Equal(7, _publisher.LastMessage.InsertCoinRules["pc"].StarterMaxPesos);
        Assert.Equal(10, _publisher.LastMessage.InsertCoinRules["pc"].NormalizedFromPesos);
    }

    [Fact]
    public async Task UpdateRates_persists_new_version_and_publishes()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var newSheet = BuildSheet(pc5: 500);
        var newVersion = await _service.UpdateRatesAsync(newSheet);

        Assert.Equal(2, newVersion);
        Assert.Equal(2, await _repository.GetCurrentVersionAsync());
        Assert.Equal(500, await _repository.GetSecondsAsync("pc", 5));

        Assert.Equal(1, _publisher.PublishCount);
        Assert.Equal(2, _publisher.LastMessage!.Version);
        Assert.Equal(500, _publisher.LastMessage.Rates["pc"]["5"]);
    }

    [Fact]
    public async Task UpdateRates_rejects_missing_device_type()
    {
        var sheet = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 60, [5] = 360, [10] = 750, [20] = 1800 },
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720, [10] = 1500, [20] = 3600 }
            // missing "tab"
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _service.UpdateRatesAsync(sheet);
        });

        // Nothing persisted or published.
        Assert.Null(await _repository.GetCurrentVersionAsync());
        Assert.Equal(0, _publisher.PublishCount);
    }

    [Fact]
    public async Task UpdateRates_rejects_missing_denomination()
    {
        var sheet = new Dictionary<string, IReadOnlyDictionary<int, int>>(StringComparer.Ordinal)
        {
            ["pc"]  = new Dictionary<int, int> { [1] = 60, [5] = 360, [10] = 750 },  // missing 20
            ["phn"] = new Dictionary<int, int> { [1] = 120, [5] = 720, [10] = 1500, [20] = 3600 },
            ["tab"] = new Dictionary<int, int> { [1] = 90, [5] = 540, [10] = 1125, [20] = 2700 }
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _service.UpdateRatesAsync(sheet);
        });

        Assert.Equal(0, _publisher.PublishCount);
    }

    [Fact]
    public async Task UpdateRates_rejects_non_positive_seconds()
    {
        var sheet = BuildSheet(pc5: 0);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await _service.UpdateRatesAsync(sheet);
        });

        Assert.Equal(0, _publisher.PublishCount);
    }

    [Fact]
    public async Task GetCurrent_returns_seeded_map()
    {
        await _repository.EnsureSeededAsync(FormatUtcNow());

        var current = await _service.GetCurrentAsync();

        Assert.NotNull(current);
        Assert.Equal(3, current!.Count);
        Assert.Equal(1800, current["pc"][5]);
        Assert.Equal(720, current["phn"][5]);
        Assert.Equal(540, current["tab"][5]);
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
