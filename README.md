# event-driven-finance-lab-dotnet

Public lab that demonstrates an event-driven architecture in .NET using Kafka (domain events) and RabbitMQ (command queue), with production-style patterns: outbox, idempotent consumers, retry/backoff, DLQ, and end-to-end tracing.

## Goals
- Show `Order -> Risk -> Limit -> Notification` flow in a finance-like domain.
- Demonstrate Kafka and RabbitMQ in their best-fit roles.
- Provide reproducible local setup with `docker compose up -d`.
- Offer interview-ready scenarios (happy path, duplicate handling, DLQ, replay).

## Tech Stack
- .NET 8
- PostgreSQL + Dapper
- Kafka (KRaft) via `Confluent.Kafka`
- RabbitMQ via `RabbitMQ.Client`
- OpenTelemetry + Jaeger + Prometheus
- Serilog structured logging

## Architecture
```mermaid
flowchart LR
  A[Order.Api\nPOST /orders] -->|DB tx: order + outbox| P[(Postgres)]
  O[Outbox.Publisher] -->|poll outbox| P
  O -->|orders.created| K[(Kafka)]
  R[RiskEngine.Worker] -->|consume orders.created| K
  R -->|limit.reserve command| Q[(RabbitMQ)]
  L[LimitService.Worker] -->|consume limit.reserve| Q
  L -->|limits.reserved / limits.rejected| K
  N[Notification.Worker] -->|consume result events| K
  N -->|persist notification| P
  R -. failures .->|orders.dlq| K
  N -. failures .->|orders.dlq| K
  Q -. failed commands .-> DQ[limits.reserve.dlq]
```

## Repository Layout
```
/src
  /Order.Api
  /Outbox.Publisher
  /RiskEngine.Worker
  /LimitService.Worker
  /Notification.Worker
  /BuildingBlocks
/contracts
/docs
/infra
/scripts
```

## Quickstart
### Prerequisites
- Docker + Docker Compose

### Run all services
```bash
docker compose -f infra/docker-compose.yml up -d --build
```

### Check health
```bash
curl http://localhost:8080/health
curl http://localhost:8081/health
curl http://localhost:8082/health
curl http://localhost:8083/health
curl http://localhost:8084/health
```

### Open observability
- Jaeger: http://localhost:16686
- Prometheus: http://localhost:9090

## API
### Create order
```bash
curl -X POST http://localhost:8080/orders \
  -H 'Content-Type: application/json' \
  -H "x-correlation-id: $(uuidgen)" \
  -d '{
    "accountId": "11111111-1111-1111-1111-111111111111",
    "symbol": "PETR4",
    "side": "BUY",
    "quantity": 100,
    "price": 32.15
  }'
```

### Get order status
```bash
curl http://localhost:8080/orders/{orderId}
```

## Scenarios
### A) Happy path
```bash
./scripts/smoke.sh
```

### B) Duplicate handling
- Restart one consumer while messages are in-flight:
```bash
docker restart risk-engine
```
- Check logs for `duplicate ignored`.

### C) Force DLQ on Rabbit
- Submit an order with symbol `DLQ1` to force failure in `LimitService.Worker`.
- Confirm message in RabbitMQ DLQ `limits.reserve.dlq`.

### D) Replay DLQ
```bash
./scripts/replay-rabbit-dlq.sh
./scripts/replay-kafka-dlq.sh
```

## Required Message Headers
- `message_id`
- `correlation_id`
- `order_id`
- `causation_id` (optional)
- `event_type`
- `event_version`
- `occurred_at`

## Key Decisions
- Kafka carries immutable domain events and fan-out.
- RabbitMQ handles command/work-queue semantics.
- Outbox pattern avoids dual-write inconsistency from API to broker.
- Idempotency uses `integration.processed_messages` unique key per consumer.

## Metrics
All services expose:
- `/health`
- `/metrics`

Key counters:
- `processed_count`
- `failed_count`
- `dlq_count`

## Troubleshooting
- If services fail to connect early, inspect container logs:
```bash
docker compose -f infra/docker-compose.yml logs -f --tail=200
```
- Verify Kafka connectivity:
```bash
docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list
```
- Verify Rabbit queues:
```bash
docker exec rabbitmq rabbitmqctl list_queues
```

## CI
GitHub Actions pipeline runs:
- build
- unit/integration tests
- format check (`dotnet format --verify-no-changes`)
- Docker Compose smoke test

## Portuguese Docs
- `docs/pt-BR/architecture.md`
- `docs/pt-BR/scenarios.md`
