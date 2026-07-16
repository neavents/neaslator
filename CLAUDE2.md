# Phase 13: Edge API Translation Consumer — Detailed Implementation

> This document covers the downstream integration: how your existing edge API
> consumes `MenuTranslationCompletedEvent` from Neaslator and pushes translated
> menu documents to Cloudflare KV.
>
> **This code lives in the edge API project, NOT in Neaslator.**

---

## Data Flow

```
Neaslator finishes translating
        │
        ▼
Publishes MenuTranslationCompletedEvent to RabbitMQ
        │
        ▼
Edge API's TranslationCompletedConsumer receives event
        │
        ├──→ Fetches current menu structure (sections, items, prices, images, ordering)
        │    from the same source the edge API already uses for default-language KV builds
        │
        ├──→ Extracts translation data from the event payload
        │    (translated name + description per item per language)
        │
        ▼
For each completed language:
    Build a KV document identical in shape to the default-language document,
    but with translated section names, item names, and item descriptions.
    All non-translatable fields (price, image, availability, sort order) stay identical.
        │
        ▼
Cloudflare KV Bulk Write:
    Key: {existingKeyFormat}:{languageCode}
    Value: the per-language JSON document
        │
        ▼
(Optional) SignalR notification to venue dashboard: "Translations live"
```

---

## Step 1: Understand What You Already Have

Before writing anything, read the existing edge API code. You are looking for:

```
1. The function/class that builds the menu JSON document for KV.
   This is the "compiler" — it takes raw menu data and produces the JSON shape
   that the Cloudflare Worker expects.

2. The Cloudflare KV write function.
   This is how the edge API currently pushes to KV.

3. The menu data source.
   How does the edge API currently get the menu structure?
   - Direct DB query to the menu service database?
   - API call to the menu service?
   - Data embedded in the existing mutation event?
```

Record:
- [ ] The exact JSON shape of a KV menu document (every field, every nesting level)
- [ ] The KV key format (e.g., `menu:{menuId}`, `venue:{venueId}:menu:{menuId}`)
- [ ] The menu data access pattern (DB query, HTTP call, event payload)
- [ ] The Cloudflare API client class and its bulk write method

---

## Step 2: Enrich the Event Contract

The `MenuTranslationCompletedEvent` in the shared contracts project needs to carry
enough data for the consumer to build KV documents without calling back to Neaslator.

### What the event needs to carry:

```csharp
public sealed record MenuTranslationCompletedEvent
{
    public required Guid MenuId { get; init; }
    public required Guid VenueId { get; init; }
    public required string SourceLanguageCode { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required int TotalLanguages { get; init; }
    public required int CompletedLanguages { get; init; }
    public required int FailedLanguages { get; init; }
    public required IReadOnlyList<string> FailedLanguageCodes { get; init; }
    public required IReadOnlyList<TranslatedMenuLanguage> TranslatedMenus { get; init; }
}

public sealed record TranslatedMenuLanguage
{
    public required string LanguageCode { get; init; }
    public required IReadOnlyList<TranslatedSectionData> Sections { get; init; }
}

public sealed record TranslatedSectionData
{
    public required Guid SectionId { get; init; }       // ADAPT: match ID type
    public required string TranslatedName { get; init; }
    public required IReadOnlyList<TranslatedItemData> Items { get; init; }
}

public sealed record TranslatedItemData
{
    public required Guid ItemId { get; init; }          // ADAPT: match ID type
    public required string TranslatedName { get; init; }
    public string? TranslatedDescription { get; init; }
}
```

### Why the event carries translation data directly:

- The translations were just computed — they are already in memory.
- Avoids a network round-trip from consumer back to Neaslator.
- A typical payload: 75 languages × 50 items × ~100 bytes = ~375 KB. RabbitMQ handles this easily.
- The event is self-contained — the consumer does not need to query anyone.

### What the event does NOT carry:

