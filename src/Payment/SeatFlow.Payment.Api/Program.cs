using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SeatFlow.Contracts;
using SeatFlow.Messaging;
using SeatFlow.Payments.Data;
using SeatFlow.Payments.Messaging;
using SeatFlow.Payments.Provider;
using SeatFlow.ServiceDefaults;

if (await HealthProbe.RunIfRequestedAsync(args) is { } probeExitCode)
{
    return probeExitCode;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("payment");
builder.AddSeatFlowAuthentication();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<PaymentDbContext>((sp, options) => options
    .UseSeatFlowPostgres(builder.Configuration)
    .UseSnakeCaseNamingConvention()
    .UseOutboxSignal(sp));
builder.Services.AddHealthChecks().AddDbContextCheck<PaymentDbContext>("postgres", tags: ["ready"]);

builder.Services.AddOptions<FakePaymentProviderOptions>().Bind(builder.Configuration.GetSection("FakeProvider"));
builder.Services.AddSingleton<IPaymentProvider, FakePaymentProvider>();

builder.AddMessaging<PaymentDbContext>()
    .Subscribe<ProcessPayment, ProcessPaymentHandler>()
    .Subscribe<RefundPayment, RefundPaymentHandler>();

var app = builder.Build();

if (await app.RunDatabaseCommandsAsync<PaymentDbContext>(args))
{
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapDefaultEndpoints();

app.MapGet("/api/payments/{bookingId:guid}", async (Guid bookingId, ClaimsPrincipal user, PaymentDbContext db, CancellationToken ct) =>
    {
        var userId = user.GetUserId();
        var payment = await db.Payments.AsNoTracking().SingleOrDefaultAsync(p => p.BookingId == bookingId && p.UserId == userId, ct);
        return payment is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                payment.Id,
                payment.BookingId,
                payment.Amount,
                payment.Currency,
                payment.Status,
                payment.FailureReason,
                payment.CreatedAt,
                payment.RefundedAt,
                payment.RefundReason,
            });
    })
    .RequireAuthorization()
    .WithTags("Payments");

await app.RunAsync();
return 0;

public partial class Program;
