using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

/// <summary>Demo-only bearer authentication. Production must use real OIDC/JWT validation.</summary>
public sealed class SampleBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "sample-bearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Transparent sample tokens are intentionally not a production authentication pattern.
        var parts = header["Bearer ".Length..]
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || !string.Equals(parts[0], "sample", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid sample token."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"sample-user-{parts[1]}"),
            new("tenant_id", parts[1]),
        };
        if (parts.Length == 3 && string.Equals(parts[2], "writer", StringComparison.Ordinal))
        {
            claims.Add(new Claim("scope", "workflow:start"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }
}
