namespace Edgewise.Infrastructure.MarketData;

/// <summary>
/// Simple sliding-window token bucket: at most <c>maxRequests</c> acquisitions per
/// <c>window</c>. Callers await a slot; timestamps of granted slots are kept in a
/// queue and expire as the window slides.
/// </summary>
public sealed class ProviderRateLimiter(int maxRequests, TimeSpan window)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _grants = new();

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTime.UtcNow;
                while (_grants.Count > 0 && now - _grants.Peek() >= window)
                {
                    _grants.Dequeue();
                }

                if (_grants.Count < maxRequests)
                {
                    _grants.Enqueue(now);
                    return;
                }

                delay = _grants.Peek() + window - now;
            }
            finally
            {
                _gate.Release();
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }
        }
    }
}
