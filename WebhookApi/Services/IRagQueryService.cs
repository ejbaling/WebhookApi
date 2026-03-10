namespace WebhookApi.Services;

public interface IRagQueryService
{
    /// <summary>
    /// Performs a semantic similarity search over the stored RAG document chunks and
    /// returns the text of the <paramref name="topK"/> most relevant chunks.
    /// </summary>
    Task<IReadOnlyList<string>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default);
}
