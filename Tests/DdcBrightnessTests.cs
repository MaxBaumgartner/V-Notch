using System.Collections.Concurrent;
using System.Diagnostics;
using VNotch.Services.Displays;
using Xunit;

namespace VNotch.Tests;

public sealed class DdcBrightnessTests
{
    [Fact]
    public async Task CachesReadsForFiveSeconds()
    {
        var time = new ManualTime();
        var port = new FakePort { Value = 42 };
        using var brightness = new DdcBrightness(port, time.Now);
        Assert.Equal(42, await brightness.GetAsync("a"));
        port.Value = 61;
        time.Advance(4999);
        Assert.Equal(42, await brightness.GetAsync("a"));
        Assert.Equal(1, port.Reads);
        time.Advance(1);
        Assert.Equal(61, await brightness.GetAsync("a"));
        Assert.Equal(2, port.Reads);
    }

    [Fact]
    public async Task CachesUnsupportedAndFailedReads()
    {
        var time = new ManualTime();
        var port = new FakePort { ThrowRead = true };
        using var brightness = new DdcBrightness(port, time.Now);
        Assert.Null(await brightness.GetAsync("a"));
        port.ThrowRead = false;
        port.Value = 50;
        Assert.Null(await brightness.GetAsync("a"));
        Assert.Equal(1, port.Reads);
        time.Advance(5000);
        Assert.Equal(50, await brightness.GetAsync("a"));
    }

