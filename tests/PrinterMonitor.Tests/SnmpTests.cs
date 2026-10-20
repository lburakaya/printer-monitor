using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using PrinterMonitor.Simulation;
using Xunit;

namespace PrinterMonitor.Tests;

public sealed class SnmpTests
{
    [Theory]
    [InlineData(50, 200, 8, 25)]
    [InlineData(0, 100, 19, 0)]
    [InlineData(67, -2, 19, 67)]
    [InlineData(-1, 100, 19, null)]
    [InlineData(-2, 100, 19, null)]
    [InlineData(-3, 100, 19, null)]
    [InlineData(40, 0, 8, null)]
    [InlineData(120, 100, 19, null)]
    [InlineData(120, 100, 8, null)]
    public void Percentages_respect_units_special_values_and_invalid_capacities(int level, int capacity, int unit, int? expected)
        => Assert.Equal(expected, SnmpPrinterAdapter.Percentage(level, capacity, unit));

    [Fact]
    public void Network_policy_limits_targets_to_configured_cidrs()
    {
        var policy = new NetworkPolicy(new() { AllowedNetworks = ["192.0.2.0/24", "127.0.0.1/32"] });
        Assert.True(policy.Allows(IPAddress.Parse("192.0.2.45")));
        Assert.True(policy.Allows(IPAddress.Loopback));
        Assert.False(policy.Allows(IPAddress.Parse("192.0.3.45")));
        Assert.False(policy.Allows(IPAddress.Parse("8.8.8.8")));
        Assert.False(policy.Allows(IPAddress.IPv6Loopback));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2c")]
    public async Task Real_udp_reads_non_default_indexes_and_waste_toner(string version)
    {
        using var simulator = new LocalSnmpSimulator();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = simulator.RunAsync(cancellation.Token);
        try
        {
            var reading = await Read(simulator.Port, "simulator-read", version, cancellation.Token);
            Assert.Equal("online", reading.Status);
            Assert.Equal("SIM-0001", reading.Serial);
            Assert.Equal(5, reading.Supplies.Count);
            Assert.Equal(74, reading.Supplies.Single(x => x.Index == "7.10").Percent);
            Assert.True(reading.Supplies.Single(x => x.Index == "7.14").IsReceptacle);
            Assert.NotNull(reading.LastSuccessAt);
        }
        finally { cancellation.Cancel(); await IgnoreCancellation(running); }
    }

    [Theory]
    [InlineData("unknown", "online")]
    [InlineData("unsupported", "online")]
    [InlineData("timeout", "unreachable")]
    [InlineData("loop", "error")]
    public async Task Real_udp_handles_missing_data_timeouts_and_non_advancing_oids(string scenario, string expected)
    {
        using var simulator = new LocalSnmpSimulator(scenario: scenario);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = simulator.RunAsync(cancellation.Token);
        try
        {
            var reading = await Read(simulator.Port, "simulator-read", "v2c", cancellation.Token);
            Assert.Equal(expected, reading.Status);
            if (scenario == "unknown") { Assert.All(reading.Supplies, x => Assert.Null(x.Percent)); Assert.Contains(reading.Supplies, x => x.State == "someRemaining"); }
            if (scenario == "unsupported") Assert.Empty(reading.Supplies);
        }
        finally { cancellation.Cancel(); await IgnoreCancellation(running); }
    }

    [Fact]
    public async Task Wrong_community_does_not_claim_a_definite_authentication_failure()
    {
        using var simulator = new LocalSnmpSimulator();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = simulator.RunAsync(cancellation.Token);
        try
        {
            var reading = await Read(simulator.Port, "wrong-secret", "v2c", cancellation.Token);
            Assert.Equal("unreachable", reading.Status);
            Assert.Contains("kesin olarak belirlenemedi", reading.Message);
            Assert.Equal(2, simulator.Requests); // Initial attempt and exactly one retry.
        }
        finally { cancellation.Cancel(); await IgnoreCancellation(running); }
    }

    [Fact]
    public async Task Failure_preserves_last_success_and_old_revision_cannot_overwrite_edits()
    {
        var options = new MonitorOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "PrinterMonitorTests", Guid.NewGuid().ToString()) };
        var store = new PrinterStore(options, new EphemeralDataProtectionProvider());
        var row = store.Save(new() { Name = "Test", Host = "127.0.0.1" }, new("secret-never-export"));
        Assert.DoesNotContain("secret-never-export", row.ProtectedCredentials);
        Assert.Equal("secret-never-export", store.Secrets(row).Community);
        var good = new PrinterReading("online", "ok", "Printer", "123", [new("7.10", "Black", "black", 52, "measured", 52, 100, 19, false)], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        store.Record(row.Config, good);
        var coordinator = new PollCoordinator(store, new FailingAdapter(), options, NullLogger<PollCoordinator>.Instance);
        await coordinator.PollAsync(row.Config.Id, CancellationToken.None);
        var result = store.Find(row.Config.Id)!.Reading!;
        Assert.Equal("unreachable", result.Status);
        Assert.Equal(good.LastSuccessAt, result.LastSuccessAt);
        Assert.Equal(52, result.Supplies[0].Percent);
        store.Save(new() { Name = "Edited", Host = "127.0.0.2" }, new("new-secret"), row.Config.Id);
        Assert.False(store.Record(row.Config, good));
        Assert.Null(store.Find(row.Config.Id)!.Reading);
        store.Delete(row.Config.Id);
        Assert.Empty(store.History(row.Config.Id));
    }

    private static Task<PrinterReading> Read(int port, string community, string version, CancellationToken token)
    {
        var options = new MonitorOptions { TimeoutMs = 150, Retries = 1, AllowedNetworks = ["127.0.0.1/32"] };
        var adapter = new SnmpPrinterAdapter(options, new(options));
        return adapter.ReadAsync(new(Guid.NewGuid(), "Test", "127.0.0.1", port, "", version, "SHA256", "healthy", "1"), new(community), token);
    }
    private static async Task IgnoreCancellation(Task task) { try { await task; } catch (OperationCanceledException) { } }
    private sealed class FailingAdapter : IPrinterAdapter
    {
        public Task<PrinterReading> ReadAsync(PrinterConfig config, Credentials credentials, CancellationToken cancellationToken)
            => Task.FromResult(new PrinterReading("unreachable", "Timeout", null, null, [], DateTimeOffset.UtcNow, null));
    }
}
