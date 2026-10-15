using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Neaslator.Infrastructure.LanguagePolicy;

/// <summary>
/// Reads identity's menu.translation.languages for a venue from its menu-defaults endpoint and keeps the answer in Garnet,
/// so a translation run costs no identity call while the answer is cached and every replica sees the same answer.
/// </summary>
/// <remarks>
/// An identity that does not answer fails the run instead of translating everything: an organisation that restricted its
/// languages would otherwise be billed for all of them during an outage, and the redelivered run loses nothing by waiting.
/// </remarks>
public sealed partial class IdentityTranslationLanguagePolicySource : ITranslationLanguagePolicySource
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConnectionMultiplexer _garnet;
    private readonly ILogger<IdentityTranslationLanguagePolicySource> _logger;

    public IdentityTranslationLanguagePolicySource(
        HttpClient http, IConnectionMultiplexer garnet, ILogger<IdentityTranslationLanguagePolicySource> logger)
    {
        _http = http;
        _garnet = garnet;
        _logger = logger;
    }

    public static string OwnerKey(Ulid ownerId) => $"neaslator:translation-languages:{ownerId}";

    public static string OrganisationKey(Ulid orgId) => $"neaslator:translation-languages:org:{orgId}";

    public async Task<TranslationLanguagePolicy> GetAsync(Ulid ownerId, CancellationToken cancellationToken)
    {
        if (_http.BaseAddress is null)
            return TranslationLanguagePolicy.Unrestricted;

        IDatabase cache = _garnet.GetDatabase();
        try
        {
            RedisValue cached = await cache.StringGetAsync(OwnerKey(ownerId)).ConfigureAwait(false);
            if (cached.HasValue && JsonSerializer.Deserialize<Cached>(cached.ToString(), Json) is { } hit)
                return new TranslationLanguagePolicy(hit.Languages);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(_logger, ex, ownerId);
        }

        using HttpResponseMessage response = await _http
            .GetAsync($"/internal/v1/owners/{ownerId}/menu-defaults", cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return TranslationLanguagePolicy.Unrestricted;

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"identity answered {(int)response.StatusCode} for the translation languages of {ownerId}; the run waits for it.",
                null, response.StatusCode);

        MenuDefaults body = await response.Content.ReadFromJsonAsync<MenuDefaults>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException($"identity sent no menu defaults for {ownerId}.");

        await RememberAsync(cache, ownerId, body).ConfigureAwait(false);
        return new TranslationLanguagePolicy(body.TranslationLanguages);
    }

    public async Task ForgetOrganisationAsync(Ulid orgId, CancellationToken cancellationToken)
    {
        IDatabase cache = _garnet.GetDatabase();
        RedisValue[] owners = await cache.SetMembersAsync(OrganisationKey(orgId)).ConfigureAwait(false);
        RedisKey[] keys = [.. owners.Select(o => (RedisKey)o.ToString()), OrganisationKey(orgId)];
        await cache.KeyDeleteAsync(keys).ConfigureAwait(false);
    }

    private async Task RememberAsync(IDatabase cache, Ulid ownerId, MenuDefaults body)
    {
        if (!Ulid.TryParse(body.OrgId, out Ulid orgId)) return;

        try
        {
            string value = JsonSerializer.Serialize(new Cached(body.TranslationLanguages), Json);
            await cache.SetAddAsync(OrganisationKey(orgId), OwnerKey(ownerId)).ConfigureAwait(false);
            await cache.KeyExpireAsync(OrganisationKey(orgId), CacheFor + CacheFor).ConfigureAwait(false);
            await cache.StringSetAsync(OwnerKey(ownerId), value, CacheFor).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(_logger, ex, ownerId);
        }
    }

    private sealed record MenuDefaults(
        [property: JsonPropertyName("orgId")] string? OrgId,
        [property: JsonPropertyName("translationLanguages")] IReadOnlyList<string>? TranslationLanguages);

    private sealed record Cached(IReadOnlyList<string>? Languages);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning,
        Message = "Garnet did not answer for the translation languages of {OwnerId}; asking identity directly")]
    private static partial void LogCacheUnavailable(ILogger logger, Exception ex, Ulid ownerId);
}
