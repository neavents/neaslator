namespace Neaslator.Domain.Entities;

public sealed class SupportedLanguage
{
    public string Code { get; set; } = default!;
    public string EnglishName { get; set; } = default!;
    public string NativeName { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public short SortOrder { get; set; }
}
