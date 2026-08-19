using System.Reflection;
using NetSplit.Core;
using NetSplit.Tray;

namespace NetSplit.Tray.Tests;

public sealed class BandwidthChartTests
{
    [Fact]
    public void SumRatesUsesAggregateReceiveAndSendWithoutOverflow()
    {
        Assert.Equal(
            2_500_000_000L,
            BandwidthChart.SumRates(1_500_000_000L, 1_000_000_000L));
        Assert.Equal(
            long.MaxValue,
            BandwidthChart.SumRates(long.MaxValue, 1));
        Assert.Equal(10, BandwidthChart.SumRates(-5, 10));
    }

    [Theory]
    [InlineData(512L * 1024)]
    [InlineData(2_100_000_000L)]
    [InlineData(3_000_000_000L)]
    public void NiceCeilingAlwaysReturnsAValidPositiveCeiling(long value)
    {
        var result = BandwidthChart.NiceCeiling(value);

        Assert.True(result > 0);
        Assert.True(result >= value);
    }

    [Fact]
    public void PaintsLargeBidirectionalRatesWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            using var host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                ClientSize = new Size(520, 160)
            };
            using var chart = new BandwidthChart
            {
                Dock = DockStyle.Fill
            };
            chart.SetHistory(
            [
                new TrafficPoint
                {
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5),
                    DirectReceiveBps = 1_500_000_000L,
                    DirectSendBps = 1_000_000_000L
                },
                new TrafficPoint
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    DirectReceiveBps = 1_250_000_000L,
                    DirectSendBps = 1_100_000_000L
                }
            ]);
            host.Controls.Add(chart);
            _ = host.Handle;
            chart.CreateControl();
            host.PerformLayout();

            using var bitmap = new Bitmap(chart.Width, chart.Height);
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
        });
    }

    [Fact]
    public void DisposeUnsubscribesThemeChangeHandler()
    {
        var chart = new BandwidthChart();
        try
        {
            Assert.Contains(
                GetThemeHandlers(),
                handler => ReferenceEquals(handler.Target, chart));
        }
        finally
        {
            chart.Dispose();
        }

        Assert.DoesNotContain(
            GetThemeHandlers(),
            handler => ReferenceEquals(handler.Target, chart));
    }

    private static Delegate[] GetThemeHandlers()
    {
        var field = typeof(ThemeManager).GetField(
            "Changed",
            BindingFlags.Static | BindingFlags.NonPublic);
        var changed = field?.GetValue(null) as MulticastDelegate;
        return changed?.GetInvocationList() ?? [];
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            completed.Wait(TimeSpan.FromSeconds(15)),
            "WinForms chart test timed out.");
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }
}
