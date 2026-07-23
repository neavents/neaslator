using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neaslator.Infrastructure.Providers;

namespace Neaslator.Tests.Providers;

/// <summary>
/// Focused coverage of how <see cref="DeepSeekProvider"/> parses the many shapes an
/// LLM may return: bare arrays, markdown-fenced arrays, {"translations": [...]} wrappers,
/// single objects, and malformed content. These assert the intended contract — a real
/// LLM is unreliable, so the parser must be forgiving where it can and fail cleanly
/// (never throw) where it cannot.
/// </summary>
public sealed class DeepSeekProviderParsingTests
{
    private static DeepSeekProvider CreateProvider(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        string responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content } }
            },
            usage = new
            {
                prompt_tokens = 120,
                completion_tokens = 40,
                prompt_cache_hit_tokens = 30
            }
        });

        var http = new HttpClient(new StubHandler(status, responseJson))
        {
            BaseAddress = new Uri("https://api.deepseek.test")
        };
        var options = Options.Create(new DeepSeekOptions { Model = "deepseek-chat" });
        return new DeepSeekProvider(http, options);
    }

    private static TranslationBatchRequest Request(params long[] hashes)
    {
        return new TranslationBatchRequest
        {
            SourceLanguageCode = "en",
            TargetLanguageCode = "tr",
            VenueType = "Restaurant",
            CuisineType = "Italian",
            SectionName = "Appetizers",
            Items = hashes.Select(h => new TranslationBatchItem { SourceHash = h, Name = $"Item {h}" }).ToList()
        };
    }

    [Fact]
    public async Task BareJsonArray_ParsedSuccessfully()
    {
        string content = """[{"hash":100,"translated_name":"Ceviri","translated_description":"Aciklama"}]""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().ContainSingle();
        result.Translations[0].TranslatedName.Should().Be("Ceviri");
        result.Translations[0].TranslatedDescription.Should().Be("Aciklama");
    }

    [Fact]
    public async Task MarkdownFencedJson_FenceStrippedAndParsed()
    {
        string content = "```json\n[{\"hash\":100,\"translated_name\":\"Ceviri\"}]\n```";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().ContainSingle();
        result.Translations[0].TranslatedName.Should().Be("Ceviri");
    }

    [Fact]
    public async Task PlainFenceWithoutLanguage_FenceStrippedAndParsed()
    {
        string content = "```\n[{\"hash\":100,\"translated_name\":\"Ceviri\"}]\n```";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations[0].TranslatedName.Should().Be("Ceviri");
    }

    [Fact]
    public async Task TranslationsWrapperObject_ArrayExtracted()
    {
        string content = """{"translations":[{"hash":100,"translated_name":"A"},{"hash":101,"translated_name":"B"}]}""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100, 101), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().HaveCount(2);
        result.Translations.Select(t => t.TranslatedName).Should().ContainInOrder("A", "B");
    }

    [Fact]
    public async Task SingleBareObject_WrappedIntoArray()
    {
        string content = """{"hash":100,"translated_name":"Solo"}""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().ContainSingle();
        result.Translations[0].TranslatedName.Should().Be("Solo");
    }

    [Fact]
    public async Task LeadingAndTrailingWhitespace_Trimmed()
    {
        string content = "   \n [{\"hash\":100,\"translated_name\":\"Ceviri\"}]  \n ";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task MalformedJson_ReturnsFailureNeverThrows()
    {
        string content = "this is not json at all { [ ";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("JSON parse failed");
        result.Translations.Should().BeEmpty();
    }

    [Fact]
    public async Task NullDescriptionInResponse_MappedToNull()
    {
        string content = """[{"hash":100,"translated_name":"Ceviri","translated_description":null}]""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations[0].TranslatedDescription.Should().BeNull();
    }

    [Fact]
    public async Task TokenUsage_IncludingCachedTokens_Extracted()
    {
        string content = """[{"hash":100,"translated_name":"Ceviri"}]""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.TokenUsage.InputTokens.Should().Be(120);
        result.TokenUsage.OutputTokens.Should().Be(40);
        result.TokenUsage.CachedTokens.Should().Be(30);
    }

    [Fact]
    public async Task DuplicateHashInResponse_ReturnsFailure()
    {
        // Count matches (2 == 2) but hash 100 appears twice and 101 is missing.
        // Accepting this would silently drop item 101 from the translation output.
        string content = """[{"hash":100,"translated_name":"A"},{"hash":100,"translated_name":"B"}]""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100, 101), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Translations.Should().BeEmpty();
    }

    [Fact]
    public async Task WhitespaceOnlyTranslatedName_ReturnsFailure()
    {
        string content = """[{"hash":100,"translated_name":"   "}]""";
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Empty translated_name");
    }

    [Fact]
    public async Task OutOfOrderTranslations_MappedByHashNotPosition()
    {
        // The LLM returns the three items in reverse order; each translation carries its own
        // hash, so the result must stay bound to the right source unit regardless of position.
        string content = """
        [{"hash":102,"translated_name":"C"},{"hash":100,"translated_name":"A"},{"hash":101,"translated_name":"B"}]
        """;
        var provider = CreateProvider(content);

        var result = await provider.TranslateBatchAsync(Request(100, 101, 102), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().Contain(t => t.SourceHash == 100 && t.TranslatedName == "A");
        result.Translations.Should().Contain(t => t.SourceHash == 101 && t.TranslatedName == "B");
        result.Translations.Should().Contain(t => t.SourceHash == 102 && t.TranslatedName == "C");
    }

    [Fact]
    public async Task NegativeAndBoundaryHashes_RoundTripExactly()
    {
        // Hashes are Int64 reinterpreted from XxHash3 and are frequently negative; JSON number
        // handling must not lose or truncate them.
        long[] hashes = [long.MinValue, -1L, 0L, long.MaxValue];
        string items = string.Join(",", hashes.Select(h => $"{{\"hash\":{h},\"translated_name\":\"n{h}\"}}"));
        var provider = CreateProvider($"[{items}]");

        var result = await provider.TranslateBatchAsync(Request(hashes), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Select(t => t.SourceHash).Should().BeEquivalentTo(hashes);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }
}
