using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SmartCraft.Assignment.Api.Authorization;

namespace SmartCraft.Assignment.Api.Endpoints;

public sealed record DevTokenRequest(Guid WorkerId, string[]? Permissions);

/// <summary>
/// POC stand-in for an identity provider (docs/04-concurrency-idempotency-auth.md
/// "Authentication"): mints a signed JWT for any worker id/permission set the caller asks for,
/// no credentials required. Mapped only when <c>ASPNETCORE_ENVIRONMENT=Development</c> — there
/// is no equivalent in a non-Development environment, by design; see README.md "Known
/// limitations".
/// </summary>
public static class DevTokenEndpoints
{
    public static void MapDevTokenEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapPost("/dev/token", (DevTokenRequest request, IConfiguration config) =>
            {
                if (request.WorkerId == Guid.Empty)
                {
                    return Results.BadRequest("workerId is required.");
                }

                var key = config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
                var issuer = config["Jwt:Issuer"] ?? "SmartCraft.Assignment";
                var audience = config["Jwt:Audience"] ?? "SmartCraft.Assignment";

                var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, request.WorkerId.ToString()) };
                claims.AddRange((request.Permissions ?? []).Select(p => new Claim(Permissions.ClaimType, p)));

                var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
                var token = new JwtSecurityToken(issuer, audience, claims, expires: DateTime.UtcNow.AddHours(8), signingCredentials: credentials);

                return Results.Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(token) });
            })
            .AllowAnonymous()
            .WithSummary("Development-only stand-in for an identity provider. Not present outside Development.");
    }
}
