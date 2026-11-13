using Ferry.Core.Download;

namespace Ferry.Core.Tests;

public sealed class SegmentPlanTests
{
    private const long MiB = 1 << 20;

    [Fact]
    public void FilesAreSplitIntoEqualPartsButNotTooSmall()
    {
        var plan = SegmentPlan.Split(10 * MiB + 3, connections: 4, MiB);
        Assert.Equal([(0, 2621440), (2621440, 5242880), (5242880, 7864320), (7864320, 10 * MiB + 3)],
                     plan.Snapshot().Select(s => (s.Start, s.End)));
        Assert.Single(SegmentPlan.Split(MiB + MiB / 2, connections: 4, MiB).Snapshot());
        Assert.Single(SegmentPlan.Split(10, connections: 4, MiB).Snapshot());
    }

    [Fact]
    public void AnIdleConnectionTakesTheSecondHalfOfTheLargestPart()
    {
        var plan = SegmentPlan.Split(8 * MiB, connections: 1, MiB);
        var first = plan.Take()!;
        Assert.Equal(MiB, plan.Claim(first, (int)MiB));
        plan.Written(first, (int)MiB);

        var second = plan.Take()!;

        Assert.Equal((4.5 * MiB, 8 * MiB), (second.Start, second.End));
        Assert.Equal(4.5 * MiB, first.End);
        // the first connection stops where the second begins
        Assert.Equal(3.5 * MiB, plan.Claim(first, (int)(5 * MiB)));
        Assert.Equal(0, plan.Active); // taken, but neither has its bytes coming yet
        plan.Receiving(first, true);
        plan.Receiving(second, true);
        Assert.Equal(2, plan.Active);
        plan.Release(first);
        Assert.Equal(1, plan.Active);
    }

    [Fact]
    public void PartsTooSmallToShareAreLeftToTheirConnection()
    {
        var plan = SegmentPlan.Split(2 * MiB, connections: 1, MiB);
        var only = plan.Take()!;
        plan.Claim(only, 1);

        Assert.Null(plan.Take());
    }

    [Fact]
    public void AReleasedPartIsTakenAgainFromWhereItsBytesStop()
    {
        var plan = SegmentPlan.Split(4 * MiB, connections: 2, MiB);
        var first = plan.Take()!;
        plan.Take(); // the second part
        plan.Claim(first, 1000);
        plan.Written(first, 600); // the rest of the claim never reached the disk
        plan.Release(first);

        var again = plan.Take()!;

        Assert.Same(first, again);
        Assert.Equal((600L, 2 * MiB), plan.Next(again));
        Assert.Equal(600, plan.Done);
    }

    [Fact]
    public void ANewTryFetchesAgainWhatWasClaimedButNeverWritten()
    {
        var plan = SegmentPlan.Split(2 * MiB, connections: 1, MiB);
        var only = plan.Take()!;
        plan.Claim(only, 1000);
        plan.Written(only, 600); // the try was cut short in the middle of a write

        plan.Rewind(only);

        Assert.Equal((600L, 2 * MiB), plan.Next(only));
        Assert.Equal(600, plan.Done);
    }

    [Fact]
    public async Task TheStartOfTheFileIsKnownToBeThereAsFarAsItReachesWithoutAGap()
    {
        var plan = SegmentPlan.Split(4 * MiB, connections: 2, MiB);
        var (first, second) = (plan.Take()!, plan.Take()!);
        var grew = plan.ContiguousGrewAsync(0);
        plan.Claim(second, 100);
        plan.Written(second, 100); // bytes past a gap don't count yet
        Assert.Equal(0, plan.ContiguousDone);
        Assert.False(grew.IsCompleted);

        plan.Claim(first, (int)(2 * MiB));
        plan.Written(first, (int)(2 * MiB));

        Assert.Equal(2 * MiB + 100, plan.ContiguousDone); // the first part done, the gap closed: the second counts
        await grew.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(plan.ContiguousGrewAsync(2 * MiB).IsCompletedSuccessfully);
        Assert.False(plan.ContiguousGrewAsync(2 * MiB + 100).IsCompleted);
    }

    [Fact]
    public void AResumedPlanKnowsHowFarItsStartReaches()
    {
        var plan = SegmentPlan.Resume(4 * MiB, [new(0, 2 * MiB, 2 * MiB), new(2 * MiB, 4 * MiB, 10)], MiB);
        Assert.Equal(2 * MiB + 10, plan.ContiguousDone);
    }

    [Fact]
    public void SavedPartsResumeAndMustCoverTheFile()
    {
        var plan = SegmentPlan.Resume(4 * MiB, [new(2 * MiB, 4 * MiB, 10), new(0, 2 * MiB, 2 * MiB)], MiB);
        Assert.Equal(2 * MiB + 10, plan.Done);
        Assert.Equal((2 * MiB + 10, 4 * MiB), plan.Next(plan.Take()!));

        Assert.Throws<InvalidDataException>(() => SegmentPlan.Resume(4 * MiB, [new(0, MiB, 0), new(2 * MiB, 4 * MiB, 0)], MiB));
        Assert.Throws<InvalidDataException>(() => SegmentPlan.Resume(4 * MiB, [new(0, 3 * MiB, 0)], MiB));
        Assert.Throws<InvalidDataException>(() => SegmentPlan.Resume(4 * MiB, [new(0, 4 * MiB, 5 * MiB)], MiB));
    }
}
