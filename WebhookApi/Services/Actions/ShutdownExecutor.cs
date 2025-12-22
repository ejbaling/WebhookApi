using System.Threading;

namespace WebhookApi.Services;

public class ShutdownExecutor : IActionExecutor
{
    public string Name => "shutdown_server";

    public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var env = parameters.GetValueOrDefault("environment", "unknown");
        await Task.Delay(500, cancellationToken);
        return $"Server {env} shutdown executed.";
    }
}
