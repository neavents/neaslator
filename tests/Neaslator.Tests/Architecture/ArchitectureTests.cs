using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using Neaslator.Domain.Entities;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Neaslator.Tests.Architecture;

/// <summary>
/// Convention guardrails that keep the codebase honest as it grows: sealed types,
/// a pure Domain layer that never reaches into infrastructure, and consumers/providers
/// that stay sealed. These fail the build if someone breaks a project-wide rule.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(TranslationMemoryEntry).Assembly)
        .Build();

    [Fact]
    public void DomainEntities_AreSealed()
    {
        Classes().That().ResideInNamespace("Neaslator.Domain.Entities")
            .Should().BeSealed()
            .Check(Architecture);
    }

    [Fact]
    public void DomainLayer_DoesNotDependOnInfrastructure()
    {
        Types().That().ResideInNamespaceMatching("^Neaslator\\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^Neaslator\\.Infrastructure"))
            .Check(Architecture);
    }

    [Fact]
    public void DomainLayer_DoesNotDependOnPersistence()
    {
        Types().That().ResideInNamespaceMatching("^Neaslator\\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^Neaslator\\.Persistence"))
            .Check(Architecture);
    }

    [Fact]
    public void DomainLayer_DoesNotDependOnFeatures()
    {
        Types().That().ResideInNamespaceMatching("^Neaslator\\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^Neaslator\\.Features"))
            .Check(Architecture);
    }

    [Fact]
    public void DomainLayer_DoesNotDependOnEntityFrameworkOrMassTransit()
    {
        Types().That().ResideInNamespaceMatching("^Neaslator\\.Domain")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^Microsoft\\.EntityFrameworkCore"))
            .AndShould().NotDependOnAny(Types().That().ResideInNamespaceMatching("^MassTransit"))
            .Check(Architecture);
    }

    [Fact]
    public void TranslationProviders_AreSealed()
    {
        Classes().That().AreAssignableTo(typeof(Neaslator.Infrastructure.Providers.ITranslationProvider))
            .Should().BeSealed()
            .Check(Architecture);
    }

    [Fact]
    public void EntityFrameworkConfigurations_AreSealed()
    {
        Classes().That().ResideInNamespace("Neaslator.Persistence.Configurations")
            .Should().BeSealed()
            .Check(Architecture);
    }
}
