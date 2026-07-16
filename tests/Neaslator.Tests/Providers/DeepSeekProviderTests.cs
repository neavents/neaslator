using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Providers;

namespace Neaslator.Tests.Providers;

public sealed class DeepSeekProviderTests
{
    private static DeepSeekProvider CreateProvider(HttpMessageHandler handler, TranslationProviderTier tier = TranslationProviderTier.Primary)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deepseek.test") };
        var options = Options.Create(new DeepSeekOptions
        {
            Model = "deepseek-chat",
            Tier = tier,
            MaxBatchSize = 20,
            MaxConcurrentRequests = 4
        });
        return new DeepSeekProvider(http, options);
    }

    private static TranslationBatchRequest CreateValidRequest(int itemCount = 2)
    {
        var items = Enumerable.Range(0, itemCount).Select(i => new TranslationBatchItem
        {
            SourceHash = 100 + i,
            Name = $"Item {i}",
            Description = $"Desc {i}"
        }).ToList();

        return new TranslationBatchRequest
        {
            SourceLanguageCode = "en",
            TargetLanguageCode = "tr",
            VenueType = "Restaurant",
            CuisineType = "Italian",
            SectionName = "Appetizers",
            Items = items
        };
    }

    private static string CreateSuccessResponse(int itemCount = 2)
    {
        var translations = Enumerable.Range(0, itemCount).Select(i => new
        {
            hash = 100L + i,
            translated_name = $"Ceviri {i}",
            translated_description = $"Aciklama {i}"
        }).ToList();

        var response = new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = JsonSerializer.Serialize(translations)
                    }
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        };
        return JsonSerializer.Serialize(response);
    }

    [Fact]
    public void ProviderName_ReturnsDeepseek()
    {
        var provider = CreateProvider(new FakeHttpHandler());
        provider.ProviderName.Should().Be("deepseek");
    }

    [Fact]
    public void Tier_ReturnsConfiguredTier()
    {
        var provider = CreateProvider(new FakeHttpHandler(), TranslationProviderTier.Secondary);
        provider.Tier.Should().Be(TranslationProviderTier.Secondary);
    }

    [Fact]
    public void SupportsPrefixCaching_IsTrue()
    {
        var provider = CreateProvider(new FakeHttpHandler());
        provider.SupportsPrefixCaching.Should().BeTrue();
    }

    [Fact]
    public async Task TranslateBatchAsync_Success_ReturnsTranslatedItems()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, CreateSuccessResponse(2));
        var provider = CreateProvider(handler);
        var request = CreateValidRequest(2);

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Translations.Should().HaveCount(2);
        result.Translations[0].TranslatedName.Should().Be("Ceviri 0");
        result.Translations[1].TranslatedName.Should().Be("Ceviri 1");
        result.TokenUsage.InputTokens.Should().Be(100);
        result.TokenUsage.OutputTokens.Should().Be(50);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task TranslateBatchAsync_HttpError_ReturnsFailedResult()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.ServiceUnavailable);
        var provider = CreateProvider(handler);
        var request = CreateValidRequest();

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Translations.Should().BeEmpty();
    }

    [Fact]
    public async Task TranslateBatchAsync_EmptyResponse_ReturnsFailedResult()
    {
        var emptyResponse = JsonSerializer.Serialize(new
        {
            choices = System.Array.Empty<object>(),
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
        });
        var handler = new FakeHttpHandler(HttpStatusCode.OK, emptyResponse);
        var provider = CreateProvider(handler);
        var request = CreateValidRequest();

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TranslateBatchAsync_ItemCountMismatch_ReturnsFailedResult()
    {
        var translations = Enumerable.Range(0, 2).Select(i => new
        {
            hash = 100L + i,
            translated_name = $"Item {i}",
            translated_description = $"Desc {i}"
        }).ToList();

        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = JsonSerializer.Serialize(translations)
                    }
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var provider = CreateProvider(handler);
        var request = CreateValidRequest(3);

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Expected 3");
    }

    [Fact]
    public async Task TranslateBatchAsync_UnexpectedHash_ReturnsFailedResult()
    {
        var translations = new[]
        {
            new { hash = 100L, translated_name = "Item 0", translated_description = "Desc 0" },
            new { hash = 999L, translated_name = "Bad Hash", translated_description = "Bad" }
        };

        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = JsonSerializer.Serialize(translations)
                    }
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var provider = CreateProvider(handler);
        var request = CreateValidRequest(2);

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unexpected hash");
    }

    [Fact]
    public async Task TranslateBatchAsync_EmptyTranslatedName_ReturnsFailedResult()
    {
        var translations = new[]
        {
            new { hash = 100L, translated_name = "", translated_description = "Desc 0" },
            new { hash = 101L, translated_name = "Item 1", translated_description = "Desc 1" }
        };

        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = JsonSerializer.Serialize(translations)
                    }
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        });

        var handler = new FakeHttpHandler(HttpStatusCode.OK, responseJson);
        var provider = CreateProvider(handler);
        var request = CreateValidRequest(2);

        var result = await provider.TranslateBatchAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Empty translated_name");
    }

    [Fact]
    public async Task IsHealthyAsync_SuccessResponse_ReturnsTrue()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.OK, "{}");
        var provider = CreateProvider(handler);

        var healthy = await provider.IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_ErrorResponse_ReturnsFalse()
    {
        var handler = new FakeHttpHandler(HttpStatusCode.InternalServerError);
        var provider = CreateProvider(handler);

        var healthy = await provider.IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeFalse();
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseBody;

        public FakeHttpHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string? responseBody = null)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody is not null)
            {
                response.Content = new StringContent(_responseBody);
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            return Task.FromResult(response);
        }
    }
}
