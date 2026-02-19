# Cenários

## 1) Fluxo de sucesso
1. Suba a stack:
```bash
docker compose -f infra/docker-compose.yml up -d --build
```
2. Execute o smoke test:
```bash
./scripts/smoke.sh
```
3. Verifique status final `LIMIT_RESERVED`.

## 2) Duplicidade e idempotência
1. Gere ordens continuamente.
2. Reinicie um consumidor durante processamento:
```bash
docker restart risk-engine
```
3. Confira logs com `duplicate ignored`.

## 3) Cenário de DLQ
1. Crie ordem com símbolo `DLQ1` para forçar falha no limit service.
2. Verifique mensagens na DLQ:
```bash
docker exec rabbitmq rabbitmqctl list_queues name messages | grep limits.reserve.dlq
```

## 4) Replay
### Replay Rabbit DLQ
```bash
./scripts/replay-rabbit-dlq.sh 50
```

### Replay Kafka DLQ
```bash
./scripts/replay-kafka-dlq.sh 50
```

## 5) Observabilidade
- Jaeger: `http://localhost:16686`
- Prometheus: `http://localhost:9090`
- Exemplo de métricas: `http://localhost:8082/metrics`
