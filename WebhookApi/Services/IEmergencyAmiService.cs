using System.Threading;
using System.Threading.Tasks;

namespace WebhookApi.Services;

public interface IEmergencyAmiService
{
    Task TriggerEmergencyAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}
