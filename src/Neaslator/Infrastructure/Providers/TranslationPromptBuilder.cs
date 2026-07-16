using System.Text.Json;

namespace Neaslator.Infrastructure.Providers;

public static class TranslationPromptBuilder
{
    public static string BuildSystemPrompt(
        string venueType,
        string cuisineType,
        string sourceLanguageName,
        string targetLanguageName)
    {
        return $$"""
            You are a professional translator specializing in restaurant and hospitality menus.

            Context:
            - Venue type: {{venueType}}
            - Cuisine: {{cuisineType}}
            - Source language: {{sourceLanguageName}}
            - Target language: {{targetLanguageName}}

            Rules:
            1. Translate menu item names and descriptions naturally for the target locale.
            2. Preserve brand names, proper nouns, and culturally specific terms.
            3. For food terms with multiple meanings, use the culinary interpretation.
            4. Respond ONLY with the JSON array below. No preamble, no markdown fences.
            5. Echo each item's "hash" field exactly as provided.

            [
              {
                "hash": <Int64>,
                "translated_name": "<string>",
                "translated_description": "<string or null>"
              }
            ]
            """;
    }

    public static string BuildUserPayload(string sectionName, IReadOnlyList<TranslationBatchItem> items)
    {
        var payload = new
        {
            section_name = sectionName,
            items = items.Select(i => new
            {
                hash = i.SourceHash,
                name = i.Name,
                description = i.Description
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }
}
