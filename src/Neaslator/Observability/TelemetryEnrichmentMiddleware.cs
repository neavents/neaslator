using System.Diagnostics;

namespace Neaslator.Observability;

public sealed class TelemetryEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public TelemetryEnrichmentMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        Activity? activity = Activity.Current;

        if (activity is not null)
        {
            if (context.Request.Headers.TryGetValue("X-Tenant-Id", out Microsoft.Extensions.Primitives.StringValues tenantId)
                && tenantId.Count > 0
                && !string.IsNullOrWhiteSpace(tenantId[0]))
            {
                activity.SetTag("neaslator.tenant_id", tenantId[0]);
            }

            if (context.Request.Headers.TryGetValue("X-User-Id", out Microsoft.Extensions.Primitives.StringValues userId)
                && userId.Count > 0
                && !string.IsNullOrWhiteSpace(userId[0]))
            {
                activity.SetTag("neaslator.user_id", userId[0]);
            }

            if (context.Request.Headers.TryGetValue("X-Owner-Id", out Microsoft.Extensions.Primitives.StringValues ownerId)
                && ownerId.Count > 0
                && !string.IsNullOrWhiteSpace(ownerId[0]))
            {
                activity.SetTag("neaslator.owner_id", ownerId[0]);
            }

            if (context.Request.Headers.TryGetValue("X-Correlation-Id", out Microsoft.Extensions.Primitives.StringValues correlationId)
                && correlationId.Count > 0
                && !string.IsNullOrWhiteSpace(correlationId[0]))
            {
                activity.SetTag("neaslator.correlation_id", correlationId[0]);
            }
        }

        await _next(context);
    }
}
