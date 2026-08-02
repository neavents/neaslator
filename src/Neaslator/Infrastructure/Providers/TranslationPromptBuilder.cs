using System.Text.Json;

namespace Neaslator.Infrastructure.Providers;

public static class TranslationPromptBuilder
{
    /// <summary>
    /// The instruction the model translates against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It asks for an object, not an array, and that is the whole point.</b> The request is sent
    /// with <c>response_format: json_object</c>, which obliges the model to return a JSON object.
    /// This prompt used to demand a JSON <i>array</i> at the same time. The two instructions
    /// contradict each other, so the model picked one per call — and when it obeyed the response
    /// format it had to invent an envelope to put the array in.
    /// </para>
    /// <para>
    /// Measured against the live API on 2026-08-02: 8% of single-item calls came back as
    /// <c>{"section_name": …, "items": [ … ]}</c> — our own request shape echoed back, with a
    /// perfectly good translation inside. The parser did not recognise that envelope and turned it
    /// into a single item with no hash, which then failed validation as "Unexpected hash 0". Every
    /// one of those translations was paid for and thrown away, and the router reported it as
    /// "All translation providers exhausted", which reads like a billing problem.
    /// </para>
    /// <para>
    /// Naming the envelope removes the contradiction and makes the common answer the one the parser
    /// has always handled best. The parser stays permissive anyway — see
    /// <c>DeepSeekProvider.ExtractItems</c> — because a model is not a schema.
    /// </para>
    /// </remarks>
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
            4. Respond ONLY with the JSON object below. No preamble, no markdown fences.
            5. Echo each item's "hash" field exactly as provided, as a JSON number.
            6. Return one entry per input item, in the same order.

            {
              "translations": [
                {
                  "hash": <Int64>,
                  "translated_name": "<string>",
                  "translated_description": "<string or null>"
                }
              ]
            }
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
