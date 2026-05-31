using Shared;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability(serviceName: "demo-external");

// Fault state lives in a singleton so the admin endpoints can flip it at runtime
builder.Services.AddSingleton<FaultState>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new FaultState
    {
        LatencyMs = int.TryParse(config["Faults:LatencyMs"], out var l) ? l : 0,
        ErrorRate = double.TryParse(config["Faults:ErrorRate"], out var e) ? e : 0.0
    };
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "demo-external",
    endpoints = new[] { "POST /validate", "GET /admin/faults", "POST /admin/faults" }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The validate endpoint - injected faults are deliberately observable in traces
app.MapPost("/validate", async (ValidateRequest req, FaultState faults, ILogger<Program> log) =>
{
    log.LogInformation("Validating order for {Customer} amount={Amount}", req.Customer, req.Amount);

    if (faults.LatencyMs > 0)
    {
        // Tag the active span so this delay is visible in Tempo
        System.Diagnostics.Activity.Current?.SetTag("fault.injected_latency_ms", faults.LatencyMs);
        await Task.Delay(faults.LatencyMs);
    }

    if (faults.ErrorRate > 0 && Random.Shared.NextDouble() < faults.ErrorRate)
    {
        System.Diagnostics.Activity.Current?.SetTag("fault.injected_error", true);
        log.LogWarning("Injected error for {Customer}", req.Customer);
        return Results.Problem("Simulated downstream error", statusCode: 500);
    }

    // Business rule: reject amounts over 10_000
    if (req.Amount > 10_000m)
    {
        return Results.Ok(new { status = "amount_too_large" });
    }
    if (string.IsNullOrWhiteSpace(req.Customer))
    {
        return Results.Ok(new { status = "missing_customer" });
    }

    return Results.Ok(new { status = "ok" });
});

// Admin endpoints to flip faults on/off without restarting the container
app.MapGet("/admin/faults", (FaultState f) => Results.Ok(new { f.LatencyMs, f.ErrorRate }));

app.MapPost("/admin/faults", (FaultUpdate update, FaultState f, ILogger<Program> log) =>
{
    if (update.LatencyMs is not null) f.LatencyMs = update.LatencyMs.Value;
    if (update.ErrorRate is not null) f.ErrorRate = update.ErrorRate.Value;
    log.LogWarning("Fault configuration updated: latency_ms={Latency} error_rate={ErrorRate}", f.LatencyMs, f.ErrorRate);
    return Results.Ok(new { f.LatencyMs, f.ErrorRate });
});

app.Run();

record ValidateRequest(string Customer, decimal Amount);
record FaultUpdate(int? LatencyMs, double? ErrorRate);

class FaultState
{
    public int LatencyMs { get; set; }
    public double ErrorRate { get; set; }
}
