namespace WebhookApi.Services;

public interface IActionRegistry
{
    bool TryGetExecutor(string actionName, out IActionExecutor? executor);
    IEnumerable<string> ListActions();
}
