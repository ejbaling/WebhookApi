using System.Threading;

namespace WebhookApi.Services;

public interface IActionExecutor
{
    string Name { get; }
    Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken);
}
