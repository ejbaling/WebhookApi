using System.Threading;

namespace WebhookApi.Services;

public class LightsOffExecutor : IActionExecutor
{
    public string Name => "lights_off";

    public async Task<string> ExecuteAsync(Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        await Task.Delay(200, cancellationToken);
        return "Lights turned off.";
    }
}
