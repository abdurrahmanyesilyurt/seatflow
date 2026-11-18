using Microsoft.EntityFrameworkCore;
using SeatFlow.Bookings;
using SeatFlow.Bookings.Data;
using SeatFlow.Bookings.Features;
using SeatFlow.Bookings.Messaging;
using SeatFlow.Bookings.Seats;
using SeatFlow.Bookings.Workers;
using SeatFlow.Contracts;
using SeatFlow.Messaging;
using SeatFlow.ServiceDefaults;

if (await HealthProbe.RunIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("booking");
builder.AddSeatFlowAuthentication();
builder.Services.AddOpenApi();

builder.Services.AddOptions<BookingOptions>().Bind(builder.Configuration.GetSection(BookingOptions.SectionName));
builder.Services.AddDbContext<BookingDbContext>((sp, options) => options
    .UseSeatFlowPostgres(builder.Configuration)
    .UseSnakeCaseNamingConvention()
    .UseOutboxSignal(sp));
builder.Services.AddHealthChecks().AddDbContextCheck<BookingDbContext>("postgres", tags: ["ready"]);

builder.AddSeatLocks();
builder.Services.AddSingleton<BookingMetrics>();
builder.Services.AddScoped<HoldSeatsHandler>();
builder.Services.AddScoped<CheckoutHandler>();
builder.Services.AddHostedService<HoldExpiryWorker>();

builder.AddMessaging<BookingDbContext>()
    .Subscribe<EventPublished, EventPublishedHandler>()
    .Subscribe<PaymentSucceeded, PaymentSucceededHandler>()
    .Subscribe<PaymentFailed, PaymentFailedHandler>()
    .Subscribe<PaymentRefunded, PaymentRefundedHandler>();

var app = builder.Build();

if (await app.RunDatabaseCommandsAsync<BookingDbContext>(args))
{
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapDefaultEndpoints();
app.MapBookingEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
