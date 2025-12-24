using System.Threading;
using System.Threading.Tasks;

namespace WebhookApi.Services
{
    public record GuestClassificationResult(bool IsGood, double Score, string Label, string Reason);

    public interface IGuestClassifier
    {
        Task<GuestClassificationResult> ClassifyAsync(string combinedMessages, CancellationToken cancellationToken = default);
    }
}
