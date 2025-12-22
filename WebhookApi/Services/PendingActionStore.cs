using System.Collections.Concurrent;

namespace WebhookApi.Services;

public interface IPendingActionStore
{
    bool TryAdd(string id, PendingAction action);
    bool TryGet(string id, out PendingAction? action);
    bool TryRemove(string id, out PendingAction? action);
}

public class PendingActionStore : IPendingActionStore, IDisposable
{
    private readonly ConcurrentDictionary<string, PendingAction> _map = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAdd(string id, PendingAction action) => _map.TryAdd(id, action);

    public bool TryGet(string id, out PendingAction? action) => _map.TryGetValue(id, out action!);

    public bool TryRemove(string id, out PendingAction? action) => _map.TryRemove(id, out action!);

    public void Dispose()
    {
        _map.Clear();
    }
}
