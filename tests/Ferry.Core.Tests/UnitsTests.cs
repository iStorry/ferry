using Ferry.Core.Text;

namespace Ferry.Core.Tests;

public sealed class UnitsTests
{
    [Fact]
    public void TimeLeftAtANormalSpeed()
    {
        Assert.Equal(TimeSpan.FromSeconds(50), Units.TimeLeft(100 << 20, 2 << 20));
        Assert.Equal(TimeSpan.Zero, Units.TimeLeft(0, 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1e-300)] // a speed fading out after a pause: TimeSpan.FromSeconds would overflow
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    public void NoTimeLeftWithoutARealSpeed(double bytesPerSecond) =>
        Assert.Null(Units.TimeLeft(1L << 40, bytesPerSecond));

    [Fact]
    public void NoTimeLeftWhenItWouldBeMonths() => Assert.Null(Units.TimeLeft(1L << 40, 1024));
}
