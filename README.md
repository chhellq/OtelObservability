# Vendor-agnostic Observability with OpenTelemetry

A distributed .NET application demonstrating end-to-end observability (traces, metrics, and logs) using OpenTelemetry and a fully open-source backend stack. Built as the practical component of an examensarbete at Chas Academy.

## What this shows

Modern applications are distributed across many services. When something goes wrong, finding **where** it went wrong is hard without a unified view of how requests flow through the system. This project demonstrates:

1. **Vendor-agnostic instrumentation** — services emit OpenTelemetry data; no backend SDKs in the application code.
2. **A single telemetry pipeline** — the OpenTelemetry Collector receives everything and fans out to three different backends.
3. **Correlated signals** — a log line links to its trace, a trace links to its logs, and span metrics drive the dashboards.
4. **Fault injection** — knobs to inject latency and errors in real time, so you can watch the dashboards light up and trace the problem back to its source.

## Architecture

```
                   ┌─────────────────────────────────────┐
                   │            Grafana :3000            │
                   │  (dashboards + queries + Explore)   │
                   └──────────────┬──────────────────────┘
                                  │
            ┌────────────────────┬┴────────────────────┐
            │                    │                     │
       ┌────▼────┐          ┌────▼────┐           ┌────▼────┐
       │Prometheus│         │  Loki   │           │  Tempo  │
       │ (metrics)│         │ (logs)  │           │(traces) │
       └────▲────┘          └────▲────┘           └────▲────┘
            │                    │                     │
            └────────────────────┼─────────────────────┘
                                 │
                       ┌─────────┴──────────┐
                       │  OTel Collector    │  receives OTLP
                       │  (4317/4318)       │  fans out to backends
                       └─────────▲──────────┘
                                 │  OTLP
        ┌────────────────────────┼───────────────────────┐
        │                        │                       │
   ┌────┴─────┐            ┌─────┴────┐           ┌──────┴───────┐
   │   API    │── http ───►│ External │           │   Worker     │
   │  :8080   │            │ Service  │           │ (consumes    │
   └────┬─────┘            │  :8090   │           │  RabbitMQ)   │
        │                  └──────────┘           └──────┬───────┘
        │ enqueue                                         │
        ▼                                                 ▼
   ┌─────────┐                                       ┌──────────┐
   │RabbitMQ │◄──────────────────────────────────────┤  reads   │
   └─────────┘                                       └──────────┘
        │
        ▼
   ┌─────────┐
   │Postgres │  ◄── both API and Worker read/write
   └─────────┘
```

The application is **four .NET 8 services**:
- **Api** — minimal-API service that creates and reads orders.
- **Worker** — background consumer that processes queued orders.
- **ExternalService** — simulated downstream dependency with runtime fault-injection knobs.
- **Shared** — single OpenTelemetry bootstrap reused by all three.

The infrastructure is **Postgres + RabbitMQ**, and the **observability stack is OTel Collector + Prometheus + Loki + Tempo + Grafana**, all run via Docker Compose.

## Quick start

Prerequisites: Docker Desktop (or Docker Engine + Compose v2).

```bash
# Bring everything up - first build takes a few minutes
docker compose up -d --build

# Wait ~30s for all services to settle, then verify
curl http://localhost:8080/                # API
curl http://localhost:8090/                # External service
docker compose ps                          # All should be "running"
```

Open the consoles:

| Service        | URL                          | Login        |
|----------------|------------------------------|--------------|
| Grafana        | http://localhost:3000        | admin/admin (anonymous admin is also enabled) |
| Prometheus     | http://localhost:9090        | —            |
| RabbitMQ admin | http://localhost:15672       | guest/guest  |
| API            | http://localhost:8080        | —            |

## Generating traffic

```bash
# Fire some orders manually
curl -X POST http://localhost:8080/orders \
    -H 'Content-Type: application/json' \
    -d '{"customer":"alice","amount":42.50}'

# Or run continuous load (~3 req/s by default)
./scripts/load.sh

# Faster, for a fixed duration
./scripts/load.sh 10 120        # 10 req/s for 2 minutes
```

In Grafana → **Dashboards** → **Service overview (RED + logs)** you should see request rate, error rate, p50/p95 latency, and live logs from all services.

## Demonstrating fault detection

This is the part that answers your *syfte*: "can a vendor-agnostic solution actually help us find problems faster?"

**Scenario 1 — injected latency in the external service**

