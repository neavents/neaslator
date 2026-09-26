namespace Neaslator.Domain.Entities;

public sealed class MenuPublishSnapshot
{
    public long Id { get; set; }
    public Ulid MenuId { get; set; }
    public Ulid OwnerId { get; set; }

    /// <summary>
    /// The organisation the menu belongs to, so a status read or retry can be scoped to the caller's.
    /// Null on rows written before it was recorded; those are stamped by the menu's next translation.
    /// </summary>
    public Ulid? TenantId { get; set; }
    public string SnapshotJson { get; set; } = default!;
    public DateTimeOffset PublishedAt { get; set; }
}
