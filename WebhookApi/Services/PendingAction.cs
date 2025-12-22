namespace WebhookApi.Services;

public class PendingAction
{
    public string ActionName { get; init; }
    public Dictionary<string, string> Parameters { get; init; }
    public long RequestedBy { get; init; }
    public DateTime CreatedAt { get; init; }

    public PendingAction(string actionName, Dictionary<string, string> parameters, long requestedBy, DateTime createdAt)
    {
        ActionName = actionName;
        Parameters = parameters;
        RequestedBy = requestedBy;
        CreatedAt = createdAt;
    }
}
