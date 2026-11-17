using CaseAgents.Core.Messaging;
using CaseAgents.Core.Operations;
using CaseAgents.Infra;
using CaseAgents.Infra.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaseAgents.Tests.Messaging;

public sealed class InMemoryEventBusTests
{
    private readonly ManualTimeProvider clock = new();
    private readonly FaultState faults = new();
    private readonly RecordingHandler handler = new();
    private readonly BusOptions options = new();
    private readonly InMemoryEventBus bus;

    public InMemoryEventBusTests() => bus = new InMemoryEventBus([handler], faults, clock, options);

    private static CaseEvent Event(string caseId, long sequence, string eventType = CaseEventTypes.AnswerConfirmed) =>
        new($"{caseId}:{sequence}", caseId, sequence, eventType, ManualTimeProvider.DefaultStart);

    [Fact]
    public async Task Publish_assigns_per_case_sequence_and_message_id()
    {
        var a1 = await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        var a2 = await bus.PublishAsync("c1", CaseEventTypes.AnswerConfirmed, "Q4", "No");
        var b1 = await bus.PublishAsync("c2", CaseEventTypes.CaseReady);

        Assert.Equal(("c1:1", 1L), (a1.MessageId, a1.Sequence));
        Assert.Equal(("c1:2", 2L), (a2.MessageId, a2.Sequence));
        Assert.Equal(("c2:1", 1L), (b1.MessageId, b1.Sequence));
        Assert.Equal(CaseEventTypes.AnswerConfirmed, a2.EventType);
        Assert.Equal("Q4", a2.QuestionId);
        Assert.Equal("No", a2.Value);
        Assert.Equal(clock.GetUtcNow(), a2.At);
        Assert.Equal(3, bus.GetStats().Published);
        Assert.Equal(2, bus.PendingFor("c1"));
        Assert.Equal(0, bus.PendingFor("unknown"));
    }

    [Fact]
    public async Task Delivers_in_order_per_case()
    {
        await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        await bus.PublishAsync("c2", CaseEventTypes.CaseReady);
        await bus.PublishAsync("c1", CaseEventTypes.AnswerConfirmed, "Q4", "No");
        await bus.PublishAsync("c1", CaseEventTypes.CaseTakenOver);

        var handled = await bus.DeliverPendingAsync();

        Assert.Equal(4, handled);
        Assert.Equal(new long[] { 1, 2, 3 }, handler.Seen.Where(e => e.CaseId == "c1").Select(e => e.Sequence));
        Assert.Single(handler.Seen, e => e.CaseId == "c2");
        var stats = bus.GetStats();
        Assert.Equal(4, stats.Delivered);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(0, bus.PendingFor("c1"));
    }

    [Fact]
    public async Task Duplicate_message_id_is_ignored_and_counted()
    {
        var first = await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        await bus.EnqueueAsync(first);               // duplicate while still queued
        Assert.Equal(1, await bus.DeliverPendingAsync());

        await bus.EnqueueAsync(first);               // duplicate after delivery
        Assert.Equal(0, await bus.DeliverPendingAsync());

        Assert.Single(handler.Seen);
        Assert.Equal(2, bus.GetStats().DuplicatesIgnored);
        Assert.Equal(0, bus.PendingFor("c1"));
    }

    [Fact]
    public async Task Out_of_order_event_is_held_until_the_gap_fills()
    {
        await bus.EnqueueAsync(Event("c1", 2));

        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Equal(0, await bus.DeliverPendingAsync());
        var held = bus.GetStats();
        Assert.Equal(1, held.Held);
        Assert.Equal(1, held.OutOfOrderDetected);

        await bus.EnqueueAsync(Event("c1", 1));
        Assert.Equal(2, await bus.DeliverPendingAsync());

        Assert.Equal(new long[] { 1, 2 }, handler.Seen.Select(e => e.Sequence));
        var stats = bus.GetStats();
        Assert.Equal(1, stats.OutOfOrderDetected);
        Assert.Equal(0, stats.Held);
        Assert.Equal(0, stats.GapsSkipped);
    }

