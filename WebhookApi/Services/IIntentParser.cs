using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebhookApi.Services
{
    public record IntentResult(string? Action, Dictionary<string,string>? Parameters, bool RequireConfirm);

    public interface IIntentParser
    {
        Task<IntentResult> ParseAsync(string text);
    }
}
