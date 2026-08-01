using Neaslator.Infrastructure.Diff;

namespace Neaslator.Infrastructure.MenuData;

public interface IMenuDataProvider
{
    /// <summary>
    /// Reads a menu for translation.
    ///
    /// <paramref name="ownerId"/> is the venue that owns the menu, and it is required: the
    /// editor endpoint is tenant-scoped, so a request without it matches no rows and comes
    /// back 404 — which would look like a deleted menu rather than a missing header.
    /// </summary>
    /// <param name="tenantId">
    /// The organisation that owns the menu. Distinct from <paramref name="ownerId"/>, which is the
    /// venue or event it hangs off. Null when the publisher predates the field, in which case the
    /// implementation falls back to the owner — the previous behaviour, correct only while the two
    /// were always equal.
    /// </param>
    Task<MenuSnapshot?> GetMenuSnapshotAsync(
        Ulid menuId, Ulid ownerId, Ulid? tenantId, CancellationToken ct);
}
