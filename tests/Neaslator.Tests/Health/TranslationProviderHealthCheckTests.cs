using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Providers;
using Neaslator.ServiceDefaults;

namespace Neaslator.Tests.Health;

/// <summary>
/// Whether this service can still reach something that translates.
/// </summary>
/// <remarks>
/// Every provider already implemented <c>IsHealthyAsync</c> and nothing called it. Postgres, Garnet
/// and RabbitMQ were checked — everything used to move work around, and nothing used to do the work
/// — so an exhausted API key left health green while every translation failed.
/// </remarks>
public sealed class TranslationProviderHealthCheckTests
{
    private sealed class StubProvider : ITranslationProvider
    {
        private readonly bool? _healthy;

        public StubProvider(string name, bool? healthy)
        {
            ProviderName = name;
            _healthy = healthy;
        }

        public string ProviderName { get; }
        public TranslationProviderTier Tier => TranslationProviderTier.Primary;
        public bool SupportsPrefixCaching => false;
        public int MaxBatchSize => 1;
        public int MaxConcurrentRequests => 1;

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
            _healthy is null
                ? Task.FromException<bool>(new HttpRequestException("provider exploded"))
                : Task.FromResult(_healthy.Value);

        public Task<TranslationBatchResult> TranslateBatchAsync(
            TranslationBatchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by the health check");
    }

    private static TranslationProviderHealthCheck Check(params ITranslationProvider[] providers) =>
        new(providers, NullLogger<TranslationProviderHealthCheck>.Instance);

    [Fact]
    public async Task AllProvidersReachableIsHealthy()
    {
        HealthCheckResult result = await Check(new StubProvider("deepseek", true), new StubProvider("gemini", true))
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    /// <summary>
    /// Losing the preferred provider is a real problem — quality and cost both change — without
    /// being an outage, because the chain still answers.
    /// </summary>
    [Fact]
    public async Task LosingOneTierIsDegradedNotUnhealthy()
    {
        HealthCheckResult result = await Check(new StubProvider("deepseek", false), new StubProvider("gemini", true))
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["deepseek"].Should().Be("unhealthy");
        result.Data["gemini"].Should().Be("healthy");
    }

    /// <summary>The case that used to be invisible: nothing can translate, and health said fine.</summary>
    [Fact]
    public async Task NoProviderReachableIsUnhealthy()
    {
        HealthCheckResult result = await Check(new StubProvider("deepseek", false), new StubProvider("gemini", false))
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    /// <summary>A provider that throws when asked whether it is well is not well.</summary>
    [Fact]
    public async Task AProviderThatThrowsCountsAsUnreachable()
    {
        HealthCheckResult result = await Check(new StubProvider("deepseek", null), new StubProvider("gemini", true))
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["deepseek"].Should().Be("unhealthy");
    }

    /// <summary>
    /// A configuration with no provider at all cannot translate anything, and should say so rather
    /// than reporting healthy on an empty set.
    /// </summary>
    [Fact]
    public async Task NoProvidersRegisteredIsUnhealthy()
    {
        HealthCheckResult result = await Check().CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