- Prices, images, availability, sort order, allergens, or any non-translatable field.
- The consumer fetches these from the menu data source it already uses.

---

## Step 3: Neaslator — Build the Event Payload Before Publishing

Back in Neaslator's `StartTranslationConsumer`, after the pipeline completes,
compile the event payload from the snapshot + translation memory.

**Update `StartTranslationConsumer.cs` in Neaslator:**

```csharp
// After pipeline.ExecuteAsync completes and snapshot is saved...

// Build the event payload with actual translation data
List<TranslatedMenuLanguage> translatedMenus = [];

foreach (LanguageResult langResult in result.Results.Where(r => r.IsSuccess))
{
    List<TranslatedSectionData> translatedSections = [];

    foreach (SectionSnapshot section in currentSnapshot.Sections)
    {
        // Fetch section name translation from cache
        string sectionNameNormalized = TextNormalizer.Normalize(section.Name);
        long sectionNameHash = TranslationHasher.ComputeHash(sectionNameNormalized);

        IReadOnlyList<CacheLookupResult> sectionLookup = await cache.LookupAsync(
            sectionNameHash,
            sectionNameNormalized,
            command.SourceLanguageCode,
            [langResult.TargetLanguageCode],
            context.CancellationToken);

        string translatedSectionName = sectionLookup
            .FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText
            ?? section.Name;

        List<TranslatedItemData> translatedItems = [];

        foreach (ItemSnapshot item in section.Items)
        {
            // Fetch item name translation
            string nameNormalized = TextNormalizer.Normalize(item.Name);
            long nameHash = TranslationHasher.ComputeHash(nameNormalized);

            IReadOnlyList<CacheLookupResult> nameLookup = await cache.LookupAsync(
                nameHash,
                nameNormalized,
                command.SourceLanguageCode,
                [langResult.TargetLanguageCode],
                context.CancellationToken);

            string translatedName = nameLookup
                .FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText
                ?? item.Name;

            // Fetch item description translation (if exists)
            string? translatedDescription = null;
            if (!string.IsNullOrEmpty(item.Description))
            {
                string descNormalized = TextNormalizer.Normalize(item.Description.AsSpan());
                long descHash = TranslationHasher.ComputeHash(descNormalized);

                IReadOnlyList<CacheLookupResult> descLookup = await cache.LookupAsync(
                    descHash,
                    descNormalized,
                    command.SourceLanguageCode,
                    [langResult.TargetLanguageCode],
                    context.CancellationToken);

                translatedDescription = descLookup
                    .FirstOrDefault(r => r.Translation is not null)?.Translation?.TranslatedText
                    ?? item.Description;
            }

            translatedItems.Add(new TranslatedItemData
            {
                ItemId = item.Id,
                TranslatedName = translatedName,
                TranslatedDescription = translatedDescription
            });
        }

        translatedSections.Add(new TranslatedSectionData
        {
            SectionId = section.Id,
            TranslatedName = translatedSectionName,
            Items = translatedItems
        });
    }

    translatedMenus.Add(new TranslatedMenuLanguage
    {
        LanguageCode = langResult.TargetLanguageCode,
        Sections = translatedSections
    });
}

await publishEndpoint.Publish(new MenuTranslationCompletedEvent
{
    MenuId = command.MenuId,
    VenueId = command.VenueId,
    SourceLanguageCode = command.SourceLanguageCode,
    TotalLanguages = result.TotalLanguages,
    CompletedLanguages = result.CompletedLanguages,
    FailedLanguages = result.FailedLanguages,
    FailedLanguageCodes = result.Results
        .Where(r => !r.IsSuccess)
        .Select(r => r.TargetLanguageCode)
        .ToList(),
    TranslatedMenus = translatedMenus,
    CompletedAt = DateTimeOffset.UtcNow
}, context.CancellationToken);
```

### Performance Note

