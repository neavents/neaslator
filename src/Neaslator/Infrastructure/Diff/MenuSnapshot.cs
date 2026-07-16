namespace Neaslator.Infrastructure.Diff;

public sealed record MenuSnapshot
{
    public required IReadOnlyList<SectionSnapshot> Sections { get; init; }
}

public sealed record SectionSnapshot
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public bool DoNotTranslateName { get; init; }
    public bool DoNotTranslateDescription { get; init; }
    public required IReadOnlyList<ItemSnapshot> Items { get; init; }
}

public sealed record ItemSnapshot
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool DoNotTranslateName { get; init; }
    public bool DoNotTranslateDescription { get; init; }
    public IReadOnlyList<SubItemSnapshot> SubItems { get; init; } = [];
}

public sealed record SubItemSnapshot
{
    public required Ulid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool DoNotTranslateName { get; init; }
    public bool DoNotTranslateDescription { get; init; }
}
