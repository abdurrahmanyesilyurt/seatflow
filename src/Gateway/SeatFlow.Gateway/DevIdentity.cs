using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SeatFlow.ServiceDefaults;

namespace SeatFlow.Gateway;

public sealed class DevIdentityOptions
{
    /// <summary>Set to false in a real deployment and point <c>Auth:MetadataAddress</c> at your identity provider.</summary>
    public bool Enabled { get; set; } = true;

    public List<string> Admins { get; set; } = [];

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Optional PEM file with an RSA private key; without it a key is generated at startup.</summary>
    public string? SigningKeyPath { get; set; }
}

/// <summary>
/// RS256 signing key. Only the gateway holds the private half; services download the public half from the JWKS
/// endpoint and can verify tokens but never mint them.
/// </summary>
public sealed class GatewaySigningKey : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public GatewaySigningKey(IOptions<DevIdentityOptions> options, ILogger<GatewaySigningKey> logger)
    {
        var path = options.Value.SigningKeyPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            logger.LogWarning("No signing key configured; generated an ephemeral RSA key. Tokens will not survive a gateway restart.");
        }

        var parameters = _rsa.ExportParameters(includePrivateParameters: false);
        var modulus = parameters.Modulus ?? throw new InvalidOperationException("RSA key has no modulus.");
        Modulus = Base64Url.EncodeToString(modulus);
        Exponent = Base64Url.EncodeToString(parameters.Exponent);
        KeyId = Base64Url.EncodeToString(SHA256.HashData(modulus)[..12]);

        SigningKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        PublicKey = new RsaSecurityKey(parameters) { KeyId = KeyId };
    }

    public string KeyId { get; }

    public string Modulus { get; }

    public string Exponent { get; }

    public RsaSecurityKey SigningKey { get; }

    public RsaSecurityKey PublicKey { get; }

    public void Dispose() => _rsa.Dispose();
}

public sealed record TokenRequest(string Email, string? Name);

/// <summary>
/// A deliberately small identity endpoint so the demo needs no external IdP. Anyone can get a customer token for any
/// e-mail; the admin role is only granted to configured addresses. Services only trust the JWKS, so swapping this for
/// Keycloak or Entra ID is a configuration change.
/// </summary>
public static class DevIdentityEndpoints
{
    public static IEndpointRouteBuilder MapDevIdentity(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", (HttpRequest request) =>
        {
            var origin = $"{request.Scheme}://{request.Host}";
            return Results.Json(new
            {
                issuer = SeatFlowAuth.Issuer,
                jwks_uri = $"{origin}/.well-known/jwks.json",
                token_endpoint = $"{origin}/auth/token",
                id_token_signing_alg_values_supported = new[] { SecurityAlgorithms.RsaSha256 },
            });
        });

        app.MapGet("/.well-known/jwks.json", (GatewaySigningKey key) => Results.Json(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = SecurityAlgorithms.RsaSha256, kid = key.KeyId, n = key.Modulus, e = key.Exponent },
            },
        }));

        app.MapPost("/auth/token", IssueToken);
        return app;
    }

    private static IResult IssueToken(TokenRequest request, GatewaySigningKey key, IOptions<DevIdentityOptions> options, TimeProvider time)
    {
        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length is < 3 or > 320 || !email.Contains('@'))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid e-mail address is required."] });
        }

        var subject = "usr_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..16];
        string[] roles = options.Value.Admins.Contains(email, StringComparer.OrdinalIgnoreCase)
            ? ["customer", SeatFlowAuth.AdminRole]
            : ["customer"];

        var now = time.GetUtcNow().UtcDateTime;
        var lifetime = options.Value.TokenLifetime;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = SeatFlowAuth.Issuer,
            Audience = SeatFlowAuth.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = email,
                ["name"] = string.IsNullOrWhiteSpace(request.Name) ? email.Split('@')[0] : request.Name.Trim(),
                ["role"] = roles,
            },
            SigningCredentials = new SigningCredentials(key.SigningKey, SecurityAlgorithms.RsaSha256),
        });

        return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = (int)lifetime.TotalSeconds, roles });
    }
}
