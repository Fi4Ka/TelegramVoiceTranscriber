namespace TelegramVoiceTranscriber;

public enum QueueStatus { Accepted, AlreadyQueued, Full }

// Reserve before acknowledging a callback so repeated clicks never enter the queue.
public sealed class TranscriptionQueue(int capacity)
{
    private readonly int limit = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly HashSet<string> keys = new();
    private readonly SemaphoreSlim worker = new(1, 1);

    public QueueStatus TryReserve(string key, out Reservation? reservation)
    {
        lock (keys)
        {
            reservation = null;
            if (keys.Contains(key)) return QueueStatus.AlreadyQueued;
            if (keys.Count >= limit) return QueueStatus.Full;
            keys.Add(key);
            reservation = new Reservation(this, key);
            return QueueStatus.Accepted;
        }
    }

    public sealed class Reservation(TranscriptionQueue owner, string key) : IDisposable
    {
        private int disposed;
        public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await owner.worker.WaitAsync(cancellationToken);
            try { await action(); }
            finally { owner.worker.Release(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.keys) owner.keys.Remove(key);
        }
    }
}
