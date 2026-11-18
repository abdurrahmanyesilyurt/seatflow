using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace SeatFlow.ServiceDefaults;

public static class SeatFlowAuth
{
    public const string Issuer = "seatflow-gateway";
    public const string Audience = "seatflow";
    public const string AdminPolicy = "admin";
    public const string AdminRole = "admin";

    /// <summary>
    /// Services validate tokens themselves (zero trust: the gateway is not the only line of defence). They only know
    /// the gateway's public keys, fetched from its JWKS endpoint; the private key never leaves the gateway.
    /// </summary>
    public static TBuilder AddSeatFlowAuthentication<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var section = builder.Configuration.GetSection("Auth");

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MetadataAddress = section["MetadataAddress"] ?? "http://localhost:8080/.well-known/openid-configuration";
                options.RequireHttpsMetadata = false; // in-cluster traffic; TLS terminates at the edge
                options.MapInboundClaims = false;
                options.RefreshInterval = TimeSpan.FromSeconds(10); // pick up a rotated gateway key quickly
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = section["Issuer"] ?? Issuer,
                    ValidAudience = section["Audience"] ?? Audience,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(AdminRole));

        return builder;
    }

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim.");

    public static string GetEmail(this ClaimsPrincipal user) =>
        user.FindFirstValue("email") ?? throw new InvalidOperationException("Authenticated user has no 'email' claim.");
}
