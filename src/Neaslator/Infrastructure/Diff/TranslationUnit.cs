using Neaslator.Domain.Enums;

namespace Neaslator.Infrastructure.Diff;

public sealed record TranslationUnit
{
    public required long SourceHash { get; init; }
    public required string NormalizedSourceText { get; init; }
    public required TranslationUnitType UnitType { get; init; }
    public required Ulid ParentSectionId { get; init; }
    public required Ulid ItemId { get; init; }
}
