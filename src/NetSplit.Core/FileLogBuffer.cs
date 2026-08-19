using System.Collections.Concurrent;
using System.Text;

namespace NetSplit.Core;

public sealed class FileLogBuffer : IDisposable
{
    private const int MaxMemoryEntries = 500;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FileLogBuffer(AppPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<string> Snapshot(int count = 200)
    {
        return _entries.Reverse().Take(Math.Clamp(count, 1, MaxMemoryEntries)).Reverse().ToArray();
    }

    public async Task WriteAsync(
        string level,
        string message,
        CancellationToken cancellationToken = default)
    {
        var sanitized = Sanitize(message);
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {sanitized}";
        _entries.Enqueue(entry);
        while (_entries.Count > MaxMemoryEntries)
        {
            _entries.TryDequeue(out _);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectories();
            RotateIfNeeded();
            await File.AppendAllTextAsync(
                _paths.ServiceLogFile,
                entry + Environment.NewLine,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_paths.ServiceLogFile);
        if (!file.Exists || file.Length < MaxFileBytes)
        {
            return;
        }

        var archive = Path.Combine(
            _paths.LogDirectory,
            $"net-split-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        File.Move(_paths.ServiceLogFile, archive, false);
    }

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        if (singleLine.Contains("://", StringComparison.Ordinal)
            || singleLine.Contains("password", StringComparison.OrdinalIgnoreCase)
            || singleLine.Contains("token", StringComparison.OrdinalIgnoreCase)
            || singleLine.Contains("secret", StringComparison.OrdinalIgnoreCase))
        {
            return "[已脱敏的敏感日志]";
        }

        return singleLine;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }
}
