namespace Neaslator;

/// <summary>
/// Who a request is from, read from the headers the identity gateway injects.
/// </summary>
/// <remarks>
/// Trustworthy only behind <see cref="InternalKeyMiddleware"/>. The gateway strips every client-supplied
/// copy of these headers and attaches the shared key only to requests it resolved to a user — always with
/// <c>X-User-Id</c> — so the key without a user id is another service calling directly.
/// </remarks>
public sealed record GatewayCaller(Ulid? TenantId, bool IsPlatformAdmin, bool IsServiceCall)
{
    public const string PlatformAdminRole = "platform_admin";

    public static GatewayCaller From(HttpRequest request)
    {
        bool isServiceCall = !request.Headers.ContainsKey("X-User-Id");
        Ulid? tenantId = Ulid.TryParse(request.Headers["X-Tenant-Id"].FirstOrDefault(), out Ulid parsed) ? parsed : null;
        bool isPlatformAdmin = string.Equals(
            request.Headers["X-Account-Role"].FirstOrDefault(), PlatformAdminRole, StringComparison.OrdinalIgnoreCase);

        return new GatewayCaller(tenantId, isPlatformAdmin, isServiceCall);
    }

    public bool SeesEveryTenant => IsServiceCall || IsPlatformAdmin;

    /// <summary>
    /// Whether this caller may see data belonging to <paramref name="ownerTenantId"/>. Data whose tenant was
    /// never recorded is visible only to callers that see every tenant.
    /// </summary>
    public bool MaySee(Ulid? ownerTenantId) =>
        SeesEveryTenant || (ownerTenantId is not null && ownerTenantId == TenantId);
}
