namespace TelegramVoiceTranscriber;

// Shutdown first closes admission, then waits for accepted callbacks to clean up.
public sealed class HandlerTracker
{
    private readonly HashSet<Task> active = new();
    private bool stopping;

    public Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (active)
        {
            if (stopping) return Task.CompletedTask;
            active.Add(completion.Task);
        }
        return ExecuteAsync(action, completion);
    }

    public Task StopAsync()
    {
        lock (active)
        {
            stopping = true;
            return Task.WhenAll(active);
        }
    }

    private async Task ExecuteAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await action(); }
        finally
        {
            completion.TrySetResult();
            lock (active) active.Remove(completion.Task);
        }
    }
}
