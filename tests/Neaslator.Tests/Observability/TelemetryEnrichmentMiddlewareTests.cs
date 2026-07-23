using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Neaslator.Observability;

namespace Neaslator.Tests.Observability;

/// <summary>
/// The middleware copies a fixed set of identity headers onto the current Activity so traces
/// carry tenant/user/owner/correlation context. It must tag only present, non-blank headers,
/// always call the next delegate, and never throw when there is no ambient Activity.
/// </summary>
public sealed class TelemetryEnrichmentMiddlewareTests
{
    private static async Task<bool> Invoke(HttpContext ctx)
    {
        bool nextCalled = false;
        var middleware = new TelemetryEnrichmentMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(ctx);
        return nextCalled;
    }

    [Fact]
    public async Task AllIdentityHeaders_CopiedToActivityTags()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Tenant-Id"] = "tenant-1";
        ctx.Request.Headers["X-User-Id"] = "user-2";
        ctx.Request.Headers["X-Owner-Id"] = "owner-3";
        ctx.Request.Headers["X-Correlation-Id"] = "corr-4";

        using var activity = new Activity("test").Start();
        bool nextCalled = await Invoke(ctx);
        activity.Stop();

        nextCalled.Should().BeTrue();
        activity.GetTagItem("neaslator.tenant_id").Should().Be("tenant-1");
        activity.GetTagItem("neaslator.user_id").Should().Be("user-2");
        activity.GetTagItem("neaslator.owner_id").Should().Be("owner-3");
        activity.GetTagItem("neaslator.correlation_id").Should().Be("corr-4");
    }

    [Fact]
    public async Task MissingHeaders_NoTagsAdded_NextStillCalled()
    {
        var ctx = new DefaultHttpContext();

        using var activity = new Activity("test").Start();
        bool nextCalled = await Invoke(ctx);
        activity.Stop();

        nextCalled.Should().BeTrue();
        activity.GetTagItem("neaslator.tenant_id").Should().BeNull();
        activity.GetTagItem("neaslator.user_id").Should().BeNull();
    }

    [Fact]
    public async Task WhitespaceHeaderValue_IsIgnored()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Tenant-Id"] = "   ";

        using var activity = new Activity("test").Start();
        await Invoke(ctx);
        activity.Stop();

        activity.GetTagItem("neaslator.tenant_id").Should().BeNull();
    }

    [Fact]
    public async Task PartialHeaders_OnlyPresentOnesTagged()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = "user-only";

        using var activity = new Activity("test").Start();
        await Invoke(ctx);
        activity.Stop();

        activity.GetTagItem("neaslator.user_id").Should().Be("user-only");
        activity.GetTagItem("neaslator.tenant_id").Should().BeNull();
    }

    [Fact]
    public async Task NoCurrentActivity_DoesNotThrow_NextStillCalled()
    {
        Activity? previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["X-Tenant-Id"] = "tenant-1";

            bool nextCalled = await Invoke(ctx);

            nextCalled.Should().BeTrue();
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
