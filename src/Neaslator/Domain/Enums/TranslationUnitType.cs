namespace Neaslator.Domain.Enums;

public enum TranslationUnitType
{
    SectionName,
    ItemName,
    ItemDescription,

    // Appended, not inserted. These are persisted as integers, so putting the menu-level types at
    // the top — where they belong logically — would silently reinterpret every stored row.
    MenuName,
    MenuDescription
}
