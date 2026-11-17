using System.Net;
using System.Net.Http.Json;
using CaseAgents.Api;
using CaseAgents.Core.Abstractions;
using CaseAgents.Core.Security;
using CaseAgents.Tests.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace CaseAgents.Tests.Api;

public sealed class StartupTests
{
    [Fact]
    public void Startup_refuses_dev_auth_outside_development()
    {
        using var factory = new ApiFactory(aiBackend: false, environment: "Production", authMode: "Dev");

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Development", ex.Message);
    }

    [Fact]
    public async Task Dev_endpoints_absent_outside_Development()
    {
        using var factory = new ApiFactory(aiBackend: false, environment: "Production", authMode: "Entra");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/token", new { userId = "alice", roles = new[] { AppRoles.Submit } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/faults")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/faults", new { caseAppDown = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/clock", new { at = "2026-11-17T10:42:00Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/questionnaire")).StatusCode);
        Assert.Same(TimeProvider.System, factory.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Dev_endpoints_are_not_mapped_in_Entra_mode()
    {
        using var factory = new ApiFactory(aiBackend: false, environment: "Development", authMode: "Entra");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/dev/token", new { userId = "alice", roles = new[] { AppRoles.Submit } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/faults")).StatusCode);
    }

    [Fact]
    public void Dev_clock_is_the_time_provider_in_Development()
    {
        using var factory = new ApiFactory(aiBackend: false);

        Assert.IsType<DevClock>(factory.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public async Task Dev_clock_endpoint_sets_the_app_time()
    {
        using var factory = new ApiFactory(aiBackend: false);
        var at = new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero);

        var response = await factory.CreateClient().PostAsJsonAsync("/dev/clock", new { at });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadAsync<ClockBody>();
        Assert.InRange(body.Now, at, at.AddSeconds(5));
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        Assert.InRange(now, at, at.AddSeconds(5));
    }

    [Fact]
    public void Dev_clock_keeps_ticking_after_it_is_set()
    {
        var system = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new DevClock(system);
        var at = new DateTimeOffset(2026, 11, 17, 10, 42, 0, TimeSpan.Zero);

        Assert.Equal(system.GetUtcNow(), clock.GetUtcNow());
        Assert.Equal(at, clock.Set(at));
        system.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(at.AddSeconds(30), clock.GetUtcNow());
    }

    [Fact]
    public void Without_the_ai_backend_the_status_reports_nothing_pending()
    {
        using var factory = new ApiFactory(aiBackend: false);

        var status = factory.Services.GetRequiredService<IAiBackendStatus>();

        Assert.IsType<NoAiBackendStatus>(status);
        Assert.False(status.IsPending("any"));
        Assert.Equal(0, status.OutboxCount);
    }

    [Fact]
    public void Ai_backend_is_off_with_Entra_auth()
    {
        using var factory = new ApiFactory(aiBackend: true, environment: "Development", authMode: "Entra");

        Assert.IsType<NoAiBackendStatus>(factory.Services.GetRequiredService<IAiBackendStatus>());
    }

    private sealed record ClockBody(DateTimeOffset Now);
}
