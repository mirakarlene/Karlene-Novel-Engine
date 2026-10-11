using Xunit;

namespace KNE.Tests;

public sealed class LogBufferTests
{
    [Fact]
    public void Drain_ReturnsFiniteOrderedBatchesAndLeavesPendingMessages()
    {
        var buffer = new LogBuffer();
        for (int i = 0; i < 250; i++) buffer.Enqueue(i.ToString());
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i.ToString()), buffer.Drain());
        Assert.Equal(Enumerable.Range(100, 100).Select(i => i.ToString()), buffer.Drain());
        Assert.Equal(Enumerable.Range(200, 50).Select(i => i.ToString()), buffer.Drain());
        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void Flood_EvictsOldMessagesAndTruncatesLongMessage()
    {
        var buffer = new LogBuffer();
        for (int i = 0; i < 1200; i++) buffer.Enqueue(i.ToString());
        Assert.Equal("200", buffer.Drain(1000)[0]);
        buffer.Enqueue(new string('x', 5000));
        Assert.Equal(new string('x', 4096) + "…", Assert.Single(buffer.Drain()));
    }
}
