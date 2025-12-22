using System.Collections.Concurrent;

namespace WebhookApi.Services;

public interface ISemaphoreStore
{
    SemaphoreSlim GetSemaphore(string id);
    bool TryRemove(string id);
}

public class SemaphoreStore : ISemaphoreStore, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _map = new(StringComparer.OrdinalIgnoreCase);

    public SemaphoreSlim GetSemaphore(string id)
        => _map.GetOrAdd(id, _ => new SemaphoreSlim(1,1));

    public bool TryRemove(string id)
    {
        if (_map.TryRemove(id, out var sem))
        {
            try { sem.Dispose(); } catch { }
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var kv in _map)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _map.Clear();
    }
}
