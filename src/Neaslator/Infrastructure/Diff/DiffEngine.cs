using Neaslator.Domain.Enums;
using Neaslator.Infrastructure.Hashing;
using Neaslator.Infrastructure.Normalization;

namespace Neaslator.Infrastructure.Diff;

public static class DiffEngine
{
    public static IReadOnlyList<TranslationUnit> ComputeDiff(
        MenuSnapshot currentSnapshot,
        MenuSnapshot? previousSnapshot)
    {
        List<TranslationUnit> units = [];

        if (previousSnapshot is null)
        {
            foreach (SectionSnapshot section in currentSnapshot.Sections)
            {
                if (!section.DoNotTranslateName)
                    AddSectionUnit(units, section);
                foreach (ItemSnapshot item in section.Items)
                {
                    AddItemUnits(units, item, section.Id);
                    foreach (SubItemSnapshot subItem in item.SubItems)
                        AddSubItemUnits(units, subItem, section.Id);
                }
            }
            return units;
        }

        Dictionary<Ulid, SectionSnapshot> previousSections =
            previousSnapshot.Sections.ToDictionary(s => s.Id);

        foreach (SectionSnapshot currentSection in currentSnapshot.Sections)
        {
            if (!previousSections.TryGetValue(currentSection.Id, out SectionSnapshot? prevSection))
            {
                if (!currentSection.DoNotTranslateName)
                    AddSectionUnit(units, currentSection);
                foreach (ItemSnapshot item in currentSection.Items)
                {
                    AddItemUnits(units, item, currentSection.Id);
                    foreach (SubItemSnapshot subItem in item.SubItems)
                        AddSubItemUnits(units, subItem, currentSection.Id);
                }
                continue;
            }

            if (!currentSection.DoNotTranslateName)
            {
                string currentSectionNorm = TextNormalizer.Normalize(currentSection.Name);
                string prevSectionNorm = TextNormalizer.Normalize(prevSection.Name);
                if (!currentSectionNorm.Equals(prevSectionNorm, StringComparison.Ordinal))
                    AddSectionUnit(units, currentSection);
            }

            Dictionary<Ulid, ItemSnapshot> previousItems =
                prevSection.Items.ToDictionary(i => i.Id);

            foreach (ItemSnapshot currentItem in currentSection.Items)
            {
                if (!previousItems.TryGetValue(currentItem.Id, out ItemSnapshot? prevItem))
                {
                    AddItemUnits(units, currentItem, currentSection.Id);
                    foreach (SubItemSnapshot subItem in currentItem.SubItems)
                        AddSubItemUnits(units, subItem, currentSection.Id);
                    continue;
                }

                if (!currentItem.DoNotTranslateName)
                {
                    string curName = TextNormalizer.Normalize(currentItem.Name);
                    string prevName = TextNormalizer.Normalize(prevItem.Name);
                    if (!curName.Equals(prevName, StringComparison.Ordinal))
                        AddNameUnit(units, currentItem, currentSection.Id);
                }

                if (!currentItem.DoNotTranslateDescription)
                {
                    string curDesc = TextNormalizer.Normalize((currentItem.Description ?? "").AsSpan());
                    string prevDesc = TextNormalizer.Normalize((prevItem.Description ?? "").AsSpan());
                    if (!curDesc.Equals(prevDesc, StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(currentItem.Description))
                        AddDescriptionUnit(units, currentItem, currentSection.Id);
                }

                Dictionary<Ulid, SubItemSnapshot> previousSubItems =
                    prevItem.SubItems.ToDictionary(si => si.Id);

                foreach (SubItemSnapshot currentSubItem in currentItem.SubItems)
                {
                    if (!previousSubItems.TryGetValue(currentSubItem.Id, out SubItemSnapshot? prevSubItem))
                    {
                        AddSubItemUnits(units, currentSubItem, currentSection.Id);
                        continue;
                    }

                    if (!currentSubItem.DoNotTranslateName)
                    {
                        string curSubName = TextNormalizer.Normalize(currentSubItem.Name);
                        string prevSubName = TextNormalizer.Normalize(prevSubItem.Name);
                        if (!curSubName.Equals(prevSubName, StringComparison.Ordinal))
                            AddSubItemNameUnit(units, currentSubItem, currentSection.Id);
                    }

                    if (!currentSubItem.DoNotTranslateDescription)
                    {
                        string curSubDesc = TextNormalizer.Normalize((currentSubItem.Description ?? "").AsSpan());
                        string prevSubDesc = TextNormalizer.Normalize((prevSubItem.Description ?? "").AsSpan());
                        if (!curSubDesc.Equals(prevSubDesc, StringComparison.Ordinal) &&
                            !string.IsNullOrEmpty(currentSubItem.Description))
                            AddSubItemDescriptionUnit(units, currentSubItem, currentSection.Id);
                    }
                }
            }
        }

        return units;
    }

    private static void AddSectionUnit(List<TranslationUnit> units, SectionSnapshot section)
    {
        string normalized = TextNormalizer.Normalize(section.Name);
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.SectionName,
            ParentSectionId = section.Id,
            ItemId = Ulid.Empty
        });
    }

    private static void AddItemUnits(List<TranslationUnit> units, ItemSnapshot item, Ulid sectionId)
    {
        if (!item.DoNotTranslateName)
            AddNameUnit(units, item, sectionId);
        if (!item.DoNotTranslateDescription && !string.IsNullOrEmpty(item.Description))
            AddDescriptionUnit(units, item, sectionId);
    }

    private static void AddNameUnit(List<TranslationUnit> units, ItemSnapshot item, Ulid sectionId)
    {
        string normalized = TextNormalizer.Normalize(item.Name);
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemName,
            ParentSectionId = sectionId,
            ItemId = item.Id
        });
    }

    private static void AddDescriptionUnit(List<TranslationUnit> units, ItemSnapshot item, Ulid sectionId)
    {
        string normalized = TextNormalizer.Normalize((item.Description ?? "").AsSpan());
        if (normalized.Length == 0)
            return;
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemDescription,
            ParentSectionId = sectionId,
            ItemId = item.Id
        });
    }

    private static void AddSubItemUnits(List<TranslationUnit> units, SubItemSnapshot subItem, Ulid sectionId)
    {
        if (!subItem.DoNotTranslateName)
            AddSubItemNameUnit(units, subItem, sectionId);
        if (!subItem.DoNotTranslateDescription && !string.IsNullOrEmpty(subItem.Description))
            AddSubItemDescriptionUnit(units, subItem, sectionId);
    }

    private static void AddSubItemNameUnit(List<TranslationUnit> units, SubItemSnapshot subItem, Ulid sectionId)
    {
        string normalized = TextNormalizer.Normalize(subItem.Name);
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemName,
            ParentSectionId = sectionId,
            ItemId = subItem.Id
        });
    }

    private static void AddSubItemDescriptionUnit(List<TranslationUnit> units, SubItemSnapshot subItem, Ulid sectionId)
    {
        string normalized = TextNormalizer.Normalize((subItem.Description ?? "").AsSpan());
        if (normalized.Length == 0)
            return;
        units.Add(new TranslationUnit
        {
            SourceHash = TranslationHasher.ComputeHash(normalized),
            NormalizedSourceText = normalized,
            UnitType = TranslationUnitType.ItemDescription,
            ParentSectionId = sectionId,
            ItemId = subItem.Id
        });
    }
}
