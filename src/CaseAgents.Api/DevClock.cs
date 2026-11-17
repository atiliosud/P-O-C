using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseAgents.Api;

/// <summary>
/// Development clock: the system clock plus an offset set through POST /dev/clock, so demos can show fixed times
/// (e.g. "17 Nov 10:42") while time keeps ticking.
/// </summary>
public sealed class DevClock(TimeProvider system) : TimeProvider
{
    private long offsetTicks;

    public override DateTimeOffset GetUtcNow() => system.GetUtcNow().AddTicks(Interlocked.Read(ref offsetTicks));

    /// <summary>From now on the clock reads <paramref name="at"/> and keeps ticking. Returns the new time.</summary>
    public DateTimeOffset Set(DateTimeOffset at)
    {
        Interlocked.Exchange(ref offsetTicks, (at - system.GetUtcNow()).Ticks);
        return GetUtcNow();
    }

    public override TimeZoneInfo LocalTimeZone => system.LocalTimeZone;
}

public static class DevClockRegistration
{
    /// <summary>Replaces the registered TimeProvider with a <see cref="DevClock"/>. Development only.</summary>
    public static IServiceCollection AddDevClock(this IServiceCollection services)
    {
        // Built over the real system clock (resolving TimeProvider here would return the DevClock itself).
        services.AddSingleton(_ => new DevClock(TimeProvider.System));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(sp => sp.GetRequiredService<DevClock>()));
        return services;
    }
}
