using FluentAssertions;
using Neaslator.Infrastructure.Providers;

namespace Neaslator.Tests.Providers;

public sealed class TranslationPromptBuilderTests
{
    [Fact]
    public void BuildSystemPrompt_ContainsVenueType()
    {
        var prompt = TranslationPromptBuilder.BuildSystemPrompt(
            "Restaurant", "Italian", "English", "Turkish");

        prompt.Should().Contain("Restaurant");
        prompt.Should().Contain("Italian");
        prompt.Should().Contain("English");
        prompt.Should().Contain("Turkish");
    }

    [Fact]
    public void BuildSystemPrompt_ContainsTranslationRules()
    {
        var prompt = TranslationPromptBuilder.BuildSystemPrompt(
            "Cafe", "French", "English", "German");

        prompt.Should().Contain("professional translator");
        prompt.Should().Contain("restaurant and hospitality menus");
        prompt.Should().Contain("Translate menu item names");
        prompt.Should().Contain("Preserve brand names");
    }

    [Fact]
    public void BuildSystemPrompt_ContainsJsonFormatInstructions()
    {
        var prompt = TranslationPromptBuilder.BuildSystemPrompt(
            "Bar", "Japanese", "English", "Japanese");

        prompt.Should().Contain("hash");
        prompt.Should().Contain("translated_name");
        prompt.Should().Contain("translated_description");
        prompt.Should().Contain("JSON");
    }

    [Fact]
    public void BuildSystemPrompt_NotEmpty_ForAllCuisineTypes()
    {
        var cuisines = new[] { "Italian", "Chinese", "Mexican", "Indian", "Turkish" };
        foreach (var cuisine in cuisines)
        {
            var prompt = TranslationPromptBuilder.BuildSystemPrompt(
                "Restaurant", cuisine, "English", "Spanish");
            prompt.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void BuildUserPayload_ContainsSectionName()
    {
        var items = new List<TranslationBatchItem>
        {
            new() { SourceHash = 1, Name = "Pizza", Description = "Cheese pizza" }
        };

        var payload = TranslationPromptBuilder.BuildUserPayload("Appetizers", items);

        payload.Should().Contain("Appetizers");
        payload.Should().Contain("Pizza");
    }

    [Fact]
    public void BuildUserPayload_SerializesToValidJson()
    {
        var items = new List<TranslationBatchItem>
        {
            new() { SourceHash = 123, Name = "Burger", Description = "Beef burger" },
            new() { SourceHash = 456, Name = "Fries", Description = null }
        };

        var payload = TranslationPromptBuilder.BuildUserPayload("Main Course", items);

        var doc = System.Text.Json.JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("section_name").GetString().Should().Be("Main Course");
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void BuildUserPayload_NullDescription_IsIncluded()
    {
        var items = new List<TranslationBatchItem>
        {
            new() { SourceHash = 789, Name = "Soda", Description = null }
        };

        var payload = TranslationPromptBuilder.BuildUserPayload("Drinks", items);

        payload.Should().Contain("Soda");
    }

    [Fact]
    public void BuildUserPayload_EmptyList_ProducesEmptyItemsArray()
    {
        var payload = TranslationPromptBuilder.BuildUserPayload("Empty", Array.Empty<TranslationBatchItem>());

        var doc = System.Text.Json.JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void BuildUserPayload_MultipleItems_PreservesAllHashesAndNames()
    {
        var items = new List<TranslationBatchItem>
        {
            new() { SourceHash = 1, Name = "Item1", Description = "Desc1" },
            new() { SourceHash = 2, Name = "Item2", Description = "Desc2" },
            new() { SourceHash = 3, Name = "Item3", Description = "Desc3" }
        };

        var payload = TranslationPromptBuilder.BuildUserPayload("Section", items);

        payload.Should().ContainAll(new[] { "Item1", "Item2", "Item3", "Desc1", "Desc2", "Desc3" });
    }
}
