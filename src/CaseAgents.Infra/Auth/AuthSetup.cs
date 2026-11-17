using System.Security.Claims;
using System.Text;
using CaseAgents.Core.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CaseAgents.Infra.Auth;

public static class AuthModes
{
    /// <summary>Microsoft Entra ID tokens validated by Microsoft.Identity.Web (section "AzureAd").</summary>
    public const string Entra = "Entra";

    /// <summary>Locally signed tokens for development only. Refused outside the Development environment.</summary>
    public const string Dev = "Dev";
}

/// <summary>Configuration section "Auth".</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Mode { get; set; } = AuthModes.Entra;
    public DevAuthOptions Dev { get; set; } = new();
}

public sealed class DevAuthOptions
{
    public string Issuer { get; set; } = "caseagents-dev";
    public string Audience { get; set; } = "api://caseagents-dev";

    /// <summary>
    /// HMAC key (at least 32 characters) shared by the API and the MCP server so both accept the same dev tokens.
    /// Set by scripts/dev.* per session or via user-secrets. When empty, a random per-process key is used.
    /// </summary>
    public string? SigningKey { get; set; }

    public int TokenLifetimeMinutes { get; set; } = 480;
}

public static class AuthPolicies
{
    public const string Submit = "CanSubmit";
    public const string Review = "CanReview";
    public const string Agent = "IsAgent";
    public const string AnyRole = "AnyRole";

    /// <summary>Submitters (own cases) and reviewers (all cases); agents are refused.</summary>
    public const string SubmitOrReview = "CanSubmitOrReview";
}

public static class AuthSetup
{
    /// <summary>
    /// Registers authentication (Entra or Dev) and the role policies used by the API and the MCP server.
    /// Throws at startup when Auth:Mode=Dev outside the Development environment.
    /// </summary>
    public static WebApplicationBuilder AddCaseAgentsAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

        if (string.Equals(options.Mode, AuthModes.Dev, StringComparison.OrdinalIgnoreCase))
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException(
                    $"Auth:Mode=Dev is only allowed in the Development environment (current: '{builder.Environment.EnvironmentName}'). Use Auth:Mode=Entra.");

            var key = CreateDevKey(options.Dev.SigningKey);
            builder.Services.AddSingleton(new DevTokenIssuer(key, options.Dev, TimeProvider.System));
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.MapInboundClaims = false;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = options.Dev.Issuer,
                        ValidAudience = options.Dev.Audience,
                        IssuerSigningKey = key,
                        NameClaimType = "name",
                        RoleClaimType = "roles",
                    };
                });
        }
        else if (string.Equals(options.Mode, AuthModes.Entra, StringComparison.OrdinalIgnoreCase))
        {
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
        }
        else
        {
            throw new InvalidOperationException($"Unknown Auth:Mode '{options.Mode}'. Use '{AuthModes.Entra}' or '{AuthModes.Dev}'.");
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.Submit, p => RequireUserId(p).RequireRole(AppRoles.Submit))
            .AddPolicy(AuthPolicies.Review, p => RequireUserId(p).RequireRole(AppRoles.Review))
            .AddPolicy(AuthPolicies.Agent, p => RequireUserId(p).RequireRole(AppRoles.Agent))
            .AddPolicy(AuthPolicies.AnyRole, p => RequireUserId(p).RequireRole(AppRoles.Submit, AppRoles.Review, AppRoles.Agent))
            .AddPolicy(AuthPolicies.SubmitOrReview, p => RequireUserId(p).RequireRole(AppRoles.Submit, AppRoles.Review));

        return builder;
    }

    /// <summary>
    /// Ownership is keyed on the user id, so a token without one (no oid/sub) is refused:
    /// otherwise every such caller would share the empty id and could read each other's cases.
    /// </summary>
    private static AuthorizationPolicyBuilder RequireUserId(AuthorizationPolicyBuilder policy) =>
        policy.RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.ToUserContext().UserId.Length > 0);

    /// <summary>Maps POST /dev/token. Does nothing unless Dev auth was registered (Development only).</summary>
    public static IEndpointRouteBuilder MapDevTokenEndpoint(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<DevTokenIssuer>() is null)
            return endpoints;

        endpoints.MapPost("/dev/token", (DevTokenRequest request, DevTokenIssuer issuer) =>
            {
                if (string.IsNullOrWhiteSpace(request.UserId) || request.UserId.Length > 64)
                    return Results.BadRequest(new { error = "userId is required (max 64 characters)." });
                var roles = request.Roles ?? [];
                var unknown = roles.Where(r => !AppRoles.All.Contains(r)).ToList();
                if (unknown.Count > 0)
                    return Results.BadRequest(new { error = $"Unknown roles: {string.Join(", ", unknown)}" });

                var caseId = string.IsNullOrWhiteSpace(request.CaseId) ? null : request.CaseId.Trim();
                if (roles.Contains(AppRoles.Agent) && caseId is null)
                    return Results.BadRequest(new { error = $"A {AppRoles.Agent} token must be scoped to a case (caseId)." });

                return Results.Ok(issuer.Issue(request.UserId.Trim(), roles, caseId));
            })
            .AllowAnonymous();

        return endpoints;
    }

    private static SymmetricSecurityKey CreateDevKey(string? configured)
    {
        if (string.IsNullOrEmpty(configured))
            return new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        if (configured.Length < 32)
            throw new InvalidOperationException("Auth:Dev:SigningKey must be at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configured));
    }
}

/// <param name="CaseId">Required for Case.Agent tokens: the only case the token may touch (claim "case_id").</param>
public sealed record DevTokenRequest(string UserId, string[]? Roles, string? CaseId = null);

public sealed record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>Issues locally signed JWTs with the same claim shape as Entra access tokens (oid, name, roles), plus "case_id" for agent tokens.</summary>
public sealed class DevTokenIssuer(SymmetricSecurityKey key, DevAuthOptions options, TimeProvider time)
{
    /// <summary>Claim that scopes an agent token to one case.</summary>
    public const string CaseScopeClaim = "case_id";

    public DevTokenResponse Issue(string userId, IEnumerable<string> roles, string? caseId = null)
    {
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(options.TokenLifetimeMinutes);
        var claims = new Dictionary<string, object>
        {
            ["oid"] = userId,
            ["name"] = userId,
            ["roles"] = roles.Distinct().ToArray(),
        };
        if (!string.IsNullOrEmpty(caseId))
            claims[CaseScopeClaim] = caseId;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        return new DevTokenResponse(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

public static class ClaimsPrincipalExtensions
{
    private const string ObjectIdUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>User id = Entra object id (oid), falling back to sub; roles from "roles" or the mapped role claim.</summary>
    public static UserContext ToUserContext(this ClaimsPrincipal principal)
    {
        var id = principal.FindFirst("oid")?.Value
                 ?? principal.FindFirst(ObjectIdUri)?.Value
                 ?? principal.FindFirst("sub")?.Value
                 ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? "";
        var roles = principal.Claims
            .Where(c => c.Type is "roles" or ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
        var scope = principal.FindFirst(DevTokenIssuer.CaseScopeClaim)?.Value;
        return new UserContext(id, roles, string.IsNullOrEmpty(scope) ? null : scope);
    }
}
