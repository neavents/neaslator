namespace Neaslator.Infrastructure.Diff;

public sealed record MenuSnapshot
{
    /// <summary>The menu's own title.</summary>
    /// <remarks>
    /// The snapshot held only <see cref="Sections"/>, so the menu's title was never a translation
    /// unit: it was not diffed, not sent to a provider, and had nothing to assemble into the
    /// completed event. A menu translated into twenty-nine languages read "Mezeler" in all of them
    /// while its sections read "Kalte Vorspeisen" and "冷前菜", and coverage reported complete —
    /// truthfully, because every unit it counted had been translated.
    /// <para>
    /// Defaulted rather than required so a snapshot deserialised from a row written before this
    /// field existed still loads. An empty title produces no unit, which is the same as before.
    /// </para>
    /// </remarks>
    public string Name { get; init; } = string.Empty;

    /// <summary>The menu's own description, or null when it has none.</summary>
    public string? Description { get; init; }

    public bool DoNotTranslateName { get; init; }

    public bool DoNotTranslateDescription { get; init; }

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
