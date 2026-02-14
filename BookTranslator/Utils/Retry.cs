namespace BookTranslator.Utils;

public static class Retry
{
    public static async Task<T> WithBackoffAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 5,
        Func<int, TimeSpan>? delay = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        delay ??= attempt => TimeSpan.FromSeconds(Math.Min(20, 1.5 * attempt * attempt));
        shouldRetry ??= _ => true;

        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxAttempts && shouldRetry(ex))
            {
                last = ex;
                await Task.Delay(delay(attempt));
            }
        }

        throw last ?? new InvalidOperationException("Retry failed without exception.");
    }
}