```bash
# Start load in one terminal
./scripts/load.sh 5

# In another terminal, inject 800ms of latency into the external service
./scripts/fault.sh latency 800
```

What you should see in Grafana:

- **Service overview** dashboard: `demo-api` p95 latency spikes; `demo-external` p95 spikes at the same time.
- **Explore → Tempo**: pick a slow trace; the waterfall view shows the `ValidateOrder.External` span is the long one, and inside `demo-external` the span carries a `fault.injected_latency_ms` tag — the cause is right there.
- **Explore → Loki**: query `{service_name="demo-external"} | json | line_format "{{.body}}"` to see what the service was logging during the spike.

Clear it again:

```bash
./scripts/fault.sh clear
```

**Scenario 2 — injected errors**

```bash
./scripts/fault.sh errors 0.3      # 30% of validation calls fail
```

- **Error rate** panel jumps to ~30% for `demo-api` and `demo-external`.
- **Tempo**: error traces show the failed span with status code ERROR; clicking through to logs lands you on the matching `Injected error for...` warning.
- The custom counter `demo_orders_failed_total{reason="validation_http_error"}` is visible in Prometheus.

## How telemetry flows

A single request to `POST /orders` produces telemetry through this path:

1. The .NET process generates spans for the incoming HTTP request, the outgoing HTTP call to `external-service`, the Postgres query (via `Npgsql.OpenTelemetry`), and the RabbitMQ publish.
2. Custom spans (`CreateOrder`, `ValidateOrder.External`, `EnqueueOrder`) and custom metrics (`demo.orders.created`, `demo.orders.value`, etc.) come from `DemoTelemetry.ActivitySource` / `Meter`.
3. The OTLP exporter ships everything to the **OpenTelemetry Collector** over gRPC on port 4317.
4. The collector's pipelines fan out:
   - Traces → Tempo (also generates RED span metrics → Prometheus).
   - Metrics → Prometheus (remote write).
   - Logs → Loki (OTLP HTTP).
5. Grafana queries all three and correlates them through datasource provisioning.

When the Worker picks up a queued order, it extracts the W3C trace-context from the message headers, so the worker's `ProcessOrder` span links back to the original API trace. In Tempo you can see a single trace that crosses both services and the message queue.

## Project layout

```
.
├── docker-compose.yml              ← brings everything up
├── otel-collector/config.yaml      ← the vendor-agnostic hub
├── prometheus/, loki/, tempo/      ← backend configs
├── grafana/
│   ├── provisioning/datasources/   ← Prometheus, Loki, Tempo wired with correlations
│   ├── provisioning/dashboards/
│   └── dashboards/service-overview.json
├── scripts/
│   ├── load.sh                     ← traffic generator
│   └── fault.sh                    ← fault-injection helper
└── src/
    ├── Shared/                     ← single OTel bootstrap, used by all services
    ├── Api/                        ← order intake
    ├── Worker/                     ← queue consumer
    └── ExternalService/            ← downstream dependency with fault knobs
```

## Where vendor-agnosticism actually lives

The selling point of OpenTelemetry is that the **application code doesn't know which backend you use**. To prove that to yourself:

- Look in `src/` — there's no reference to Prometheus, Loki, Tempo, Jaeger, Datadog, etc. anywhere. Only `OpenTelemetry.*` packages.
- The choice of where data goes lives entirely in `otel-collector/config.yaml`. Swap Tempo for Jaeger by changing the `exporters:` section. Add Datadog alongside the open-source stack by adding an exporter and listing it in the pipelines.
- The OTLP endpoint is set by an environment variable (`OTEL_EXPORTER_OTLP_ENDPOINT`). In production you'd point it at a managed collector or a hosted service. No rebuild needed.

## Stopping everything

```bash
docker compose down              # stop, keep data
docker compose down -v           # stop and wipe volumes
```

## Known limitations / things to discuss in the report

- **Single-binary Loki/Tempo**: fine for a demo, not for production. Real deployments use the microservices mode with object storage.
- **No sampling**: every span is exported. For high-traffic systems you'd add tail-based sampling in the collector to keep costs predictable.
- **No alerting**: Grafana can drive alerts off these dashboards; not wired up here to keep the demo focused.
- **Local-only**: the demo runs on one machine. The same OTel pipeline scales to a Kubernetes cluster with minimal config changes — that's part of the value of standardizing on the protocol.
