using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Shared;

/// <summary>
/// One-stop OpenTelemetry registration for traces, metrics, and logs.
/// Every service in this demo calls this to get identical telemetry wiring.
/// </summary>
public static class TelemetryExtensions
{
    public static IHostApplicationBuilder AddObservability(
        this IHostApplicationBuilder builder,
        string serviceName,
        params string[] extraActivitySources)
    {
        // Resource attributes describe "what is producing this telemetry".
        // service.name + service.version + service.instance.id are the
        // OTel semantic-convention essentials.
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: "1.0.0",
                serviceInstanceId: Environment.MachineName)
            .AddEnvironmentVariableDetector();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: "1.0.0", serviceInstanceId: Environment.MachineName)
                .AddEnvironmentVariableDetector())

            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Drop health-check noise so traces stay useful
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation()
                    // Npgsql 8.x traces automatically via NpgsqlDataSource — no AddNpgsql() needed
                    .AddSource("Npgsql") // still subscribe to the source so spans are exported
                    .AddSource(DemoTelemetry.ActivitySourceName);

                foreach (var src in extraActivitySources)
                {
                    tracing.AddSource(src);
                }

                tracing.AddOtlpExporter();
            })

            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddMeter(DemoTelemetry.MeterName)
                    .AddOtlpExporter();
            });

        // Logs go through ILogger -> OpenTelemetry -> OTLP exporter.
        // ParseStateValues + IncludeFormattedMessage make structured logs queryable in Loki.
        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resourceBuilder);
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.AddOtlpExporter();
        });
        // Keep console output too, useful when developing
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        return builder;
    }
}

/// <summary>
/// Names for the custom ActivitySource and Meter used to emit
/// application-level (not framework-level) telemetry.
/// </summary>
public static class DemoTelemetry
{
    public const string ActivitySourceName = "Demo.App";
    public const string MeterName = "Demo.App";

    public static readonly System.Diagnostics.ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName, "1.0.0");
}