    [Fact]
    public async Task Held_event_is_delivered_after_timeout_and_gap_counted()
    {
        await bus.EnqueueAsync(Event("c1", 2));
        Assert.Equal(0, await bus.DeliverPendingAsync());

        clock.Advance(options.OutOfOrderTimeout);
        Assert.Equal(0, await bus.DeliverPendingAsync());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await bus.DeliverPendingAsync());
        Assert.Equal(new long[] { 2 }, handler.Seen.Select(e => e.Sequence));
        Assert.Equal(1, bus.GetStats().GapsSkipped);

        // The skipped event arriving late is below the expected sequence: ignored.
        await bus.EnqueueAsync(Event("c1", 1));
        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Equal(1, bus.GetStats().DuplicatesIgnored);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task Failing_handler_is_retried_then_dead_lettered_and_case_moves_on()
    {
        handler.FailWhen = e => e.CaseId == "c1" && e.Sequence == 1;
        await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        await bus.PublishAsync("c1", CaseEventTypes.AnswerConfirmed, "Q4", "No");

        Assert.Equal(0, await bus.DeliverPendingAsync());
        clock.Advance(options.RetryDelay);
        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Equal(2, bus.PendingFor("c1"));
        clock.Advance(options.RetryDelay * 2);
        Assert.Equal(1, await bus.DeliverPendingAsync());   // third failure dead-letters, then seq 2 goes through

        Assert.Equal(new long[] { 1, 1, 1, 2 }, handler.Seen.Select(e => e.Sequence));
        var stats = bus.GetStats();
        Assert.Equal(3, stats.Retries);
        Assert.Equal(1, stats.DeadLetterCount);
        var dead = Assert.Single(stats.DeadLetters);
        Assert.Equal("c1:1", dead.Event.MessageId);
        Assert.Equal(RecordingHandler.FailureMessage, dead.Reason);
        Assert.Equal(3, dead.DeliveryCount);
        Assert.Equal(clock.GetUtcNow(), dead.At);
        Assert.Equal(0, bus.PendingFor("c1"));
    }

