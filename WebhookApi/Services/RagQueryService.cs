using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using System.Text.Json;
using WebhookApi.Data;

namespace WebhookApi.Services;

/// <summary>
/// Performs semantic similarity search against stored RAG document chunks using
/// the same embedding model that was used at ingest time.
/// </summary>
public class RagQueryService : IRagQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<RagQueryService> _logger;

    public RagQueryService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<RagQueryService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var embedding = await GetEmbeddingAsync(query, cancellationToken);
        if (embedding == null || embedding.Length == 0)
        {
            _logger.LogWarning("RAG query: failed to generate embedding for query; returning empty results.");
            return [];
        }

        var queryVector = new Vector(Array.ConvertAll(embedding, d => (float)d));

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var chunks = await dbContext.RagChunks
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(queryVector))
            .Take(topK)
            .Select(c => c.Text)
            .ToListAsync(cancellationToken);

        var preview = query.Length > 100 ? query.Substring(0, 100) + "..." : query;
        _logger.LogInformation("RAG query returned {Count} chunks for query: {Query}", chunks.Count, preview);

        return chunks;
    }

    private async Task<double[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var endpoint = _config["AI:Endpoint"]?.TrimEnd('/') ?? "http://10.0.0.106:11434";
        var apiKey = _config["AI:ApiKey"] ?? string.Empty;
        var model = _config["AI:EmbeddingModel"] ?? "mxbai-embed-large";
        var embeddingPath = _config["AI:EmbeddingPath"] ?? "/api/embeddings";

        var client = _httpFactory.CreateClient();
        client.BaseAddress = new Uri(endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var requestUri = embeddingPath.StartsWith("/") ? embeddingPath : "/" + embeddingPath;

        // Detect Ollama-like endpoints (same heuristic as RagIngestService)
        var isOllama = endpoint.Contains("ollama", StringComparison.OrdinalIgnoreCase)
                       || endpoint.Contains(":11434")
                       || model.Contains("mxbai", StringComparison.OrdinalIgnoreCase)
                       || model.Contains("ollama", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (isOllama)
            {
                var req = new { model, prompt = text };
                using var resp = await client.PostAsJsonAsync(requestUri, req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Embedding request failed: {Status}", resp.StatusCode);
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                    return emb.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            }
            else
            {
                var req = new { model, input = new[] { text } };
                using var resp = await client.PostAsJsonAsync(requestUri, req, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Embedding request failed: {Status}", resp.StatusCode);
                    return null;
                }

                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.GetArrayLength() > 0
                    && data[0].TryGetProperty("embedding", out var emb)
                    && emb.ValueKind == JsonValueKind.Array)
                    return emb.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching embedding for RAG query");
        }

        return null;
    }
}
