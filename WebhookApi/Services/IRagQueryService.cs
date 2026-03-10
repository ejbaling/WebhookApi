namespace WebhookApi.Services;

public interface IRagQueryService
{
    /// <summary>
    /// Performs a semantic similarity search over the stored RAG document chunks and
    /// returns the text of the <paramref name="topK"/> most relevant chunks whose cosine
    /// distance is at or below <paramref name="maxDistance"/>.
    /// Cosine distance 0 = identical, 1 = orthogonal. Typical relevant matches fall below 0.40.
    /// </summary>
    Task<IReadOnlyList<string>> SearchAsync(string query, int topK = 3, double maxDistance = 0.40, CancellationToken cancellationToken = default);
}
