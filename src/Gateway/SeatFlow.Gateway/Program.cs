using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SeatFlow.Gateway;
using SeatFlow.ServiceDefaults;

if (await HealthProbe.RunIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("gateway");

builder.Services.AddOptions<DevIdentityOptions>().Bind(builder.Configuration.GetSection("DevIdentity"));
builder.Services.AddSingleton<GatewaySigningKey>();

// The gateway validates with its own public key; downstream services validate again via JWKS.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<GatewaySigningKey>((options, key) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = SeatFlowAuth.Issuer,
            ValidAudience = SeatFlowAuth.Audience,
            IssuerSigningKey = key.PublicKey,
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(SeatFlowAuth.AdminPolicy, policy => policy.RequireRole(SeatFlowAuth.AdminRole));

// Per-user token bucket on the booking API: a client hammering "hold" cannot starve everybody else.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("per-user", context => RateLimitPartition.GetTokenBucketLimiter(
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 10,
            ReplenishmentPeriod = TimeSpan.FromSeconds(5),
            QueueLimit = 0,
        }));
});

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapDefaultEndpoints();
app.MapDevIdentity();
app.MapReverseProxy();

await app.RunAsync();
return 0;
