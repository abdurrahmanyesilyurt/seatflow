using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Testing;

/// <summary>
/// Replaces the gateway's JWKS with a test key, so tests send real signed JWTs through the real validation pipeline.
/// </summary>
public static class TestAuth
{
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static void UseTestSigningKey(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = SeatFlowAuth.Issuer };
            configuration.SigningKeys.Add(Key);
            options.Configuration = configuration;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });

    public static string TokenFor(string userId, params string[] roles) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = SeatFlowAuth.Issuer,
            Audience = SeatFlowAuth.Audience,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId,
                ["email"] = $"{userId}@example.com",
                ["name"] = userId,
                ["role"] = roles.Length == 0 ? ["customer"] : roles,
            },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });

    public static HttpClient As(this HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(userId));
        return client;
    }
}
