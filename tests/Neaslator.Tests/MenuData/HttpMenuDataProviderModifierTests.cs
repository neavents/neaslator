using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neaslator.Infrastructure.Diff;
using Neaslator.Infrastructure.MenuData;

namespace Neaslator.Tests.MenuData;

public sealed class HttpMenuDataProviderModifierTests
{
    private static readonly Ulid MilkId = Ulid.Parse("01JA2B3C4D5E6F7G8H9JAKBMD1");
    private static readonly Ulid OatId = Ulid.Parse("01JA2B3C4D5E6F7G8H9JAKBMD2");
    private static readonly Ulid SoyId = Ulid.Parse("01JA2B3C4D5E6F7G8H9JAKBMD3");

    private static async Task<MenuSnapshot> ReadAsync(string modifierGroupsJson)
    {
        string body = """{"smartMenuDto":{"smartMenuId":"01JA2B3C4D5E6F7G8H9JAKBMD0","name":"Kahve","sections":[]"""
            + modifierGroupsJson + """},"detailsDto":{}}""";
        HttpClient http = new(new StaticHandler(body)) { BaseAddress = new Uri("http://menu.local") };
        HttpMenuDataProvider provider = new(http, NullLogger<HttpMenuDataProvider>.Instance);

        return (await provider.GetMenuSnapshotAsync(Ulid.NewUlid(), Ulid.NewUlid(), null, CancellationToken.None))!;
    }

    [Fact]
    public async Task Groups_and_options_are_read_in_the_shape_menu_service_writes_with_their_human_names()
    {
        MenuSnapshot snapshot = await ReadAsync($$$"""
            ,"modifierGroups":[{
              "id":"{{{MilkId}}}","name":"Süt","min":0,"max":1,"required":false,"displayOrder":0,
              "nameTranslations":{"en":"Dairy","de":"Milch"},"humanEditedLanguages":["en"],
              "options":[
                {"id":"{{{OatId}}}","name":"Yulaf","priceDelta":{"amount":10,"currency":{"code":"TRY"}},"availability":"Active",
                 "linkedItemId":null,"taxCategory":null,"displayOrder":0,"nameTranslations":{"en":"Oat"},"humanEditedLanguages":null},
                {"id":"{{{SoyId}}}","name":"Soya","priceDelta":{"amount":10,"currency":{"code":"TRY"}},"availability":"Unavailable",
                 "displayOrder":1,"nameTranslations":null,"humanEditedLanguages":["fr"]}
              ]}]
            """);

        ModifierGroupSnapshot milk = snapshot.ModifierGroups.Should().ContainSingle().Subject;
        milk.Id.Should().Be(MilkId);
        milk.Name.Should().Be("Süt");
        milk.HumanNames.Should().BeEquivalentTo(new Dictionary<string, string> { ["en"] = "Dairy" });
        milk.Options.Select(o => (o.Id, o.Name)).Should().Equal((OatId, "Yulaf"), (SoyId, "Soya"));
        milk.Options[0].HumanNames.Should().BeNull("a machine translation is not a person's name");
        milk.Options[1].HumanNames.Should().BeNull("a language marked edited with no text has nothing to carry");
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"modifierGroups\":null")]
    [InlineData(",\"modifierGroups\":[]")]
    public async Task A_menu_without_modifiers_reads_as_an_empty_list(string modifierGroupsJson)
    {
        (await ReadAsync(modifierGroupsJson)).ModifierGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_without_options_or_a_name_does_not_break_the_read()
    {
        MenuSnapshot snapshot = await ReadAsync($$$"""
            ,"modifierGroups":[
              {"id":"{{{MilkId}}}","name":"Süt","options":null},
              {"id":"{{{SoyId}}}","name":"  ","options":[]},
              {"id":"{{{OatId}}}","name":"Şurup","options":[{"id":"{{{SoyId}}}","name":""}]}]
            """);

        snapshot.ModifierGroups.Select(g => (g.Name, g.Options.Count)).Should().Equal(("Süt", 0), ("Şurup", 0));
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
