using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using Shared;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability(serviceName: "demo-api");

// Connection-string-backed Postgres factory
var pgConnString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string missing");
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(pgConnString).Build());

// HttpClient pointed at the external service - automatically instrumented
builder.Services.AddHttpClient("external", c =>
{
    var baseUrl = builder.Configuration["ExternalService:BaseUrl"]
        ?? throw new InvalidOperationException("ExternalService:BaseUrl missing");
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
});

// RabbitMQ connection factory
builder.Services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
{
    HostName = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq",
    UserName = builder.Configuration["RabbitMq:User"] ?? "guest",
    Password = builder.Configuration["RabbitMq:Password"] ?? "guest",
    DispatchConsumersAsync = true
});

builder.Services.AddHostedService<DbInitializer>();

// Custom application metrics
var ordersCreated = DemoTelemetry.Meter.CreateCounter<long>(
    "demo.orders.created", unit: "{order}", description: "Number of orders created");
var ordersFailed = DemoTelemetry.Meter.CreateCounter<long>(
    "demo.orders.failed", unit: "{order}", description: "Number of orders that failed to create");
var orderValueHist = DemoTelemetry.Meter.CreateHistogram<double>(
    "demo.orders.value", unit: "USD", description: "Distribution of order values");

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "demo-api",
    endpoints = new[] { "POST /orders", "GET /orders/{id}", "GET /health" }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Create an order:
//   1. validate via external service
//   2. write to Postgres
//   3. enqueue a background job for the worker
// Every step shows up as a child span in the trace.
app.MapPost("/orders", async (
    OrderRequest req,
    NpgsqlDataSource db,
    IHttpClientFactory httpFactory,
    IConnectionFactory mqFactory,
    ILogger<Program> log) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("CreateOrder");
    activity?.SetTag("order.customer", req.Customer);
    activity?.SetTag("order.amount", req.Amount);

    log.LogInformation("Creating order for {Customer} amount={Amount}", req.Customer, req.Amount);

    // 1. External validation
    string validationStatus;
    try
    {
        using var validateSpan = DemoTelemetry.ActivitySource.StartActivity("ValidateOrder.External");
        var http = httpFactory.CreateClient("external");
        var resp = await http.PostAsJsonAsync("/validate", new { req.Customer, req.Amount });
        if (!resp.IsSuccessStatusCode)
        {
            ordersFailed.Add(1, new KeyValuePair<string, object?>("reason", "validation_http_error"));
            log.LogWarning("External validation returned {Status}", resp.StatusCode);
            activity?.SetStatus(ActivityStatusCode.Error, "validation http error");
            return Results.Problem($"Validation service returned {(int)resp.StatusCode}", statusCode: 502);
        }
        var payload = await resp.Content.ReadFromJsonAsync<ValidateResponse>();
        validationStatus = payload?.Status ?? "unknown";
        validateSpan?.SetTag("validation.status", validationStatus);
        if (validationStatus != "ok")
        {
            ordersFailed.Add(1, new KeyValuePair<string, object?>("reason", "validation_rejected"));
            log.LogWarning("Order rejected by validation: {Status}", validationStatus);
            activity?.SetStatus(ActivityStatusCode.Error, "validation rejected");
            return Results.BadRequest(new { error = "rejected", detail = validationStatus });
        }
    }
    catch (Exception ex)
    {
        ordersFailed.Add(1, new KeyValuePair<string, object?>("reason", "validation_exception"));
        log.LogError(ex, "Validation call failed");
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        return Results.Problem("Validation service unreachable", statusCode: 503);
    }

    // 2. Persist
    Guid id;
    await using (var conn = await db.OpenConnectionAsync())
    {
        id = await conn.ExecuteScalarAsync<Guid>(
            "INSERT INTO orders (customer, amount, status) VALUES (@Customer, @Amount, 'pending') RETURNING id",
            new { req.Customer, req.Amount });
    }
    activity?.SetTag("order.id", id);

    // 3. Enqueue for worker
    try
    {
        using var enqueueSpan = DemoTelemetry.ActivitySource.StartActivity("EnqueueOrder");
        using var mqConn = mqFactory.CreateConnection();
        using var channel = mqConn.CreateModel();
        channel.QueueDeclare("orders", durable: true, exclusive: false, autoDelete: false);

        var body = JsonSerializer.SerializeToUtf8Bytes(new { Id = id, req.Customer, req.Amount });
        var props = channel.CreateBasicProperties();
        props.Persistent = true;
        props.Headers = new Dictionary<string, object>();

        // Propagate trace context through the message headers so the
        // worker's span links into this same trace.
        var propagator = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;
        propagator.Inject(
            new OpenTelemetry.Context.Propagation.PropagationContext(Activity.Current?.Context ?? default, default),
            props,
            (carrier, key, value) => carrier.Headers[key] = value);

        channel.BasicPublish(exchange: "", routingKey: "orders", basicProperties: props, body: body);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to enqueue order {Id}", id);
        // Don't fail the request - the order is already saved
        activity?.AddEvent(new ActivityEvent("enqueue_failed"));
    }

    ordersCreated.Add(1, new KeyValuePair<string, object?>("customer", req.Customer));
    orderValueHist.Record((double)req.Amount);

    log.LogInformation("Order {OrderId} created", id);
    return Results.Created($"/orders/{id}", new { id, status = "pending" });
});

app.MapGet("/orders/{id:guid}", async (Guid id, NpgsqlDataSource db, ILogger<Program> log) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("GetOrder");
    activity?.SetTag("order.id", id);

    await using var conn = await db.OpenConnectionAsync();
    var order = await conn.QuerySingleOrDefaultAsync(
        "SELECT id, customer, amount, status FROM orders WHERE id = @id",
        new { id });

    if (order is null)
    {
        log.LogWarning("Order {OrderId} not found", id);
        return Results.NotFound();
    }
    return Results.Ok(order);
});

app.MapGet("/orders", async (NpgsqlDataSource db) =>
{
    await using var conn = await db.OpenConnectionAsync();
    var orders = await conn.QueryAsync(
        "SELECT id, customer, amount, status, created_at FROM orders ORDER BY created_at DESC LIMIT 50");
    return Results.Ok(orders);
});

app.Run();

record OrderRequest(string Customer, decimal Amount);
record ValidateResponse(string Status);

/// <summary>One-shot DB initializer - creates the orders table on startup.</summary>
public class DbInitializer(NpgsqlDataSource db, ILogger<DbInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Retry a few times in case Postgres isn't quite ready
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                await conn.ExecuteAsync("""
                    CREATE TABLE IF NOT EXISTS orders (
                        id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                        customer TEXT NOT NULL,
                        amount NUMERIC(12,2) NOT NULL,
                        status TEXT NOT NULL,
                        created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                        processed_at TIMESTAMPTZ
                    );
                    CREATE EXTENSION IF NOT EXISTS "pgcrypto";
                """);
                log.LogInformation("DB initialized");
                return;
            }
            catch (Exception ex) when (attempt < 10)
            {
                log.LogWarning(ex, "DB init attempt {Attempt} failed - retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
