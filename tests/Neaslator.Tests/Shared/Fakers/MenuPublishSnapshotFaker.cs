using Bogus;
using Neaslator.Domain.Entities;

namespace Neaslator.Tests.Shared.Fakers;

public sealed class MenuPublishSnapshotFaker : Faker<MenuPublishSnapshot>
{
    public MenuPublishSnapshotFaker()
    {
        RuleFor(s => s.Id, f => f.Random.Long(1, long.MaxValue));
        RuleFor(s => s.MenuId, _ => Ulid.NewUlid());
        RuleFor(s => s.OwnerId, _ => Ulid.NewUlid());
        RuleFor(s => s.SnapshotJson, f => f.Lorem.Text());
        RuleFor(s => s.PublishedAt, f => f.Date.RecentOffset(7));
    }
}
