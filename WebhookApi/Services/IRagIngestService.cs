using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebhookApi.Services
{
    public interface IRagIngestService
    {
        Task<Guid> IngestDocumentAsync(string title, string source, string text, CancellationToken cancellationToken = default);
    }
}
