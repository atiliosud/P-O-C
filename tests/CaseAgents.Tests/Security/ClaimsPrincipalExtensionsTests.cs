using System.Security.Claims;
using CaseAgents.Core.Security;
using CaseAgents.Infra.Auth;

namespace CaseAgents.Tests.Security;

/// <summary>
/// The user id and roles come from the token. Dev tokens carry raw "oid"/"roles" claims; Entra tokens may arrive
/// with the long mapped claim types. Ownership is keyed on the id, so its source and priority are business rules.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private const string ObjectIdUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void Raw_oid_and_roles_claims_are_used()
    {
        var user = Principal(new Claim("oid", "u1"), new Claim("roles", AppRoles.Submit), new Claim("roles", AppRoles.Review)).ToUserContext();

        Assert.Equal("u1", user.UserId);
        Assert.Equal(new[] { AppRoles.Review, AppRoles.Submit }, user.Roles.Order());
    }

    [Fact]
    public void Mapped_Entra_claim_types_are_used()
    {
        var user = Principal(new Claim(ObjectIdUri, "u2"), new Claim(ClaimTypes.Role, AppRoles.Review)).ToUserContext();

        Assert.Equal("u2", user.UserId);
        Assert.True(user.IsReviewer);
    }

    [Fact]
    public void Oid_wins_over_sub()
    {
        var user = Principal(new Claim("sub", "pairwise-sub"), new Claim("oid", "object-id")).ToUserContext();

        Assert.Equal("object-id", user.UserId);
    }

    [Fact]
    public void Sub_is_the_fallback_when_there_is_no_oid()
    {
        Assert.Equal("s1", Principal(new Claim("sub", "s1")).ToUserContext().UserId);
        Assert.Equal("s2", Principal(new Claim(ClaimTypes.NameIdentifier, "s2")).ToUserContext().UserId);
    }

    [Fact]
    public void Case_id_claim_becomes_the_case_scope()
    {
        var scoped = Principal(new Claim("oid", "ai-backend"), new Claim("roles", AppRoles.Agent), new Claim(DevTokenIssuer.CaseScopeClaim, "case-1")).ToUserContext();
        var unscoped = Principal(new Claim("oid", "ai-backend"), new Claim("roles", AppRoles.Agent), new Claim(DevTokenIssuer.CaseScopeClaim, "")).ToUserContext();

        Assert.Equal("case-1", scoped.CaseScope);
        Assert.True(scoped.IsAgent);
        Assert.Null(unscoped.CaseScope);
    }

    [Fact]
    public void Issued_agent_token_carries_the_case_scope()
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("claims-tests-signing-key-0123456789abcdef"));
        var token = new DevTokenIssuer(key, new DevAuthOptions(), TimeProvider.System).Issue("ai-backend", [AppRoles.Agent], "case-1");

        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token.AccessToken);

        Assert.Equal("case-1", jwt.GetClaim(DevTokenIssuer.CaseScopeClaim).Value);
    }

    [Fact]
    public void No_id_claim_gives_an_empty_id_and_no_roles()
    {
        var user = Principal(new Claim("name", "someone")).ToUserContext();

        Assert.Equal("", user.UserId);
        Assert.Empty(user.Roles);
    }
}
