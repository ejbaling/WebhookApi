using System;
using System.Collections.Generic;
using System.Globalization;
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

        public async Task<Guid> IngestDocumentAsync(string title, string source, string text, string[]? tags = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required", nameof(text));

            var docId = Guid.NewGuid();

            // Create and insert a typed RagDocument instance and assign Tags directly
            var doc = new RagDocument
            {
                Id = docId,
                Title = title ?? string.Empty,
                Source = source ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
                Tags = tags ?? Array.Empty<string>()
            };

            // Ensure MetadataJson is stored as a JSON-typed CLR value when the entity exposes
            // a JSON-backed property (e.g. JsonDocument/JsonElement/object). This avoids
            // Npgsql sending a text parameter for a jsonb column which causes a Postgres error.
            try
            {
                var metaProp = doc.GetType().GetProperty("MetadataJson");
                if (metaProp != null && metaProp.CanWrite)
                {
                    var propType = metaProp.PropertyType;
                    if (propType == typeof(System.Text.Json.JsonDocument))
                    {
                        metaProp.SetValue(doc, System.Text.Json.JsonDocument.Parse("{}"));
                    }
                    else if (propType == typeof(System.Text.Json.JsonElement))
                    {
                        metaProp.SetValue(doc, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{}"));
                    }
                    else if (propType == typeof(object))
                    {
                        // set a plain JsonElement which EF/Npgsql can map to jsonb
                        metaProp.SetValue(doc, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{}"));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to set MetadataJson property via reflection; continuing without explicit JSON value.");
            }

                // Add the document via EF Core and set the shadow `MetadataJson` property
                // to a JsonDocument so Npgsql sends a jsonb parameter instead of text.
                _db.Add(doc);
                try
                {
                    // Set the shadow JSON property we mapped in AppDbContext
                    _db.Entry(doc).Property("_MetadataJsonShadow").CurrentValue = System.Text.Json.JsonDocument.Parse("{}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to set shadow MetadataJson property; proceeding with save.");
                }
                await _db.SaveChangesAsync(cancellationToken);

            // Chunk the text
            var chunks = ChunkText(text, maxWords: 450).Select((chunkText, idx) => new { Index = idx, Text = chunkText }).ToList();

            // Get embeddings from configured AI provider (id-mapped)
            var vectors = await GetEmbeddingsAsync(chunks.Select(c => c.Text).ToList(), cancellationToken);

            var chunkObjects = new List<RagChunk>();

            foreach (var c in chunks)
            {
                var chunk = new RagChunk
                {
                    Id = Guid.NewGuid(),
                    RagDocumentId = docId,
                    ChunkIndex = c.Index,
                    Text = c.Text,
                    CreatedAt = DateTime.UtcNow
                };

                // Try to set the Embedding property (Pgvector.Vector) if available
                if (vectors.TryGetValue(c.Index, out var embedding) && embedding != null && embedding.Length > 0)
                {
                    try
                    {
                        var floatArray = Array.ConvertAll(embedding, d => (float)d);
                        chunk.Embedding = new Vector(floatArray);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to assign embedding for chunk {Index}", c.Index);
                    }
                }

                chunkObjects.Add(chunk);
            }

            // Add chunks via DbContext (untyped AddRange works)
            _db.AddRange(chunkObjects);
            try
            {
                // Ensure each chunk has a jsonb-typed MetadataJson shadow value so Postgres
                // doesn't receive a plain text parameter for the jsonb column.
                foreach (var ch in chunkObjects)
                {
                    try
                    {
                        _db.Entry(ch).Property("_MetadataJsonShadow").CurrentValue = System.Text.Json.JsonDocument.Parse("{}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to set MetadataJson shadow for chunk {ChunkId}", ch.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed setting chunk metadata shadows");
            }

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

        private async Task<Dictionary<int, double[]>> GetEmbeddingsAsync(List<string> inputs, CancellationToken cancellationToken)
        {
            if (inputs == null || inputs.Count == 0)
            {
                var empty = new Dictionary<int, double[]>();
                return empty;
            }

            var endpoint = _config["AI:Endpoint"]?.TrimEnd('/') ?? "http://10.0.0.106:11434";
            var apiKey = _config["AI:ApiKey"] ?? string.Empty;
            var model = _config["AI:EmbeddingModel"] ?? "mxbai-embed-large";
            var embeddingPath = _config["AI:EmbeddingPath"] ?? "/api/embeddings";

            var client = _httpFactory.CreateClient();
            client.BaseAddress = new Uri(endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var requestUri = embeddingPath.StartsWith("/") ? embeddingPath : "/" + embeddingPath;

            // Detect Ollama-like endpoints/models which expect a single `prompt` per request
            var isOllama = endpoint.Contains("ollama", StringComparison.OrdinalIgnoreCase)
                           || endpoint.Contains(":11434")
                           || model.Contains("mxbai", StringComparison.OrdinalIgnoreCase)
                           || model.Contains("ollama", StringComparison.OrdinalIgnoreCase);

            var result = new Dictionary<int, double[]>();

            try
            {
                if (isOllama)
                {
                    // Ollama expects single-prompt requests. Call per input and read { "embedding": [...] } responses.
                    int idx = 0;
                    foreach (var text in inputs)
                    {
                        var singleReq = new { model = model, prompt = text };
                        using var resp = await client.PostAsJsonAsync(requestUri, singleReq, cancellationToken);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                            _logger.LogWarning("Embedding request failed (ollama): {Status} {Body}", resp.StatusCode, body);
                            // ensure index exists as empty
                            result[idx++] = Array.Empty<double>();
                            continue;
                        }

                        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                        using var docO = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                        var rootO = docO.RootElement;
                        if (rootO.ValueKind == JsonValueKind.Object && rootO.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                        {
                            result[idx++] = ParseArrayToDoubles(emb);
                        }
                        else if (rootO.ValueKind == JsonValueKind.Array && rootO.GetArrayLength() > 0 && rootO[0].ValueKind == JsonValueKind.Number)
                        {
                            // Some Ollama variants return a bare array
                            result[idx++] = ParseArrayToDoubles(rootO);
                        }
                        else
                        {
                            _logger.LogWarning("Embedding service returned unexpected shape for Ollama request (endpoint={Endpoint})", endpoint);
                            result[idx++] = Array.Empty<double>();
                        }
                    }

                    // pad any missing
                    for (int i = 0; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                    var returnedCount = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                    if (returnedCount < inputs.Count)
                        _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; provider=ollama)", returnedCount, inputs.Count, endpoint);
                    return result;
                }

                // Non-Ollama: send a single batch request using an array of strings (OpenAI-style)
                var payload = new { model = model, input = inputs };

                using var respBatch = await client.PostAsJsonAsync(requestUri, payload, cancellationToken);
                if (!respBatch.IsSuccessStatusCode)
                {
                    var body = await respBatch.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Embedding request failed: {Status} {Body}", respBatch.StatusCode, body);
                    return Enumerable.Range(0, inputs.Count).ToDictionary(i => i, _ => Array.Empty<double>());
                }

                using var streamBatch = await respBatch.Content.ReadAsStreamAsync(cancellationToken);
                using var docB = await JsonDocument.ParseAsync(streamBatch, cancellationToken: cancellationToken);
                var root = docB.RootElement;
                // Diagnose response shape to help triage missing/short responses
                string responseShape;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("data", out var _data) && _data.ValueKind == JsonValueKind.Array) responseShape = $"object.data[{_data.GetArrayLength()}]";
                    else if (root.TryGetProperty("embeddings", out var _embs) && _embs.ValueKind == JsonValueKind.Array) responseShape = $"object.embeddings[{_embs.GetArrayLength()}]";
                    else if (root.TryGetProperty("embedding", out _)) responseShape = "object.single_embedding";
                    else responseShape = "object.unknown";
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array) responseShape = $"array_of_arrays[{root.GetArrayLength()}]";
                    else if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object) responseShape = $"array_of_objects[{root.GetArrayLength()}]";
                    else responseShape = $"array[{root.GetArrayLength()}]";
                }
                else
                {
                    responseShape = $"other:{root.ValueKind}";
                }

                static int? ReadId(JsonElement item)
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("id", out var idEl))
                        {
                            if (idEl.ValueKind == JsonValueKind.Number) return idEl.GetInt32();
                            if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var v)) return v;
                        }
                        if (item.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number) return idxEl.GetInt32();
                    }
                    return null;
                }

                // OpenAI-style: { data: [ { embedding: [...] }, ... ] }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    int nextIndex = 0;
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                        {
                            var embArr = ParseArrayToDoubles(emb);
                            var id = ReadId(item) ?? nextIndex++;
                            result[id] = embArr;
                        }
                        else
                        {
                            var id = ReadId(item) ?? nextIndex++;
                            result[id] = Array.Empty<double>();
                        }
                    }
                    // log if fewer embeddings returned than requested
                    var returnedCount = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                    if (returnedCount < inputs.Count)
                    {
                        _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; shape={ResponseShape})", returnedCount, inputs.Count, endpoint, responseShape);
                    }

                    // ensure all requested ids exist (pad with empty arrays)
                    for (int i = 0; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                    return result;
                }

                // Ollama-style or simple array: either [[...],[...]] or { embeddings: [[...],[...]] }
                if (root.ValueKind == JsonValueKind.Array)
                {
                    // root is an array of embeddings or array of objects
                    if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
                    {
                        int idx = 0;
                        foreach (var arr in root.EnumerateArray())
                        {
                            result[idx++] = ParseArrayToDoubles(arr);
                        }
                        // log if fewer embeddings returned than requested
                        var returnedCountArr = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                        if (returnedCountArr < inputs.Count)
                        {
                            _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; shape={ResponseShape})", returnedCountArr, inputs.Count, endpoint, responseShape);
                        }
                        for (int i = idx; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                        return result;
                    }
                    else if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object && root[0].TryGetProperty("embedding", out _))
                    {
                        int nextIndex = 0;
                        foreach (var item in root.EnumerateArray())
                        {
                            var id = ReadId(item) ?? nextIndex++;
                            if (item.TryGetProperty("embedding", out var emb)) result[id] = ParseArrayToDoubles(emb);
                            else result[id] = Array.Empty<double>();
                        }
                        // log if fewer embeddings returned than requested
                        var returnedCountObj = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                        if (returnedCountObj < inputs.Count)
                        {
                            _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; shape={ResponseShape})", returnedCountObj, inputs.Count, endpoint, responseShape);
                        }
                        for (int i = 0; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                        return result;
                    }
                }

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embeddings", out var embs) && embs.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var arr in embs.EnumerateArray()) result[idx++] = ParseArrayToDoubles(arr);
                    var returnedCountEmbs = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                    if (returnedCountEmbs < inputs.Count)
                    {
                        _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; shape={ResponseShape})", returnedCountEmbs, inputs.Count, endpoint, responseShape);
                    }
                    for (int i = idx; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                    return result;
                }

                // Single embedding returned as { embedding: [...] }
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("embedding", out var singleEmb) && singleEmb.ValueKind == JsonValueKind.Array)
                {
                    result[0] = ParseArrayToDoubles(singleEmb);
                    var returnedCountSingle = result.Count(kv => kv.Value != null && kv.Value.Length > 0);
                    if (returnedCountSingle < inputs.Count)
                    {
                        _logger.LogWarning("Embedding service returned {Returned} embeddings for {Requested} inputs (endpoint={Endpoint}; shape={ResponseShape})", returnedCountSingle, inputs.Count, endpoint, responseShape);
                    }
                    for (int i = 1; i < inputs.Count; i++) if (!result.ContainsKey(i)) result[i] = Array.Empty<double>();
                    return result;
                }

                // Fallback: return empty vectors for all inputs
                _logger.LogWarning("Embedding response shape not recognized; returning empty vectors (shape={ResponseShape})", responseShape);
                return Enumerable.Range(0, inputs.Count).ToDictionary(i => i, _ => Array.Empty<double>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call embedding API");
                return Enumerable.Range(0, inputs.Count).ToDictionary(i => i, _ => Array.Empty<double>());
            }

            static double[] ParseArrayToDoubles(JsonElement arr)
            {
                if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<double>();
                var list = new List<double>();
                foreach (var v in arr.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.Number)
                    {
                        // Prefer TryGetDouble for safety
                        if (v.TryGetDouble(out var d)) list.Add(d);
                        else
                        {
                            try { list.Add((double)v.GetDecimal()); } catch { }
                        }
                    }
                    else if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                            list.Add(parsed);
                    }
                    // ignore other value kinds
                }
                return list.Count == 0 ? Array.Empty<double>() : list.ToArray();
            }
        }
    }
}
