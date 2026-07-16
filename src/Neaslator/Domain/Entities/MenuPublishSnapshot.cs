namespace Neaslator.Domain.Entities;

public sealed class MenuPublishSnapshot
{
    public long Id { get; set; }
    public Ulid MenuId { get; set; }
    public Ulid OwnerId { get; set; }
    public string SnapshotJson { get; set; } = default!;
    public DateTimeOffset PublishedAt { get; set; }
}
