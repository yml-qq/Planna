using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Planna.Application.Identity;

namespace Planna.Api.Identity;

public sealed class AccessTokenIssuer(
    string issuer, string audience, string key, TimeProvider clock) : IAccessTokenIssuer
{
    public string Issue(Guid ownerId, Guid sessionId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([
                new Claim("sub", ownerId.ToString()),
                new Claim("sid", sessionId.ToString()),
                new Claim("jti", Guid.NewGuid().ToString())
            ]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        });
    }
}