This event-building code makes cache lookups for every item × every language.
But ALL of these are L1 Garnet hits — the pipeline JUST stored them.
Each lookup is < 1ms. For 50 items × 75 languages = 3,750 lookups,
pipelined in batches, this completes in well under a second.

For even better performance, the pipeline can accumulate translations in memory
during execution and pass them directly to the event builder, bypassing the
cache round-trip entirely. This is an optimization — the cache path works correctly first.

---

## Step 4: Edge API Consumer

**File: in your edge API project, e.g., `Consumers/MenuTranslationCompletedConsumer.cs`**

```csharp
using MassTransit;
// ADAPT: import your existing KV client, menu data access, JSON builder

public sealed class MenuTranslationCompletedConsumer(
    IMenuDataSource menuDataSource,        // ADAPT: your existing menu data access
    IKvDocumentBuilder kvDocumentBuilder,   // ADAPT: your existing KV JSON builder
    ICloudflareKvClient kvClient,          // ADAPT: your existing Cloudflare client
    ILogger<MenuTranslationCompletedConsumer> logger
    ) : IConsumer<MenuTranslationCompletedEvent>
{
    public async Task Consume(ConsumeContext<MenuTranslationCompletedEvent> context)
    {
        MenuTranslationCompletedEvent message = context.Message;

        logger.LogInformation(
            "Building KV documents for menu {MenuId}: {Count} languages",
            message.MenuId, message.TranslatedMenus.Count);

        // 1. Fetch the full menu structure with all non-translatable fields.
        //    This is the SAME data source your edge API already uses
        //    when building the default-language KV document.
        //
        //    ADAPT: Replace this with the actual call to your menu data source.
        //    It might be a DB query, an API call, or whatever pattern already exists.

        FullMenuData menuData = await menuDataSource.GetMenuAsync(
            message.MenuId, context.CancellationToken);

        if (menuData is null)
        {
            logger.LogError("Menu {MenuId} not found — cannot build KV documents", message.MenuId);
            return;
        }

        // 2. Build per-language KV documents.
        List<KvEntry> kvEntries = [];

        foreach (TranslatedMenuLanguage translatedMenu in message.TranslatedMenus)
        {
            // Build a lookup: ItemId → translated text
            Dictionary<Guid, TranslatedItemData> itemTranslations =
                translatedMenu.Sections
                    .SelectMany(s => s.Items)
                    .ToDictionary(i => i.ItemId);

            Dictionary<Guid, string> sectionTranslations =
                translatedMenu.Sections
                    .ToDictionary(s => s.SectionId, s => s.TranslatedName);

            // ADAPT: Use your existing KV document builder.
            // The idea: take the same menuData you'd use for the default language,
            // but override section names and item names/descriptions with translations.
            //
            // If your builder doesn't support text overrides, you may need to
            // create translated copies of the menu entities, or add an overload
            // that accepts a translation dictionary.

            // ──── OPTION A: Your builder accepts translation overrides ────
            //
            // string kvDocument = kvDocumentBuilder.Build(
            //     menuData,
            //     sectionNameOverrides: sectionTranslations,
            //     itemNameOverrides: itemTranslations.ToDictionary(
            //         kvp => kvp.Key, kvp => kvp.Value.TranslatedName),
            //     itemDescriptionOverrides: itemTranslations
            //         .Where(kvp => kvp.Value.TranslatedDescription is not null)
            //         .ToDictionary(
            //             kvp => kvp.Key, kvp => kvp.Value.TranslatedDescription!));

            // ──── OPTION B: Build the JSON document directly ────
            //
            // This is the more likely path if your current builder is rigid.
            // Build the same JSON structure as the default-language document,
            // replacing translatable text fields.

            object kvDocument = BuildTranslatedDocument(
                menuData,
                sectionTranslations,
                itemTranslations,
                translatedMenu.LanguageCode);

            string kvJson = JsonSerializer.Serialize(kvDocument);

            // ADAPT: Key format. Take your existing key format and append the language code.
            //
            // If current key is:     menu:{menuId}
            // Translated key is:     menu:{menuId}:{languageCode}
            //
            // If current key is:     venue:{venueId}:menu:{menuId}
            // Translated key is:     venue:{venueId}:menu:{menuId}:{languageCode}

            string kvKey = BuildTranslatedKvKey(message.MenuId, translatedMenu.LanguageCode);

            kvEntries.Add(new KvEntry(kvKey, kvJson));
        }

        // 3. Bulk write to Cloudflare KV.
        //    ADAPT: Use your existing Cloudflare KV write method.

        if (kvEntries.Count > 0)
        {
            await kvClient.BulkWriteAsync(kvEntries, context.CancellationToken);

            logger.LogInformation(
                "Pushed {Count} translated menu documents to KV for menu {MenuId}",
                kvEntries.Count, message.MenuId);
        }

        // 4. (Optional) If failed languages exist, log them for monitoring.
        if (message.FailedLanguages > 0)
        {
            logger.LogWarning(
                "Menu {MenuId} has {Count} failed language translations: {Languages}",
                message.MenuId,
                message.FailedLanguages,
                string.Join(", ", message.FailedLanguageCodes));
        }
    }

    private static object BuildTranslatedDocument(
        FullMenuData menuData,
        Dictionary<Guid, string> sectionTranslations,
        Dictionary<Guid, TranslatedItemData> itemTranslations,
        string languageCode)
    {
        // ADAPT: This must produce the EXACT same JSON structure your Cloudflare Worker
        // already reads. The only difference is that text fields are translated.
        //
        // Walk the menu structure. For each section:
        //   - Use sectionTranslations[section.Id] as the name (fall back to original if missing)
        //   - For each item in the section:
        //     - Use itemTranslations[item.Id].TranslatedName (fall back to original)
        //     - Use itemTranslations[item.Id].TranslatedDescription (fall back to original)
        //     - Keep price, imageUrl, availability, allergens, etc. UNTOUCHED
        //
        // EXAMPLE (adapt to your actual schema):

        return new
        {
            menu_id = menuData.MenuId,
            venue_id = menuData.VenueId,
            language = languageCode,
            compiled_at = DateTimeOffset.UtcNow,
            sections = menuData.Sections.Select(section => new
            {
                id = section.Id,
                name = sectionTranslations.TryGetValue(section.Id, out string? translatedSectionName)
                    ? translatedSectionName
                    : section.Name,
                sort_order = section.SortOrder,
                items = section.Items.Select(item => new
                {
                    id = item.Id,
                    name = itemTranslations.TryGetValue(item.Id, out TranslatedItemData? translatedItem)
                        ? translatedItem.TranslatedName
                        : item.Name,
                    description = translatedItem?.TranslatedDescription ?? item.Description,
                    price = item.Price,                    // NOT translated
                    image_url = item.ImageUrl,             // NOT translated
                    is_available = item.IsAvailable,       // NOT translated
                    sort_order = item.SortOrder,           // NOT translated
                    // ... any other fields your KV document has
                }).ToArray()
            }).ToArray()
        };
    }

    private static string BuildTranslatedKvKey(Guid menuId, string languageCode)
    {
        // ADAPT: Match your existing key format and append language code.
        return $"menu:{menuId}:{languageCode}";
    }
}
```

