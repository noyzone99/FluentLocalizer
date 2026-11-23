namespace FluentLocalizer.Polyfill;

/// <summary>Provides cancellation-aware HTTP content access on older targets.</summary>
internal static class HttpContentExtensions
{
#if !NET8_0_OR_GREATER
    /// <summary>Opens a content stream while honoring cancellation where supported.</summary>
    public static async Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        Guard.IfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var operation = content.ReadAsStreamAsync();
        await WaitForContentAsync(operation, cancellationToken, static completed =>
        {
            if (completed is Task<Stream> { Status: TaskStatus.RanToCompletion } stream)
                stream.Result.Dispose();
        }).ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }
#endif

#if !NET10_0_OR_GREATER
    /// <summary>Buffers content within a size limit and stops waiting when cancelled.</summary>
    public static Task LoadIntoBufferAsync(this HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        Guard.IfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        return WaitForContentAsync(content.LoadIntoBufferAsync(maxBytes), cancellationToken);
    }

    private static async Task WaitForContentAsync(Task operation, CancellationToken cancellationToken, Action<Task>? onAbandoned = null)
    {
        if (!operation.IsCompleted && cancellationToken.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled);
            if (await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false) == cancelled.Task)
            {
                // The caller disposes the HTTP response. Observe any late fault and release any late stream.
                _ = operation.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    onAbandoned?.Invoke(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        await operation.ConfigureAwait(false);
    }
#endif
}
