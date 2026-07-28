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
    Task<MenuSnapshot?> GetMenuSnapshotAsync(Ulid menuId, Ulid ownerId, CancellationToken ct);
}