---

## Step 5: Register the Consumer in the Edge API

In the edge API project's MassTransit configuration, register the new consumer:

```csharp
cfg.AddConsumer<MenuTranslationCompletedConsumer>();
```

Match the existing consumer registration pattern in that project.

---

## Step 6: Cloudflare Worker Language Resolution

Your Cloudflare Worker currently fetches `menu:{menuId}` and serves it.
It needs to become language-aware.

### Worker Changes (TypeScript/JavaScript):

```typescript
// ADAPT: This is pseudocode. Match your existing Worker structure.

async function handleRequest(request: Request, env: Env): Promise<Response> {
  const menuId = extractMenuId(request);

  // Detect preferred language from:
  // 1. Query parameter: ?lang=de
  // 2. Accept-Language header
  // 3. Default to source language
  const preferredLanguage = detectLanguage(request);

  // Try language-specific key first
  let menuJson = await env.KV.get(`menu:${menuId}:${preferredLanguage}`);

  // Fall back to default language if translation doesn't exist
  if (!menuJson) {
    menuJson = await env.KV.get(`menu:${menuId}`);
  }

  if (!menuJson) {
    return new Response('Menu not found', { status: 404 });
  }

  return new Response(menuJson, {
    headers: {
      'Content-Type': 'application/json',
      'Content-Language': preferredLanguage,
      'Cache-Control': 'public, max-age=60'
    }
  });
}

function detectLanguage(request: Request): string {
  // 1. Explicit query parameter (highest priority)
  const url = new URL(request.url);
  const langParam = url.searchParams.get('lang');
  if (langParam) return langParam;

  // 2. Accept-Language header
  const acceptLanguage = request.headers.get('Accept-Language');
  if (acceptLanguage) {
    // Parse and match against available translations
    // e.g., "de-DE,de;q=0.9,en;q=0.8" → "de"
    const preferred = parseAcceptLanguage(acceptLanguage);
    if (preferred) return preferred;
  }

  // 3. Default
  return 'en';
}
```

