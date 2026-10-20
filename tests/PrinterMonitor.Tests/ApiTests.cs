using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PrinterMonitor.Tests;

public sealed class ApiTests
{
    private sealed class Factory(bool demo) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Monitor:DemoMode", demo.ToString())
            .UseSetting("Monitor:AdminPassword", "test-only-password-12345")
            .UseSetting("Monitor:AllowedNetworks:0", "127.0.0.1/32")
            .UseSetting("Monitor:DataDirectory", Path.Combine(Path.GetTempPath(), "PrinterMonitorApiTests", Guid.NewGuid().ToString()))
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitor:DemoMode"] = demo.ToString(), ["Monitor:AdminPassword"] = "test-only-password-12345",
                ["Monitor:AllowedNetworks:0"] = "127.0.0.1/32",
                ["Monitor:DataDirectory"] = Path.Combine(Path.GetTempPath(), "PrinterMonitorApiTests", Guid.NewGuid().ToString())
            }));
    }
    private static async Task<JsonElement> Session(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return session;
    }

    [Fact]
    public async Task Demo_crud_poll_history_export_and_csrf_work_end_to_end()
    {
        await using var factory = new Factory(true);
        using var client = factory.CreateClient();
        var forbidden = await client.PostAsJsonAsync("/api/printers", new { name = "Unsafe", host = "127.0.0.1" });
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
        Assert.True((await Session(client)).GetProperty("demoMode").GetBoolean());
        var response = await client.PostAsJsonAsync("/api/printers", new { name = "API Test", host = "127.0.0.1", community = "do-not-export-987", demoScenario = "low" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = item.GetProperty("id").GetGuid();
        Assert.DoesNotContain("do-not-export-987", await response.Content.ReadAsStringAsync());
        var test = await client.PostAsync($"/api/printers/{id}/test", null); test.EnsureSuccessStatusCode();
        var reading = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("online", reading.GetProperty("status").GetString());
        Assert.Equal(12, reading.GetProperty("supplies")[0].GetProperty("percent").GetInt32());
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/printers/{id}/history"); Assert.True(history.GetArrayLength() > 0);
        Assert.DoesNotContain("do-not-export-987", await client.GetStringAsync("/api/export"));
        var edit = await client.PutAsJsonAsync($"/api/printers/{id}", new { name = "Updated", host = "127.0.0.1", demoScenario = "unknown" }); edit.EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reading").ValueKind);
        (await client.DeleteAsync($"/api/printers/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/printers/{id}/test", null)).StatusCode);
    }

    [Fact]
    public async Task Live_mode_requires_login_and_rejects_out_of_scope_networks()
    {
        await using var factory = new Factory(false);
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/printers")).StatusCode);
        Assert.False((await Session(client)).GetProperty("demoMode").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { password = "wrong" })).StatusCode);
        (await client.PostAsJsonAsync("/api/login", new { password = "test-only-password-12345" })).EnsureSuccessStatusCode();
        Assert.True((await Session(client)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/printers")).StatusCode);
        var denied = await client.PostAsJsonAsync("/api/printers", new { name = "Outside", host = "192.0.2.1", community = "example-read" });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("izin verilen", await denied.Content.ReadAsStringAsync());
        var invalid = await client.PostAsJsonAsync("/api/printers", new { name = "Invalid", host = "127.0.0.1", port = -1, community = "example-read" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        (await client.PostAsync("/api/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/export")).StatusCode);
    }
}
