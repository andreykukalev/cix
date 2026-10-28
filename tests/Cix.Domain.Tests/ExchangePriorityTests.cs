using Cix.Domain.Exchange;

namespace Cix.Domain.Tests;

public sealed class ExchangePriorityTests
{
    [Fact]
    public async Task AnUrgentScope_FlowsIntoWhatItAwaits_AndEndsWithIt()
    {
        Assert.False(ExchangePriority.IsUrgent);
        bool inside;
        using (ExchangePriority.BeginUrgent())
            inside = await Task.Run(() => ExchangePriority.IsUrgent);

        Assert.True(inside);
        Assert.False(ExchangePriority.IsUrgent);

        await UrgentlyAsync();
        Assert.False(ExchangePriority.IsUrgent); // an async method's scope never leaks to its caller
    }

    private static async Task UrgentlyAsync()
    {
        using var scope = ExchangePriority.BeginUrgent();
        await Task.Yield();
        Assert.True(ExchangePriority.IsUrgent);
    }
}
