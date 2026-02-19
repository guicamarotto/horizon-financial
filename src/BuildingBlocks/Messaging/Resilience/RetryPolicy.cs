using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Messaging.Resilience;

public static class RetryPolicy
{
    public static async Task ExecuteAsync(
        Func<int, CancellationToken, Task> operation,
        ResilienceOptions options,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        var random = Random.Shared;

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            try
            {
                await operation(attempt, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;

                if (attempt == options.MaxAttempts)
                {
                    break;
                }

                var delayMs = (int)Math.Pow(2, attempt - 1) * options.BaseDelayMs;
                var jitter = random.Next(0, options.MaxJitterMs + 1);
                var delay = TimeSpan.FromMilliseconds(delayMs + jitter);

                logger.LogWarning(
                    exception,
                    "Operation {OperationName} failed on attempt {Attempt}/{MaxAttempts}; retrying in {Delay}",
                    operationName,
                    attempt,
                    options.MaxAttempts,
                    delay);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException($"Operation {operationName} failed without an exception.");
    }
}
