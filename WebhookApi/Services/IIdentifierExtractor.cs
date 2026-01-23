using System.Threading;
using System.Threading.Tasks;

namespace WebhookApi.Services;

public record IdentifierResult(
    string? Name,
    string? GuestName,
    string? HostName,
    string? Email,
    string? Phone,
    string? BookingId,
    string? AirbnbId,
    string? Amount);

public interface IIdentifierExtractor
{
    Task<IdentifierResult> ExtractAsync(string text, CancellationToken cancellationToken = default);
}
