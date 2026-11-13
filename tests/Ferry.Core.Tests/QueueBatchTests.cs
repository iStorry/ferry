using Ferry.Core.Queue;
using Ferry.Core.Sites;

namespace Ferry.Core.Tests;

public sealed class QueueBatchTests
{
    private static QueueItem Item(QueueState state) =>
        new(Guid.NewGuid(), new FileLink(DirectSite.SiteId, new Uri("https://example.test/a.zip"), "https://example.test/a.zip"), "C:\\Downloads\\Ferry", null, state, null, 0, null, 0, 0, 0, 0, null, null);

    private static QueueItemStateChanged To(QueueItem item, QueueState state) => new(item with { State = state }, item.State);

    [Theory]
    [InlineData(QueueState.Done)]
    [InlineData(QueueState.Failed)]
    [InlineData(QueueState.Skipped)]
    public void AFileThatEndsFinishesTheBatch(QueueState end)
    {
        var batch = new QueueBatch();
        var file = Item(QueueState.Downloading);
        Assert.Null(batch.Note(new QueueItemAdded(file)));
        Assert.Equal(end, Assert.Single(batch.Note(To(file, end))!).State);
    }

    [Theory]
    [InlineData(QueueState.Paused)]
    [InlineData(QueueState.Cancelled)]
    public void StoppingTheOnlyFileFinishesNothing(QueueState stop)
    {
        var batch = new QueueBatch();
        var file = Item(QueueState.Downloading);
        batch.Note(new QueueItemAdded(file));
        Assert.Null(batch.Note(To(file, stop)));
    }

    [Fact]
    public void RemovingTheOnlyFileFinishesNothing()
    {
        var batch = new QueueBatch();
        var file = Item(QueueState.Downloading);
        batch.Note(new QueueItemAdded(file));
        Assert.Null(batch.Note(new QueueItemRemoved(file)));
    }

    [Fact]
    public void AFileRemovedAfterItEndedIsNotAmongThoseFinished()
    {
        var batch = new QueueBatch();
        var (gone, kept) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(gone));
        batch.Note(new QueueItemAdded(kept));
        batch.Note(To(gone, QueueState.Done));
        batch.Note(new QueueItemRemoved(gone with { State = QueueState.Done }));
        Assert.Equal([kept.Id], batch.Note(To(kept, QueueState.Done))!.Select(item => item.Id));
    }

    [Fact]
    public void PausingWhatIsLeftAfterOneEndedFinishesNothing()
    {
        var batch = new QueueBatch();
        var (first, second) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(first));
        batch.Note(new QueueItemAdded(second));
        Assert.Null(batch.Note(To(first, QueueState.Done)));
        Assert.Null(batch.Note(To(second, QueueState.Paused)));
    }

    [Fact]
    public void WhatHadEndedCountsOnceAPausedFileIsResumedAndEnds()
    {
        var batch = new QueueBatch();
        var (first, second) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(first));
        batch.Note(new QueueItemAdded(second));
        batch.Note(To(first, QueueState.Done));
        batch.Note(To(second, QueueState.Paused));
        batch.Note(To(second with { State = QueueState.Paused }, QueueState.Downloading));
        var finished = batch.Note(To(second, QueueState.Done));
        Assert.Equal([first.Id, second.Id], finished!.Select(item => item.Id));
    }

    [Fact]
    public void LateNewsOfAFileThatEndedDoesNotKeepTheBatchOpen()
    {
        var batch = new QueueBatch();
        var (file, other) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(file));
        batch.Note(new QueueItemAdded(other));
        batch.Note(To(file with { Revision = 5 }, QueueState.Done));
        Assert.Null(batch.Note(new QueueItemUpdated(file with { Revision = 4 })));
        Assert.Equal(2, batch.Note(To(other, QueueState.Done))!.Count);
    }

    [Fact]
    public void LateNewsOfAFileThatWasRemovedDoesNotKeepTheBatchOpen()
    {
        var batch = new QueueBatch();
        var (gone, other) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(gone));
        batch.Note(new QueueItemAdded(other));
        batch.Note(new QueueItemRemoved(gone));
        Assert.Null(batch.Note(new QueueItemUpdated(gone)));
        Assert.NotNull(batch.Note(To(other, QueueState.Done)));
    }

    [Fact]
    public void AFilePausedEarlierDoesNotHoldTheBatchBack()
    {
        var batch = new QueueBatch();
        var (first, second) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(first));
        batch.Note(new QueueItemAdded(second));
        Assert.Null(batch.Note(To(first, QueueState.Paused)));
        Assert.NotNull(batch.Note(To(second, QueueState.Done)));
    }

    [Fact]
    public void ANewQueueStartsOverWithoutFinishing()
    {
        var batch = new QueueBatch();
        var (old, running) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(old));
        batch.Note(To(old, QueueState.Done));
        batch.Reset([Item(QueueState.Done), running]);
        Assert.Equal([running.Id], batch.Note(To(running, QueueState.Done))!.Select(item => item.Id));
    }

    [Fact]
    public void TheCountsAreOnlyThisBatchs()
    {
        var batch = new QueueBatch();
        var (first, second) = (Item(QueueState.Downloading), Item(QueueState.Downloading));
        batch.Note(new QueueItemAdded(first));
        batch.Note(To(first, QueueState.Done));
        batch.Note(new QueueItemAdded(second));
        Assert.Equal([second.Id], batch.Note(To(second, QueueState.Failed))!.Select(item => item.Id));
    }
}
