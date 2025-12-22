using System.Collections.Concurrent;

namespace WebhookApi.Services;

public class ActionRegistry : IActionRegistry
{
    private readonly ConcurrentDictionary<string, IActionExecutor> _map = new(StringComparer.OrdinalIgnoreCase);

    public ActionRegistry(IEnumerable<IActionExecutor> executors)
    {
        foreach (var e in executors)
        {
            if (!string.IsNullOrWhiteSpace(e.Name))
                _map.TryAdd(e.Name, e);
        }
    }

    public bool TryGetExecutor(string actionName, out IActionExecutor? executor)
        => _map.TryGetValue(actionName, out executor);

    public IEnumerable<string> ListActions() => _map.Keys;
}
