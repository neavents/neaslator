using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Neaslator.Domain.Enums;
using Neaslator.Observability;

namespace Neaslator.Infrastructure.Providers;

public sealed class DeepSeekProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly DeepSeekOptions _options;

    public string ProviderName => "deepseek";
    public TranslationProviderTier Tier => _options.Tier;
    public bool SupportsPrefixCaching => true;
    public int MaxBatchSize => _options.MaxBatchSize;
    public int MaxConcurrentRequests => _options.MaxConcurrentRequests;

    public DeepSeekProvider(HttpClient httpClient, IOptions<DeepSeekOptions> options)
    {
        _http = httpClient;
        _options = options.Value;
    }

    public async Task<TranslationBatchResult> TranslateBatchAsync(
        TranslationBatchRequest request,
        CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Provider.StartActivity("DeepSeekProvider.TranslateBatch");
        activity?.SetTag("neaslator.provider.name", "deepseek");
        activity?.SetTag("neaslator.provider.model", _options.Model);
        activity?.SetTag("neaslator.provider.batch_size", request.Items.Count);
        activity?.SetTag("neaslator.provider.source_language", request.SourceLanguageCode);
        activity?.SetTag("neaslator.provider.target_language", request.TargetLanguageCode);

        Stopwatch sw = Stopwatch.StartNew();

        string systemPrompt = TranslationPromptBuilder.BuildSystemPrompt(
            request.VenueType,
            request.CuisineType,
            request.SourceLanguageCode,
            request.TargetLanguageCode);

        string userPayload = TranslationPromptBuilder.BuildUserPayload(
            request.SectionName,
            request.Items);

        activity?.SetTag("neaslator.provider.system_prompt_length", systemPrompt.Length);
        activity?.SetTag("neaslator.provider.user_payload_length", userPayload.Length);

        DeepSeekChatRequest chatRequest = new()
        {
            Model = _options.Model,
            Messages =
            [
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user", Content = userPayload }
            ],
            Temperature = 0.1,
            MaxTokens = 4096,
            ResponseFormat = new() { Type = "json_object" }
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/chat/completions", chatRequest, cancellationToken);
            response.EnsureSuccessStatusCode();
            activity?.SetTag("neaslator.provider.http_status", (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            activity?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection([
                    new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                    new("exception.message", ex.Message)
                ])));
            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP request failed: {ex.Message}");
            activity?.SetTag("neaslator.provider.latency_ms", sw.Elapsed.TotalMilliseconds);
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = new(0, 0, 0),
                ErrorMessage = $"HTTP request failed: {ex.Message}",
                Latency = sw.Elapsed
            };
        }

        DeepSeekChatResponse? chatResponse = await response.Content
            .ReadFromJsonAsync<DeepSeekChatResponse>(cancellationToken);

        sw.Stop();

        TokenUsage tokenUsage = ExtractTokenUsage(chatResponse);
        activity?.SetTag("neaslator.provider.input_tokens", tokenUsage.InputTokens);
        activity?.SetTag("neaslator.provider.output_tokens", tokenUsage.OutputTokens);
        activity?.SetTag("neaslator.provider.cached_tokens", tokenUsage.CachedTokens);
        activity?.SetTag("neaslator.provider.latency_ms", sw.Elapsed.TotalMilliseconds);

        if (chatResponse?.Choices is not [{ Message.Content: string rawJson }, ..])
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Empty response from provider");
            activity?.AddEvent(new ActivityEvent("empty_response"));
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = tokenUsage,
                ErrorMessage = "Empty response from provider",
                Latency = sw.Elapsed
            };
        }

        activity?.SetTag("neaslator.provider.response_length", rawJson.Length);

        string cleaned = rawJson.Trim();
        if (cleaned.StartsWith("```"))
        {
            int firstNewline = cleaned.IndexOf('\n');
            int lastFence = cleaned.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                cleaned = cleaned[(firstNewline + 1)..lastFence].Trim();
            activity?.AddEvent(new ActivityEvent("markdown_fence_stripped"));
        }

        List<LlmTranslatedItem>? items;
        try
        {
            if (cleaned.StartsWith('{'))
            {
                using JsonDocument doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.TryGetProperty("translations", out JsonElement arr))
                    items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>(arr.GetRawText());
                else
                    items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>("[" + cleaned + "]");
            }
            else
            {
                items = JsonSerializer.Deserialize<List<LlmTranslatedItem>>(cleaned);
            }
        }
        catch (JsonException ex)
        {
            activity?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection([
                    new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                    new("exception.message", ex.Message)
                ])));
            activity?.SetStatus(ActivityStatusCode.Error, $"JSON parse failed: {ex.Message}");
            activity?.AddEvent(new ActivityEvent("json_parse_failed",
                tags: new ActivityTagsCollection([
                    new("exception_message", ex.Message),
                    new("response_preview", cleaned.Length > 200 ? cleaned[..200] : cleaned)
                ])));
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = tokenUsage,
                ErrorMessage = $"JSON parse failed: {ex.Message}",
                Latency = sw.Elapsed
            };
        }

        if (items is null || items.Count != request.Items.Count)
        {
            string errorMsg = $"Expected {request.Items.Count} items, got {items?.Count ?? 0}";
            activity?.SetStatus(ActivityStatusCode.Error, errorMsg);
            activity?.AddEvent(new ActivityEvent("item_count_mismatch",
                tags: new ActivityTagsCollection([
                    new("expected", request.Items.Count),
                    new("actual", items?.Count ?? 0)
                ])));
            return new TranslationBatchResult
            {
                IsSuccess = false,
                Translations = [],
                TokenUsage = tokenUsage,
                ErrorMessage = errorMsg,
                Latency = sw.Elapsed
            };
        }

        HashSet<long> expectedHashes = new(request.Items.Select(i => i.SourceHash));
        List<TranslatedUnit> translations = new(items.Count);

        foreach (LlmTranslatedItem item in items)
        {
            if (!expectedHashes.Contains(item.Hash))
            {
                string errorMsg = $"Unexpected hash {item.Hash} in response";
                activity?.SetStatus(ActivityStatusCode.Error, errorMsg);
                activity?.AddEvent(new ActivityEvent("unexpected_hash",
                    tags: new ActivityTagsCollection([new("hash", item.Hash)])));
                return new TranslationBatchResult
                {
                    IsSuccess = false,
                    Translations = [],
                    TokenUsage = tokenUsage,
                    ErrorMessage = errorMsg,
                    Latency = sw.Elapsed
                };
            }

            if (string.IsNullOrWhiteSpace(item.TranslatedName))
            {
                string errorMsg = $"Empty translated_name for hash {item.Hash}";
                activity?.SetStatus(ActivityStatusCode.Error, errorMsg);
                activity?.AddEvent(new ActivityEvent("empty_translation",
                    tags: new ActivityTagsCollection([new("hash", item.Hash)])));
                return new TranslationBatchResult
                {
                    IsSuccess = false,
                    Translations = [],
                    TokenUsage = tokenUsage,
                    ErrorMessage = errorMsg,
                    Latency = sw.Elapsed
                };
            }

            translations.Add(new TranslatedUnit
            {
                SourceHash = item.Hash,
                TranslatedName = item.TranslatedName,
                TranslatedDescription = item.TranslatedDescription
            });
        }

        activity?.SetTag("neaslator.provider.translated_count", translations.Count);
        activity?.AddEvent(new ActivityEvent("translation_successful",
            tags: new ActivityTagsCollection([
                new("translated_count", translations.Count),
                new("input_tokens", tokenUsage.InputTokens),
                new("output_tokens", tokenUsage.OutputTokens),
                new("cached_tokens", tokenUsage.CachedTokens)
            ])));

        return new TranslationBatchResult
        {
            IsSuccess = true,
            Translations = translations,
            TokenUsage = tokenUsage,
            Latency = sw.Elapsed
        };
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        using Activity? activity = NeaslatorActivitySources.Provider.StartActivity("DeepSeekProvider.HealthCheck");
        activity?.SetTag("neaslator.provider.name", "deepseek");

        try
        {
            HttpResponseMessage response = await _http.GetAsync("/models", cancellationToken);
            bool healthy = response.IsSuccessStatusCode;
            activity?.SetTag("neaslator.provider.healthy", healthy);
            activity?.SetTag("neaslator.provider.health_status", (int)response.StatusCode);
            return healthy;
        }
        catch (Exception ex)
        {
            activity?.AddEvent(new ActivityEvent("exception",
                tags: new ActivityTagsCollection([
                    new("exception.type", ex.GetType().FullName ?? ex.GetType().Name),
                    new("exception.message", ex.Message)
                ])));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("neaslator.provider.healthy", false);
            return false;
        }
    }

    private static TokenUsage ExtractTokenUsage(DeepSeekChatResponse? response)
    {
        if (response?.Usage is null)
            return new(0, 0, 0);
        return new(
            response.Usage.PromptTokens,
            response.Usage.CompletionTokens,
            response.Usage.PromptCacheHitTokens);
    }
}