    [Fact]
    public async Task ConcurrentReadsShareOneWorker()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var port = new FakePort { Value = 33, BeforeRead = () => { entered.Set(); release.Wait(); } };
        using var brightness = new DdcBrightness(port, () => DateTime.UtcNow);
        Task<int?> first = brightness.GetAsync("a");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var rest = Enumerable.Range(0, 30).Select(_ => brightness.GetAsync("a")).ToArray();
            Assert.All(rest, task => Assert.Same(first, task));
            Assert.Equal(1, port.Reads);
        }
        finally { release.Set(); }
        Assert.Equal(33, await first.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task CoalescesWritesTo120ms()
    {
        var time = new ManualTime();
        var port = new FakePort();
        using var brightness = new DdcBrightness(port, time.Now, time.Delay);
        brightness.Set("a", 10);
        await port.NextWrite();
        brightness.Set("a", 20);
        await time.WaitForDelay();
        brightness.Set("a", 30);
        brightness.Set("a", 40);
        time.Advance(119);
        Assert.Single(port.Writes);
        time.Advance(1);
        await port.NextWrite();
        Assert.Equal(new[] { ("a", 10), ("a", 40) }, port.Writes.ToArray());
    }

    [Fact]
    public async Task ThrottlesMonitorsIndependentlyAndClampsValues()
    {
        var time = new ManualTime();
        var port = new FakePort();
        using var brightness = new DdcBrightness(port, time.Now, time.Delay);
        brightness.Set("a", -5);
        await port.NextWrite();
        brightness.Set("a", 50);
        await time.WaitForDelay();
        brightness.Set("b", 150);
        await port.NextWrite();
        Assert.Equal(new[] { ("a", 0), ("b", 100) }, port.Writes.ToArray());
        time.Advance(120);
        await port.NextWrite();
        Assert.Equal(("a", 50), port.Writes.Last());
    }

    [Fact]
    public async Task WriteFailureInvalidatesCachedValueAndDoesNotStopWorker()
    {
        var time = new ManualTime();
        var port = new FakePort { Value = 10, WriteResult = false };
        using var brightness = new DdcBrightness(port, time.Now, time.Delay);
        Assert.Equal(10, await brightness.GetAsync("a"));
        brightness.Set("a", 40);
        await port.NextWrite();
        // Wait until the write result has been processed, using the next worker iteration.
        brightness.Set("a", 50);
        await time.WaitForDelay();
        port.Value = 20;
        Assert.Equal(20, await brightness.GetAsync("a"));
        port.WriteResult = true;
        time.Advance(120);
        await port.NextWrite();
        Assert.Equal(("a", 50), port.Writes.Last());
    }

    [Fact]
    public async Task InvalidationDoesNotPublishAnOldRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var port = new FakePort { Value = 20, BeforeRead = () => { entered.Set(); release.Wait(); } };
        using var brightness = new DdcBrightness(port, () => DateTime.UtcNow);
        var oldRead = brightness.GetAsync("a");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            brightness.Invalidate();
        }
        finally { release.Set(); }
        await oldRead.WaitAsync(TimeSpan.FromSeconds(3));
        port.BeforeRead = null;
        port.Value = 80;
        Assert.Equal(80, await brightness.GetAsync("a"));
        Assert.Equal(2, port.Reads);
    }

    [Fact]
    public async Task DisposeDropsPendingWrites()
    {
        var time = new ManualTime();
        var port = new FakePort();
        var brightness = new DdcBrightness(port, time.Now, time.Delay);
        brightness.Set("a", 10);
        await port.NextWrite();
        brightness.Set("a", 20);
        await time.WaitForDelay();
        brightness.Dispose();
        time.Advance(120);
        brightness.Set("b", 30);
        Assert.Null(await brightness.GetAsync("a"));
        Assert.Single(port.Writes);
    }

    [Fact]
    public async Task AsyncWritesReturnDeviceFailuresAndResolveSupersededValues()
    {
        var time = new ManualTime();
        var port = new FakePort { WriteResult = false };
        using var brightness = new DdcBrightness(port, time.Now, time.Delay);
        Assert.False(await brightness.SetAsync("a", 10).WaitAsync(TimeSpan.FromSeconds(3)));
        var superseded = brightness.SetAsync("a", 20);
        await time.WaitForDelay();
        var latest = brightness.SetAsync("a", 30);
        Assert.False(await superseded);
        port.WriteResult = true;
        time.Advance(120);
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { ("a", 10), ("a", 30) }, port.Writes.ToArray());
    }

    [Fact]
    public async Task InvalidationAndDisposeResolvePendingWriteResults()
    {
        var time = new ManualTime();
        var port = new FakePort();
        using var brightness = new DdcBrightness(port, time.Now, time.Delay);
        Assert.True(await brightness.SetAsync("a", 10).WaitAsync(TimeSpan.FromSeconds(3)));
        var pending = brightness.SetAsync("a", 20);
        await time.WaitForDelay();
        brightness.Invalidate();
        Assert.False(await pending);
        pending = brightness.SetAsync("a", 30);
        brightness.Dispose();
        Assert.False(await pending);
        time.Advance(120);
        Assert.Single(port.Writes);
    }

    [Fact]
    public async Task SlowDdcDoesNotBlockSectionOpen()
    {
        var port = new FakePort { Value = 65, BeforeRead = () => Thread.Sleep(2000) };
        using var brightness = new DdcBrightness(port, () => DateTime.UtcNow);
        var watch = Stopwatch.StartNew();
        Task<int?> pending = brightness.GetAsync("a");
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.False(pending.IsCompleted);
        Assert.Equal(65, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class FakePort : IDdcPort
    {
        private readonly SemaphoreSlim _writes = new(0);
        public readonly ConcurrentQueue<(string Id, int Value)> Writes = new();
        public int Reads;
        public int? Value;
        public bool ThrowRead;
        public bool WriteResult = true;
        public Action? BeforeRead;

        public int? Read(string id)
        {
            Interlocked.Increment(ref Reads);
            BeforeRead?.Invoke();
            if (ThrowRead) throw new InvalidOperationException("fake unavailable monitor");
            return Value;
        }

        public bool Write(string id, int value)
        {
            Writes.Enqueue((id, value));
            _writes.Release();
            return WriteResult;
        }

        public async Task NextWrite() => Assert.True(await _writes.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class ManualTime
    {
        private readonly object _gate = new();
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly List<(DateTime Due, TaskCompletionSource Done)> _delays = new();
        private readonly SemaphoreSlim _scheduled = new(0);

        public DateTime Now() { lock (_gate) return _now; }
        public Task Delay(TimeSpan interval)
        {
            lock (_gate)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _delays.Add((_now + interval, done));
                _scheduled.Release();
                return done.Task;
            }
        }

        public async Task WaitForDelay() => Assert.True(await _scheduled.WaitAsync(TimeSpan.FromSeconds(3)));

        public void Advance(int milliseconds)
        {
            lock (_gate)
            {
                _now += TimeSpan.FromMilliseconds(milliseconds);
                foreach (var delay in _delays.Where(d => d.Due <= _now).ToArray())
                {
                    _delays.Remove(delay);
                    delay.Done.TrySetResult();
                }
            }
        }
    }
}
