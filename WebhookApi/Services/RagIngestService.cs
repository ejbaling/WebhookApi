using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pgvector;
using RedwoodIloilo.Common.Entities;
using WebhookApi.Data;

namespace WebhookApi.Services
{
    public class RagIngestService : IRagIngestService
    {
        private readonly AppDbContext _db;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<RagIngestService> _logger;

        public RagIngestService(AppDbContext db, IHttpClientFactory httpFactory, IConfiguration config, ILogger<RagIngestService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<Guid> IngestDocumentAsync(string title, string source, string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required", nameof(text));

            var docId = Guid.NewGuid();

            // Create and insert a RagDocument instance using the NuGet-provided type via reflection-safe population
            var doc = Activator.CreateInstance(typeof(RagDocument));
            var docType = doc!.GetType();
            var idProp = docType.GetProperty("Id");
            idProp?.SetValue(doc, docId);
            var titleProp = docType.GetProperty("Title");
            titleProp?.SetValue(doc, title ?? string.Empty);
            var sourceProp = docType.GetProperty("Source");
            sourceProp?.SetValue(doc, source ?? string.Empty);
            var createdProp = docType.GetProperty("CreatedAt");
            createdProp?.SetValue(doc, DateTime.UtcNow);

            _db.Add(doc);
            await _db.SaveChangesAsync(cancellationToken);

            // Chunk the text
            var chunks = ChunkText(text, maxWords: 450).Select((chunkText, idx) => new { Index = idx, Text = chunkText }).ToList();

            // Get embeddings from configured AI provider
            var vectors = await GetEmbeddingsAsync(chunks.Select(c => c.Text).ToList(), cancellationToken);

            var chunkObjects = new List<object>();
            var chunkType = typeof(RagChunk);

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkObj = Activator.CreateInstance(chunkType)!;
                var pId = chunkType.GetProperty("Id");
                pId?.SetValue(chunkObj, Guid.NewGuid());
                var pDocId = chunkType.GetProperty("RagDocumentId");
                pDocId?.SetValue(chunkObj, docId);
                var pIndex = chunkType.GetProperty("ChunkIndex");
                if (pIndex == null) pIndex = chunkType.GetProperty("ChunkIdx") ?? chunkType.GetProperty("Ordinal");
                pIndex?.SetValue(chunkObj, i);
                var pText = chunkType.GetProperty("Text");
                pText?.SetValue(chunkObj, chunks[i].Text);
                var pCreated = chunkType.GetProperty("CreatedAt");
                pCreated?.SetValue(chunkObj, DateTime.UtcNow);

                // Try to set the Embedding property (Pgvector.Vector) if available
                try
                {
                    var embedding = vectors.ElementAtOrDefault(i);
                    if (embedding != null)
                    {
                        var floatArray = embedding.Select(d => (float)d).ToArray();
                        var vec = new Vector(floatArray);
                        var pEmbedding = chunkType.GetProperty("Embedding");
                        pEmbedding?.SetValue(chunkObj, vec);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to assign embedding for chunk {Index}", i);
                }

                // Optionally store embedding as JSON in MetadataJson for compatibility
                try
                {
                    var metaProp = chunkType.GetProperty("MetadataJson");
                    if (metaProp != null)
                    {
                        var emb = vectors.ElementAtOrDefault(i);
                        if (emb != null)
                        {
                            metaProp.SetValue(chunkObj, JsonSerializer.Serialize(emb));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to set MetadataJson for chunk {Index}", i);
                }

                chunkObjects.Add(chunkObj!);
            }

            // Add chunks via DbContext (untyped AddRange works)
            _db.AddRange(chunkObjects);
            await _db.SaveChangesAsync(cancellationToken);

            return docId;
        }

        private IEnumerable<string> ChunkText(string text, int maxWords = 500)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;
            var words = text.Split(null as char[], StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            var count = 0;
            foreach (var w in words)
            {
                sb.Append(w).Append(' ');
                count++;
                if (count >= maxWords)
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                    count = 0;
                }
            }
            if (sb.Length > 0) yield return sb.ToString().Trim();
        }

        private async Task<List<double[]>> GetEmbeddingsAsync(List<string> inputs, CancellationToken cancellationToken)
        {
            if (inputs == null || inputs.Count == 0) return new List<double[]>();

            var endpoint = _config["AI:Endpoint"]?.TrimEnd('/') ?? "http://10.0.0.106:11434";
            var apiKey = _config["AI:ApiKey"] ?? string.Empty;
            var model = _config["AI:EmbeddingModel"] ?? "text-embedding-3-small";
            var embeddingPath = _config["AI:EmbeddingPath"] ?? "/api/embeddings";

            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var requestUri = embeddingPath.StartsWith("/") ? embeddingPath : "/" + embeddingPath;
            var payload = new { model = model, input = inputs };

            try
            {
                using var resp = await client.PostAsJsonAsync(requestUri, payload, cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Embedding request failed: {Status} {Body}", resp.StatusCode, body);
                    return inputs.Select(_ => Array.Empty<double>()).ToList();
                }

                using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = doc.RootElement;
                var result = new List<double[]>();

                // OpenAI-style: { data: [ { embedding: [...] }, ... ] }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                        {
                            result.Add(ParseArrayToDoubles(emb));
                        }
                        else
                        {
                            result.Add(Array.Empty<double>());
                        }
                    }
                    return result;
                }

                // Ollama-style or simple array: either [[...],[...]] or { embeddings: [[...],[...]] }
                if (root.ValueKind == JsonValueKind.Array)
                {
                    // root is an array of embeddings or array of objects
                    if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arr in root.EnumerateArray())
                            result.Add(ParseArrayToDoubles(arr));
                        return result;
                    }
                    else if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object && root[0].TryGetProperty("embedding", out _))
                    {
                        foreach (var item in root.EnumerateArray())
                        {
                            if (item.TryGetProperty("embedding", out var emb)) result.Add(ParseArrayToDoubles(emb));
                            else result.Add(Array.Empty<double>());
                        }
                        return result;
                    }
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embeddings", out var embs) && embs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var arr in embs.EnumerateArray()) result.Add(ParseArrayToDoubles(arr));
                    return result;
                }

                // Single embedding returned as { embedding: [...] }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embedding", out var singleEmb) && singleEmb.ValueKind == JsonValueKind.Array)
                {
                    result.Add(ParseArrayToDoubles(singleEmb));
                    return result;
                }

                // Fallback: return empty vectors
                _logger.LogWarning("Embedding response shape not recognized; returning empty vectors");
                return inputs.Select(_ => Array.Empty<double>()).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call embedding API");
                return inputs.Select(_ => Array.Empty<double>()).ToList();
            }

            static double[] ParseArrayToDoubles(JsonElement arr)
            {
                var list = new List<double>();
                if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
                foreach (var v in arr.EnumerateArray())
                {
                    try
                    {
                        list.Add(v.GetDouble());
                    }
                    catch
                    {
                        // try as float
                        try { list.Add((double)v.GetDecimal()); } catch { }
                    }
                }
                return list.ToArray();
            }
        }
    }
}
