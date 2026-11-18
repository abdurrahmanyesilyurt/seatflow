using Microsoft.EntityFrameworkCore;
using SeatFlow.Catalog;
using SeatFlow.Catalog.Data;
using SeatFlow.Messaging;
using SeatFlow.ServiceDefaults;

if (await HealthProbe.RunIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("catalog");
builder.AddSeatFlowAuthentication();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<CatalogDbContext>((sp, options) => options
    .UseSeatFlowPostgres(builder.Configuration)
    .UseSnakeCaseNamingConvention()
    .UseOutboxSignal(sp));
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>("postgres", tags: ["ready"]);

builder.AddMessaging<CatalogDbContext>(); // publishes only
builder.AddCatalogCache();

var app = builder.Build();

if (await app.RunDatabaseCommandsAsync<CatalogDbContext>(args))
{
    return 0;
}

if (app.Configuration.GetValue("Seed:Enabled", true))
{
    await CatalogSeeder.SeedAsync(app.Services);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapCatalogEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
