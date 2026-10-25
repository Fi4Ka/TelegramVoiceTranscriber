using System.Diagnostics;
using System.Text;

namespace TelegramVoiceTranscriber;

public static class ProcessRunner
{
    public static async Task RunAsync(string executable, IReadOnlyList<string> args,
        string workingDirectory, CancellationToken cancellationToken, TimeSpan? timeLimit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException($"Не удалось запустить {executable}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeLimit ?? TimeSpan.FromMinutes(10));
        var output = CaptureTail(process.StandardOutput, timeout.Token);
        var error = CaptureTail(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* process already exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} превысил время выполнения.");
        }

        if (process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(executable)} вернул код {process.ExitCode}:\n{await error}\n{await output}");
    }

    // Drain both pipes concurrently but retain only a bounded diagnostic tail.
    private static async Task<string> CaptureTail(StreamReader reader, CancellationToken ct)
    {
        const int limit = 4096;
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            result.Append(buffer, 0, count);
            if (result.Length > limit) result.Remove(0, result.Length - limit);
        }
        return result.ToString();
    }
}
