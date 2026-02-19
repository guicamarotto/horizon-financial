# Scenarios

## 1) Happy path
1. Start stack:
```bash
docker compose -f infra/docker-compose.yml up -d --build
```
2. Run smoke scenario:
```bash
./scripts/smoke.sh
```
3. Validate order reaches `LIMIT_RESERVED`.

## 2) Duplicate delivery and idempotency
1. Create orders continuously.
2. Restart a consumer in the middle:
```bash
docker restart risk-engine
```
3. Check logs for `duplicate ignored` and stable final state.

## 3) DLQ scenario
1. Create order using symbol `DLQ1` to force processing failure in limit service.
2. Check Rabbit DLQ queue contains messages:
```bash
docker exec rabbitmq rabbitmqctl list_queues name messages | grep limits.reserve.dlq
```

## 4) Replay
### Rabbit DLQ replay
```bash
./scripts/replay-rabbit-dlq.sh 50
```

### Kafka DLQ replay
```bash
./scripts/replay-kafka-dlq.sh 50
```

## 5) Observability
- Jaeger UI: `http://localhost:16686`
- Prometheus UI: `http://localhost:9090`
- Metrics endpoint example: `http://localhost:8082/metrics`
