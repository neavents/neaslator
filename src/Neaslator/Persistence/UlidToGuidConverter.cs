using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Neaslator.Persistence;

public sealed class UlidToGuidConverter : ValueConverter<Ulid, Guid>
{
    public UlidToGuidConverter()
        : base(
            ulid => ulid.ToGuid(),
            guid => new Ulid(guid))
    {
    }

    public static readonly UlidToGuidConverter Instance = new();
}