---

## Step 7: Default Language KV Document

Your existing edge API already pushes the default-language menu to KV.
That flow continues unchanged. The translation consumer adds language-specific
keys alongside the default one.

After both run, KV contains:
```
menu:{menuId}           → default language JSON (existing flow, unchanged)
menu:{menuId}:de        → German translation (new, from translation consumer)
menu:{menuId}:fr        → French translation (new)
menu:{menuId}:ja        → Japanese translation (new)
menu:{menuId}:tr        → Turkish translation (new)
... (up to 75 languages)
```

The Worker tries `menu:{menuId}:{lang}` first. If missing, falls back to `menu:{menuId}`.
This means:
- Translations that haven't completed yet → user sees default language (graceful degradation)
- Failed languages → user sees default language
- All 75 succeed → user sees their preferred language

---

## Verification

1. Trigger a menu publish for a venue with some items.
2. Wait for Neaslator to complete the translation pipeline.
3. Verify `MenuTranslationCompletedEvent` arrives at the edge API consumer.
4. Verify KV contains language-specific keys.
5. Verify the Cloudflare Worker serves translated content when `?lang=de` is appended.
6. Verify the Worker falls back to default language for an untranslated language.
7. Verify non-translatable fields (price, images) are identical across all language documents.

---

## Performance Considerations

- The consumer processes 75 KV documents sequentially but writes them in a single bulk API call.
  Total consumer time: menu fetch (~5ms) + document building (~1ms × 75) + bulk write (~200ms) = ~300ms.

- If the menu has many items (100+), the event payload grows. At 100 items × 75 languages × ~120 bytes per item,
  the payload is ~900 KB. RabbitMQ default max is 128 MB. Well within limits.

- For very large menus (500+ items), consider chunking: publish one event per language instead of one event
  with all languages. The consumer stays simple (one KV write per event), and RabbitMQ handles the fan-out.

---

## What If You Want More Consumers Later?

Because `MenuTranslationCompletedEvent` is a published event (not a command),
multiple consumers can subscribe independently:

- **Edge API consumer** → Cloudflare KV (your current use case)
- **Webhook consumer** → POST translations to third-party systems
- **Analytics consumer** → Track translation coverage metrics
- **PDF generator** → Build translated PDF menus
- **Database sync** → Write translations to a different database

Each consumer subscribes to the same event and does its own thing.
Neaslator publishes once. Consumers multiply freely.
