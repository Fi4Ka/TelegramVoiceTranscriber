using System.Diagnostics;
using TelegramVoiceTranscriber;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class QueueTests
{
    [Fact]
    public void DuplicateClicksDoNotConsumeCapacity()
    {
        var queue = new TranscriptionQueue(2);
        Assert.Equal(QueueStatus.Accepted, queue.TryReserve("one", out var one));
        using (one)
        {
            Assert.Equal(QueueStatus.AlreadyQueued, queue.TryReserve("one", out _));
            Assert.Equal(QueueStatus.Accepted, queue.TryReserve("two", out var two));
            using (two) Assert.Equal(QueueStatus.Full, queue.TryReserve("three", out _));
        }
        Assert.Equal(QueueStatus.Accepted, queue.TryReserve("one", out var retry));
        retry!.Dispose();
    }

    [Fact]
    public async Task WorkIsSerializedAndCancelledWaiterCanRetry()
    {
        var queue = new TranscriptionQueue(2);
        queue.TryReserve("one", out var one);
        queue.TryReserve("two", out var two);
        using var first = one!;
        using var second = two!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = first.RunAsync(async () => { entered.SetResult(); await release.Task; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = second.RunAsync(() => throw new Exception("Concurrent execution"), cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { release.TrySetResult(); await running; }
        bool executed = false;
        await second.RunAsync(() => { executed = true; return Task.CompletedTask; }, default);
        Assert.True(executed);
    }

    [Fact]
    public async Task FailureReleasesWorkerAndReservationDisposalIsIdempotent()
    {
        var queue = new TranscriptionQueue(1);
        queue.TryReserve("one", out var one);
        await Assert.ThrowsAsync<IOException>(() => one!.RunAsync(() => throw new IOException(), default));
        one!.Dispose();
        queue.TryReserve("one", out var next);
        using (next)
        {
            one.Dispose();
            Assert.Equal(QueueStatus.AlreadyQueued, queue.TryReserve("one", out _));
            await next!.RunAsync(() => Task.CompletedTask, default);
        }
    }
}

public sealed class ShutdownTests
{
    [Fact]
    public async Task ShutdownWaitsForCleanupAndRejectsNewCallbacks()
    {
        var tracker = new HandlerTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleanedUp = false;
        var running = tracker.RunAsync(async () =>
        {
            try { await release.Task; }
            finally { cleanedUp = true; }
        });
        var stopping = tracker.StopAsync();
        try
        {
            Assert.False(stopping.IsCompleted);
            await tracker.RunAsync(() => throw new Exception("New callback after shutdown"));
        }
        finally { release.TrySetResult(); await running; }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleanedUp);
    }

    [Fact]
    public async Task CallbackFailureDoesNotPreventShutdown()
    {
        var tracker = new HandlerTracker();
        await Assert.ThrowsAsync<IOException>(() => tracker.RunAsync(() => throw new IOException()));
        await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public sealed class PathAndTextTests
{
    [Fact]
    public void ExecutableIsFoundOnPathAndRelativePathsUseConfigRoot()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "whisper-cli" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.WriteAllText(file, "fixture");
        string? original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", directory);
            Assert.Equal(file, RuntimePaths.ResolveExecutable("whisper-cli", directory));
            Assert.Equal(file, RuntimePaths.ResolveExecutable("./" + Path.GetFileName(file), directory));
            Assert.Throws<FileNotFoundException>(() => RuntimePaths.ResolveExecutable("missing", directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("a😀b😀c", 2)]
    [InlineData("Русский текст 😀 с эмодзи", 7)]
    [InlineData("", 3500)]
    public void ChunksRoundTripWithoutBreakingEmoji(string text, int size)
    {
        var chunks = TextChunks.Split(text, size).ToArray();
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(chunk.Length, 1, size);
            Assert.False(char.IsLowSurrogate(chunk[0]));
            Assert.False(char.IsHighSurrogate(chunk[^1]));
        });
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Process-tree regression uses POSIX sh and sleep.";
    }
}

public sealed class ProcessTests
{
    [UnixFact]
    public async Task LargeOutputIsDrainedAndFailureDiagnosticsAreBounded()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunAsync("/bin/sh",
            ["-c", "head -c 100000 /dev/zero; head -c 100000 /dev/zero >&2; echo failure-marker >&2; exit 7"],
            Path.GetTempPath(), default, TimeSpan.FromSeconds(10)));
        Assert.Contains("код 7", error.Message);
        Assert.Contains("failure-marker", error.Message);
        Assert.True(error.Message.Length < 8500);
    }

    [UnixFact]
    public async Task TimeoutTerminatesChildProcess()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync("/bin/sh",
                ["-c", "echo $$ > \"$1\"; exec sleep 30", "sh", pidFile],
                Path.GetTempPath(), default, TimeSpan.FromSeconds(1)));
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally { File.Delete(pidFile); }
    }

    [UnixFact]
    public async Task UserCancellationIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("/bin/sh",
            ["-c", "exec sleep 30"], Path.GetTempPath(), cancellation.Token, TimeSpan.FromSeconds(10)));
    }
}