    [Fact]
    public async Task Failed_delivery_backs_off_exponentially_and_blocks_the_case_until_retry()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), options.RetryDelay);
        var start = clock.GetUtcNow();
        handler.FailWhen = e => e.Sequence == 1;
        await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        await bus.PublishAsync("c1", CaseEventTypes.AnswerConfirmed, "Q4", "No");

        Assert.Equal(0, await bus.DeliverPendingAsync());          // attempt 1 fails at t0
        Assert.Equal(0, await bus.DeliverPendingAsync());          // too early: not retried, seq 2 waits behind it
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Single(handler.Seen);

        clock.Advance(TimeSpan.FromMilliseconds(1));               // t0 + 1 s: attempt 2 fails
        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Equal(2, handler.Seen.Count);

        clock.Advance(TimeSpan.FromMilliseconds(1999));            // next retry not before t0 + 1 s + 2 s
        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal(2, bus.PendingFor("c1"));

        clock.Advance(TimeSpan.FromMilliseconds(1));               // t0 + 3 s: attempt 3 fails, dead-letter, seq 2 delivered
        Assert.Equal(1, await bus.DeliverPendingAsync());

        Assert.Equal(new long[] { 1, 1, 1, 2 }, handler.Seen.Select(e => e.Sequence));
        var dead = Assert.Single(bus.GetStats().DeadLetters);
        Assert.Equal(start.AddSeconds(3), dead.At);
        Assert.Equal(0, bus.PendingFor("c1"));
    }

    [Fact]
    public async Task Backoff_of_one_case_does_not_block_other_cases()
    {
        handler.FailWhen = e => e.CaseId == "c1";
        await bus.PublishAsync("c1", CaseEventTypes.CaseReady);
        await bus.PublishAsync("c2", CaseEventTypes.CaseReady);

        Assert.Equal(1, await bus.DeliverPendingAsync());
        await bus.PublishAsync("c2", CaseEventTypes.CaseTakenOver);
        Assert.Equal(1, await bus.DeliverPendingAsync());

        Assert.Equal(2, handler.Seen.Count(e => e.CaseId == "c2"));
        Assert.Single(handler.Seen, e => e.CaseId == "c1");
    }

    [Fact]
    public async Task Nothing_is_delivered_while_ai_backend_is_down_and_events_wait()
    {
        faults.AiBackendDown = true;
        await bus.PublishAsync("c1", CaseEventTypes.CaseReady);

        Assert.Equal(0, await bus.DeliverPendingAsync());
        Assert.Empty(handler.Seen);
        Assert.Equal(1, bus.PendingFor("c1"));

        faults.AiBackendDown = false;
        Assert.Equal(1, await bus.DeliverPendingAsync());
        Assert.Single(handler.Seen);
        Assert.Equal(0, bus.PendingFor("c1"));
    }

    [Fact]
    public async Task Nothing_is_delivered_without_handlers()
    {
        var empty = new InMemoryEventBus([], faults, clock, options);
        await empty.PublishAsync("c1", CaseEventTypes.CaseReady);

        Assert.Equal(0, await empty.DeliverPendingAsync());
        Assert.Equal(1, empty.PendingFor("c1"));
    }

    [Fact]
    public async Task PendingFor_counts_queued_and_held()
    {
        await bus.EnqueueAsync(Event("c1", 1));
        await bus.EnqueueAsync(Event("c1", 3));
        await bus.EnqueueAsync(Event("c2", 1));
        Assert.Equal(2, bus.PendingFor("c1"));

        Assert.Equal(2, await bus.DeliverPendingAsync());
        Assert.Equal(1, bus.PendingFor("c1"));           // seq 3 held
        await bus.EnqueueAsync(Event("c1", 4));
        Assert.Equal(2, bus.PendingFor("c1"));           // held + queued
        Assert.Equal(0, bus.PendingFor("c2"));

        var stats = bus.GetStats();
        Assert.Equal(2, stats.Pending);
        Assert.Equal(1, stats.Held);
    }

    [Fact]
    public async Task Publish_after_enqueue_continues_the_case_sequence()
    {
        await bus.EnqueueAsync(Event("c1", 5));

        var next = await bus.PublishAsync("c1", CaseEventTypes.CaseReleased);

        Assert.Equal(6, next.Sequence);
        Assert.Equal("c1:6", next.MessageId);
    }

    [Fact]
    public void Messaging_registration_registers_the_bus_and_the_pump()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bus:MaxDeliveryCount"] = "5" })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCaseAgentsMessaging(config);
        using var provider = services.BuildServiceProvider();

        var eventBus = provider.GetRequiredService<IEventBus>();
        Assert.IsType<InMemoryEventBus>(eventBus);
        Assert.Same(eventBus, provider.GetRequiredService<InMemoryEventBus>());
        Assert.Equal(5, provider.GetRequiredService<BusOptions>().MaxDeliveryCount);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is EventBusPump);
    }

    [Fact]
    public async Task Pump_delivers_published_events_in_the_background()
    {
        using var pump = new EventBusPump(bus, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<EventBusPump>.Instance);
        await pump.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync("c1", CaseEventTypes.CaseReady);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (handler.Seen.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            Assert.Single(handler.Seen);
        }
        finally
        {
            await pump.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingHandler : ICaseEventHandler
    {
        public const string FailureMessage = "Case API unavailable";

        private readonly object gate = new();
        private readonly List<CaseEvent> seen = [];

        public Func<CaseEvent, bool> FailWhen { get; set; } = _ => false;

        public IReadOnlyList<CaseEvent> Seen
        {
            get
            {
                lock (gate)
                    return seen.ToList();
            }
        }

        public Task HandleAsync(CaseEvent evt, CancellationToken ct)
        {
            lock (gate)
                seen.Add(evt);
            if (FailWhen(evt))
                throw new InvalidOperationException(FailureMessage);
            return Task.CompletedTask;
        }
    }
}
