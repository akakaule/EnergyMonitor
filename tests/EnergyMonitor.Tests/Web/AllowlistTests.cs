using System.Security.Claims;
using EnergyMonitor.Core.Model;
using EnergyMonitor.Web.Auth;
using Microsoft.Extensions.Options;

namespace EnergyMonitor.Tests.Web;

public class AllowlistTests
{
    private const string Email = "owner@example.com";

    private sealed class Monitor(AuthOptions value) : IOptionsMonitor<AuthOptions>
    {
        public AuthOptions CurrentValue => value;

        public AuthOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AuthOptions, string?> listener) => null;
    }

    private static Allowlist Create(params (string Subject, string Role)[] users) =>
        new(new Monitor(new AuthOptions { Users = [.. users.Select(u => new AllowedUser { Subject = u.Subject, Role = u.Role })] }));

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void VerifiedEmail_MatchesTheAllowlist_IgnoringCase(string verified)
    {
        var user = Create((Email, UserRoles.Admin))
            .Resolve(Principal(("sub", "1234567890"), ("email", "Owner@Example.com"), ("email_verified", verified)));

        Assert.Equal(UserRoles.Admin, user?.Role);
    }

    [Fact]
    public void UnverifiedEmail_NeverMatches()
    {
        var allowlist = Create((Email, UserRoles.Admin));

        Assert.Null(allowlist.Resolve(Principal(("sub", "1"), ("email", Email), ("email_verified", "false"))));
        Assert.Null(allowlist.Resolve(Principal(("sub", "1"), ("email", Email)))); // no statement that it is verified
    }

    [Fact]
    public void AnotherVerifiedEmail_IsRefused()
    {
        var allowlist = Create((Email, UserRoles.Admin));

        Assert.Null(allowlist.Resolve(Principal(("sub", "2"), ("email", "someone.else@example.com"), ("email_verified", "true"))));
    }

    [Fact]
    public void AClaimThatMerelyContainsTheAddress_IsNotAMatch()
    {
        var allowlist = Create((Email, UserRoles.Admin));

        // The display name is chosen by the user; only the provider's identifier claims count.
        Assert.Null(allowlist.Resolve(Principal(("sub", "3"), ("name", Email), ("email", "x" + Email), ("email_verified", "true"))));
    }

    [Fact]
    public void SubjectAndObjectId_StillMatch()
    {
        var allowlist = Create(("1234567890", UserRoles.Viewer), ("aaaaaaaa-0000-0000-0000-000000000001", UserRoles.Admin));

        Assert.Equal(UserRoles.Viewer, allowlist.Resolve(Principal(("sub", "1234567890")))?.Role);
        Assert.Equal(UserRoles.Admin, allowlist.Resolve(Principal(("oid", "aaaaaaaa-0000-0000-0000-000000000001")))?.Role);
    }

    [Fact]
    public void UnauthenticatedPrincipal_IsRefused()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", Email), new Claim("email_verified", "true")]));

        Assert.Null(Create((Email, UserRoles.Admin)).Resolve(anonymous));
    }
}
