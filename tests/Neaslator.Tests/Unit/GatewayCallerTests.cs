using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Neaslator.Tests.Unit;

public sealed class GatewayCallerTests
{
    private static GatewayCaller From(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return GatewayCaller.From(context.Request);
    }

    [Fact]
    public void A_gateway_user_carries_their_tenant()
    {
        Ulid tenant = Ulid.NewUlid();

        GatewayCaller caller = From(("X-User-Id", Ulid.NewUlid().ToString()), ("X-Tenant-Id", tenant.ToString()));

        caller.IsServiceCall.Should().BeFalse();
        caller.IsPlatformAdmin.Should().BeFalse();
        caller.TenantId.Should().Be(tenant);
        caller.MaySee(tenant).Should().BeTrue();
        caller.MaySee(Ulid.NewUlid()).Should().BeFalse();
        caller.MaySee(null).Should().BeFalse();
    }

    [Fact]
    public void The_key_without_a_user_is_a_service_and_sees_every_tenant()
    {
        GatewayCaller caller = From();

        caller.IsServiceCall.Should().BeTrue();
        caller.MaySee(Ulid.NewUlid()).Should().BeTrue();
        caller.MaySee(null).Should().BeTrue();
    }

    [Theory]
    [InlineData("platform_admin", true)]
    [InlineData("PLATFORM_ADMIN", true)]
    [InlineData("admin", false)]
    [InlineData("owner", false)]
    public void Only_platform_admin_sees_every_tenant(string role, bool expected)
    {
        GatewayCaller caller = From(("X-User-Id", Ulid.NewUlid().ToString()), ("X-Account-Role", role));

        caller.SeesEveryTenant.Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-ulid")]
    [InlineData("")]
    public void A_malformed_user_id_is_still_a_user_and_a_malformed_tenant_is_none(string raw)
    {
        GatewayCaller caller = From(("X-User-Id", raw), ("X-Tenant-Id", raw));

        caller.IsServiceCall.Should().BeFalse();
        caller.TenantId.Should().BeNull();
        caller.SeesEveryTenant.Should().BeFalse();
    }
}
