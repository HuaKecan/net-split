namespace NetSplit.Core;

public static class AtomicFile
{
    private const int ReplaceAttempts = 6;

    public static async Task WriteAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var tempPath = CreateTempPath(path);
        try
        {
            await using (var stream = OpenTempStream(tempPath, 4096))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await ReplaceAsync(tempPath, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static async Task CopyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var tempPath = CreateTempPath(destination);
        try
        {
            await using (var sourceStream = new FileStream(
                             source,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Open,
                                 Access = FileAccess.Read,
                                 Share = FileShare.ReadWrite | FileShare.Delete,
                                 BufferSize = 81920,
                                 Options = FileOptions.Asynchronous
                                     | FileOptions.SequentialScan
                             }))
            await using (var destinationStream = OpenTempStream(tempPath, 81920))
            {
                await sourceStream.CopyToAsync(
                    destinationStream,
                    cancellationToken).ConfigureAwait(false);
                await destinationStream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                destinationStream.Flush(flushToDisk: true);
            }

            await ReplaceAsync(tempPath, destination, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static FileStream OpenTempStream(string path, int bufferSize)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = bufferSize,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
    }

    private static async Task ReplaceAsync(
        string tempPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < ReplaceAttempts - 1
                && exception is IOException or UnauthorizedAccessException)
            {
                var delay = TimeSpan.FromMilliseconds(20 * (attempt + 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string CreateTempPath(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"无法确定文件目录：{destinationPath}");
        Directory.CreateDirectory(directory);
        return Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A failed cleanup must not hide the original write error.
        }
    }
}
