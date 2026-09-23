using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace SmartCraft.Assignment.Api.Authorization;

/// <summary>Maps the JWT "sub" claim to the caller's WorkerId. JwtBearer is configured with
/// <c>MapInboundClaims = false</c> (Program.cs) so the claim type stays exactly "sub" as
/// issued, instead of ASP.NET's default remap to the long ClaimTypes.NameIdentifier URI.</summary>
public static class ClaimsPrincipalExtensions
{
    public static Guid GetWorkerId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(sub, out var workerId)
            ? workerId
            : throw new InvalidOperationException("Authenticated principal has no valid worker id ('sub' claim).");
    }
}
