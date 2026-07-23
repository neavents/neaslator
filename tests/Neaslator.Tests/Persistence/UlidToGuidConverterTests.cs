using FluentAssertions;
using Neaslator.Persistence;

namespace Neaslator.Tests.Persistence;

/// <summary>
/// The EF value converter that stores every <see cref="Ulid"/> id as a Guid column. Round-trip
/// fidelity here is load-bearing for the whole persistence layer — a lossy conversion would
/// silently corrupt every menu/owner id.
/// </summary>
public sealed class UlidToGuidConverterTests
{
    private static Guid ToProvider(Ulid u) => (Guid)UlidToGuidConverter.Instance.ConvertToProvider(u)!;
    private static Ulid FromProvider(Guid g) => (Ulid)UlidToGuidConverter.Instance.ConvertFromProvider(g)!;

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        Ulid original = Ulid.NewUlid();
        FromProvider(ToProvider(original)).Should().Be(original);
    }

    [Fact]
    public void RoundTrip_ManyValues_AllPreserved()
    {
        for (int i = 0; i < 500; i++)
        {
            Ulid original = Ulid.NewUlid();
            FromProvider(ToProvider(original)).Should().Be(original);
        }
    }

    [Fact]
    public void Empty_RoundTripsToEmpty()
    {
        FromProvider(ToProvider(Ulid.Empty)).Should().Be(Ulid.Empty);
    }

    [Fact]
    public void DistinctUlids_MapToDistinctGuids()
    {
        Ulid a = Ulid.NewUlid();
        Ulid b = Ulid.NewUlid();
        a.Should().NotBe(b);
        ToProvider(a).Should().NotBe(ToProvider(b));
    }

    [Fact]
    public void ToProvider_MatchesUlidToGuid()
    {
        Ulid u = Ulid.NewUlid();
        ToProvider(u).Should().Be(u.ToGuid());
    }

    [Fact]
    public void FromProvider_MatchesUlidFromGuid()
    {
        Guid g = Guid.NewGuid();
        FromProvider(g).Should().Be(new Ulid(g));
    }

    [Fact]
    public void Instance_IsReusableSingleton()
    {
        UlidToGuidConverter.Instance.Should().BeSameAs(UlidToGuidConverter.Instance);
    }
}
