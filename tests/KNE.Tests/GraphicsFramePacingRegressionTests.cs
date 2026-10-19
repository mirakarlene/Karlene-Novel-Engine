using Xunit;

namespace KNE.Tests;

public sealed class GraphicsFramePacingRegressionTests
{
    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(144, 144)]
    [InlineData(30, 60)]
    public void FramePacer_JitteredCompositionKeepsRequestedRateRatherThanDroppingEveryOtherFrame(int cap, int refresh)
    {
        var pacer = new FramePacer();
        var accepted = new List<double>();
        for (int frame = 0; frame <= refresh * 10; frame++)
        {
            // Alternating delivery jitter is smaller than a refresh interval but defeats a gate
            // that restarts the entire period from every late composition event.
            double time = frame == 0 ? 0 : frame / (double)refresh + (frame % 2 == 0 ? -.0003 : .0003);
            if (pacer.ShouldRender(time, cap)) accepted.Add(time);
            Assert.False(pacer.ShouldRender(time, cap));
        }

        Assert.InRange(accepted.Count, cap * 10 - 2, cap * 10 + 1);
        Assert.Equal(0, accepted[0]);
        Assert.True(accepted[^1] >= 9.95, "The last frame must still track the current composition time.");
        Assert.All(accepted.Zip(accepted.Skip(1)), pair => Assert.True(pair.Second > pair.First));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void FramePacer_LongStallSkipsMissedDeadlinesWithoutBurstingFramesOnResume(int cap)
    {
        var pacer = new FramePacer(); double period = 1d / cap;
        Assert.True(pacer.ShouldRender(0, cap));
        Assert.True(pacer.ShouldRender(period, cap));
        double resume = 10 + period / 2;
        Assert.True(pacer.ShouldRender(resume, cap));
        for (int attempt = 0; attempt < 10; attempt++)
            Assert.False(pacer.ShouldRender(resume + attempt * period / 100, cap));
        Assert.True(pacer.ShouldRender(resume + period, cap));
        Assert.False(pacer.ShouldRender(resume + period, cap));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void FramePacer_HigherFrequencyCallbacksDoNotExceedCapOverTenSeconds(int cap)
    {
        var pacer = new FramePacer(); int rendered = 0;
        for (int tick = 0; tick <= 10000; tick++)
            if (pacer.ShouldRender(tick / 1000d, cap)) rendered++;
        Assert.InRange(rendered, cap * 10, cap * 10 + 1);
    }
}
