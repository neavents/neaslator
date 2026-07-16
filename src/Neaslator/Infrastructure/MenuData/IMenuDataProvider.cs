using Neaslator.Infrastructure.Diff;

namespace Neaslator.Infrastructure.MenuData;

public interface IMenuDataProvider
{
    Task<MenuSnapshot?> GetMenuSnapshotAsync(Ulid menuId, CancellationToken ct);
}