public sealed class DeepSeekOptions
{
    public string Model { get; set; } = "deepseek-chat";
    public TranslationProviderTier Tier { get; set; } = TranslationProviderTier.Primary;
    public int MaxBatchSize { get; set; } = 20;
    public int MaxConcurrentRequests { get; set; } = 50;
}

internal sealed class DeepSeekChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = default!;
    [JsonPropertyName("messages")] public List<DeepSeekMessage> Messages { get; set; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
    [JsonPropertyName("response_format")] public DeepSeekResponseFormat? ResponseFormat { get; set; }
}

internal sealed class DeepSeekMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = default!;
    [JsonPropertyName("content")] public string Content { get; set; } = default!;
}

internal sealed class DeepSeekResponseFormat
{
    [JsonPropertyName("type")] public string Type { get; set; } = default!;
}

internal sealed class DeepSeekChatResponse
{
    [JsonPropertyName("choices")] public List<DeepSeekChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public DeepSeekUsage? Usage { get; set; }
}

internal sealed class DeepSeekChoice
{
    [JsonPropertyName("message")] public DeepSeekMessage Message { get; set; } = default!;
}

internal sealed class DeepSeekUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("prompt_cache_hit_tokens")] public int PromptCacheHitTokens { get; set; }
}

internal sealed class LlmTranslatedItem
{
    [JsonPropertyName("hash")] public long Hash { get; set; }
    [JsonPropertyName("translated_name")] public string TranslatedName { get; set; } = default!;
    [JsonPropertyName("translated_description")] public string? TranslatedDescription { get; set; }
}
