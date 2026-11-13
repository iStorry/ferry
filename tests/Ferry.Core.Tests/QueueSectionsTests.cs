using Ferry.Core.Sites;
using Ferry.Core.Queue;

namespace Ferry.Core.Tests;

public sealed class QueueSectionsTests
{
    private static QueueItem Item(QueueState state) =>
        new(Guid.NewGuid(), new FileLink(DirectSite.SiteId, new Uri("https://example.test/a.zip"), "https://example.test/a.zip"), "C:\\Downloads\\Ferry", null, state, null, 0, null, 0, 0, 0, 0, null, null);

    [Theory]
    [InlineData(QueueState.Failed, QueueSection.NeedsYou)]
    [InlineData(QueueState.Resolving, QueueSection.Now)]
    [InlineData(QueueState.Downloading, QueueSection.Now)]
    [InlineData(QueueState.Waiting, QueueSection.UpNext)]
    [InlineData(QueueState.Paused, QueueSection.Stopped)]
    [InlineData(QueueState.Cancelled, QueueSection.Stopped)]
    [InlineData(QueueState.Done, QueueSection.Done)]
    [InlineData(QueueState.Skipped, QueueSection.Done)]
    public void EachStateHasItsSection(QueueState state, QueueSection section) =>
        Assert.Equal(section, QueueSections.Of(Item(state)));

    [Fact]
    public void SpeedHistoryKeepsTheLatestAndOnlyRealSpeeds()
    {
        var history = new SpeedHistory(3);
        foreach (var speed in new[] { 1.0, 2, double.NaN, -5, 4 })
            history.Add(speed);
        Assert.Equal([0.0, 0, 4], history.Samples);
        Assert.Equal(4, history.Peak);
        history.Clear();
        Assert.Equal(0, history.Peak);
    }
}
