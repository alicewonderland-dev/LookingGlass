using LookingGlass.Protocol;

namespace LookingGlass.Tests;

/// <summary>The test harness itself, where what it promises tests depends on more than it seems to.</summary>
public sealed class HarnessTests {
    /// <summary>
    /// More events than the server queues for one connection: sent all at once, they would fill the queue faster than it is
    /// sent (most of all with the whole suite running), and the server would drop the client as too slow, events and all.
    /// </summary>
    [Fact]
    public async Task SendAndSettleDeliversMoreEventsThanTheServerQueues() {
        const int queue = 8;
        await using var server = new Harness(settings: ("LookingGlass:Limits:SendQueueLength", queue.ToString()));
        var bob = await server.RegisterAsync("Bob Takes Many");

        const int count = queue * 25;
        var events = Enumerable.Range(0, count).Select(i => new Event { Announcement = new Announcement { Text = $"many {i}" } }).ToArray();
        await server.SendAndSettleAsync(bob, events);

        Assert.Equal(Enumerable.Range(0, count).Select(i => $"many {i}"), bob.Notices.Select(n => n.Text).Where(t => t.StartsWith("many ")));
    }
}
