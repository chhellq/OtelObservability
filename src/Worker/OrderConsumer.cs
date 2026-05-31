using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared;

namespace Worker;

public class OrderConsumer(
    IConnectionFactory mqFactory,
    NpgsqlDataSource db,
    ILogger<OrderConsumer> log) : BackgroundService
{
    private IConnection? _conn;
    private IModel? _channel;

    private static readonly Counter<long> _processed = DemoTelemetry.Meter.CreateCounter<long>(
        "demo.orders.processed", unit: "{order}", description: "Orders processed successfully");
    private static readonly Counter<long> _failed = DemoTelemetry.Meter.CreateCounter<long>(
        "demo.orders.process_failed", unit: "{order}", description: "Orders that failed processing");
    private static readonly Histogram<double> _processingDuration = DemoTelemetry.Meter.CreateHistogram<double>(
        "demo.orders.processing_duration", unit: "ms", description: "Time to process an order");

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry the connection until RabbitMQ is reachable
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                _conn = mqFactory.CreateConnection();
                _channel = _conn.CreateModel();
                break;
            }
            catch (Exception ex) when (attempt < 30)
            {
                log.LogWarning("RabbitMQ not ready yet (attempt {Attempt}): {Msg}", attempt, ex.Message);
                Thread.Sleep(2000);
            }
        }

        if (_channel is null)
        {
            log.LogError("Could not establish RabbitMQ channel");
            return Task.CompletedTask;
        }

        _channel.QueueDeclare("orders", durable: true, exclusive: false, autoDelete: false);
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 5, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageAsync;
        _channel.BasicConsume("orders", autoAck: false, consumer);

        log.LogInformation("Worker started, listening on 'orders' queue");
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        // Extract the trace context the API attached in headers
        var propagator = Propagators.DefaultTextMapPropagator;
        var parentContext = propagator.Extract(default, ea.BasicProperties, ExtractTraceContextFromHeaders);
        Baggage.Current = parentContext.Baggage;

        using var activity = DemoTelemetry.ActivitySource.StartActivity(
            "ProcessOrder",
            ActivityKind.Consumer,
            parentContext.ActivityContext);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", "orders");

        var sw = Stopwatch.StartNew();
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.ToArray());
            var msg = JsonSerializer.Deserialize<OrderMessage>(json);
            if (msg is null)
            {
                log.LogWarning("Could not deserialize message body");
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            activity?.SetTag("order.id", msg.Id);
            activity?.SetTag("order.customer", msg.Customer);

            log.LogInformation("Processing order {OrderId} for {Customer}", msg.Id, msg.Customer);

            // Simulate work
            await Task.Delay(Random.Shared.Next(50, 300));

            // Mark order as processed in DB
            await using var conn = await db.OpenConnectionAsync();
            var affected = await conn.ExecuteAsync(
                "UPDATE orders SET status = 'processed', processed_at = now() WHERE id = @Id",
                new { msg.Id });

            if (affected == 0)
            {
                log.LogWarning("Order {OrderId} not found in DB", msg.Id);
            }

            _processed.Add(1, new KeyValuePair<string, object?>("customer", msg.Customer));
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
            log.LogInformation("Processed order {OrderId}", msg.Id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to process order");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _failed.Add(1);
            // Don't requeue - in a real system you'd send to a DLQ
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
        finally
        {
            sw.Stop();
            _processingDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private static IEnumerable<string> ExtractTraceContextFromHeaders(IBasicProperties props, string key)
    {
        if (props.Headers != null && props.Headers.TryGetValue(key, out var value) && value is byte[] bytes)
        {
            return new[] { Encoding.UTF8.GetString(bytes) };
        }
        return Enumerable.Empty<string>();
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _conn?.Dispose();
        base.Dispose();
    }

    private record OrderMessage(Guid Id, string Customer, decimal Amount);
}
