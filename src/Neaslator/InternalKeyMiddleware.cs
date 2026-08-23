namespace Neaslator;

/// <summary>
/// Requires the shared gateway secret on every translation endpoint.
///
/// neaslator is reached at <c>/translate/v1/**</c> through the identity gateway, but its container
/// also publishes port 5300, and it enforced nothing: <c>GET /translate/v1/languages</c>,
/// <c>/translate/v1/memory/stats</c> and <c>/translate/v1/providers/health</c> all answered 200 to
/// an unauthenticated caller, and <c>POST /translate/v1/on-demand</c> would spend provider quota
/// for anyone who asked.
///
/// This service already holds the secret — it sends <c>X-Internal-Key</c> outbound to reach
/// menu-service's editor endpoints. It simply never checked the header on the way in.
///
/// Exempt: the health probe, the service banner at <c>/</c>, and the SignalR hub, which negotiates
/// before any identity header is available and is guarded by its own connection handshake.
///
/// Fails closed when no key is configured, so a missing secret cannot silently reopen the service.
/// </summary>
public sealed class InternalKeyMiddleware
{
    public const string HeaderName = "X-Internal-Key";

    private static readonly string[] AnonymousPaths =
    [
        "/health",
        // /metrics, for the same reason as /health and with the same limits.
        //
        // A Prometheus scraper carries no shared secret and cannot be given one through the header
        // above without putting the estate's internal key in a scrape config. With this endpoint
        // behind the key check every scrape returned 401, so the service exported nothing — and
        // nothing reported it: the exporter was configured, the endpoint existed, and the only
        // symptom was a dashboard with no data, which reads as a dashboard nobody built yet.
        //
        // Not a hole. This is the container's own port on a ClusterIP Service with no Ingress rule
        // pointing at it, so /metrics is reachable only from inside the cluster — the same boundary
        // that already protects every service-to-service call.
        "/metrics",
        "/hubs",
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<InternalKeyMiddleware> _logger;
    private readonly string? _expectedKey;

    public InternalKeyMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<InternalKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // The same secret it already presents to menu-service, so there is one value to rotate.
        _expectedKey = configuration["MenuService:InternalApiKey"] ?? configuration["InternalApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;

        // The banner is the liveness signal for anything that cannot use /health.
        if (path == "/" || AnonymousPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_expectedKey))
        {
            _logger.LogError(
                "No internal key configured (MenuService:InternalApiKey). Rejecting {Method} {Path}.",
                context.Request.Method, path);

            await WriteUnauthorizedAsync(context);
            return;
        }

        string? presented = context.Request.Headers[HeaderName].FirstOrDefault();

        if (!CryptographicEquals(presented, _expectedKey))
        {
            _logger.LogWarning(
                "Rejected {Method} {Path}: missing or invalid {Header}.",
                context.Request.Method, path, HeaderName);

            await WriteUnauthorizedAsync(context);
            return;
        }

        await _next(context);
    }

    /// <summary>Length-independent comparison, so a wrong key leaks no timing signal.</summary>
    private static bool CryptographicEquals(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented))
            return false;

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(
            """{"type":"https://tools.ietf.org/html/rfc7235#section-3.1","title":"UNAUTHENTICATED","status":401,"detail":"This service is reachable only through the identity gateway."}""");
    }
}
