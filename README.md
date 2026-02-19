# event-driven-finance-lab-dotnet

Lab público que demonstra arquitetura orientada a eventos em .NET usando Kafka (eventos de domínio) e RabbitMQ (fila de comandos), com padrões de produção: outbox, consumidores idempotentes, retry/backoff, DLQ e tracing de ponta a ponta.

## Objetivos
- Demonstrar o fluxo `Order -> Risk -> Limit -> Notification` em um domínio financeiro.
- Mostrar Kafka e RabbitMQ em seus papéis mais adequados.
- Fornecer setup local reproduzível com `docker compose up -d`.
- Entregar cenários prontos para entrevista (happy path, duplicidade, DLQ e replay).

## Stack Técnica
- .NET 8
- PostgreSQL + Dapper
- Kafka (KRaft) via `Confluent.Kafka`
- RabbitMQ via `RabbitMQ.Client`
- OpenTelemetry + Jaeger + Prometheus
- Logs estruturados com Serilog

## Arquitetura
```mermaid
flowchart LR
  A[Order.Api\nPOST /orders] -->|Transação DB: order + outbox| P[(Postgres)]
  O[Outbox.Publisher] -->|poll outbox| P
  O -->|orders.created| K[(Kafka)]
  R[RiskEngine.Worker] -->|consome orders.created| K
  R -->|comando limit.reserve| Q[(RabbitMQ)]
  L[LimitService.Worker] -->|consome limit.reserve| Q
  L -->|limits.reserved / limits.rejected| K
  N[Notification.Worker] -->|consome eventos de resultado| K
  N -->|persiste notificação| P
  R -.->|orders.dlq| K
  N -.->|orders.dlq| K
  Q -.->|limits.reserve.dlq| DQ[DLQ Rabbit]
```

## Estrutura do Repositório
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
### Pré-requisitos
- Docker + Docker Compose

### Subir todos os serviços
```bash
docker compose -f infra/docker-compose.yml up -d --build
```

### Verificar health
```bash
curl http://localhost:8080/health
curl http://localhost:8081/health
curl http://localhost:8082/health
curl http://localhost:8083/health
curl http://localhost:8084/health
```

### Abrir observabilidade
- Jaeger: http://localhost:16686
- Prometheus: http://localhost:9090
- Lab UI E2E: http://localhost:8080/lab

## Lab UI (E2E)
A UI em `Order.Api` mostra o fluxo completo da mensageria com polling de 1s:
- Timeline por ordem com etapas de API, Kafka, RabbitMQ e workers.
- Pipeline visual do caminho `Order.Api -> Outbox -> Kafka -> Risk -> Rabbit -> Limit -> Kafka -> Notification`.
- Painel DLQ com contadores e últimas falhas.
- Ação de replay para Rabbit DLQ e Kafka DLQ.
- Botões `Rodar 1x`, `Rodar 10x` e `Rodar Falhas` para iniciar demos sem terminal.

### Endpoints da UI
- `GET /lab`
- `POST /lab/api/smoke/run`
- `GET /lab/api/orders/recent?limit=20`
- `GET /lab/api/orders/{orderId}/timeline`
- `GET /lab/api/dlq/overview`
- `POST /lab/api/dlq/replay/rabbit?count=50`
- `POST /lab/api/dlq/replay/kafka?count=50`

## API
### Criar ordem
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

### Consultar status da ordem
```bash
curl http://localhost:8080/orders/{orderId}
```

## Cenários
### A) Happy path
```bash
./scripts/smoke.sh
```
Também é possível usar os botões da UI em `http://localhost:8080/lab`.

### B) Duplicidade e idempotência
- Reinicie um consumer enquanto há mensagens em processamento:
```bash
docker restart risk-engine
```
- Procure no log por `duplicate ignored`.

### C) Forçar DLQ no Rabbit
- Envie uma ordem com símbolo `DLQ1` para forçar falha no `LimitService.Worker`.
- Confirme a mensagem na DLQ `limits.reserve.dlq`.

### D) Replay de DLQ
```bash
./scripts/replay-rabbit-dlq.sh
./scripts/replay-kafka-dlq.sh
```

## Headers Obrigatórios de Mensagem
- `message_id`
- `correlation_id`
- `order_id`
- `causation_id` (opcional)
- `event_type`
- `event_version`
- `occurred_at`

## Decisões Técnicas
- Kafka carrega eventos imutáveis de domínio e fan-out.
- RabbitMQ trata semântica de comandos/work-queue.
- Outbox evita inconsistências de dual-write entre API e broker.
- Idempotência usa chave única em `integration.processed_messages`.

## Métricas
Todos os serviços expõem:
- `/health`
- `/metrics`

Contadores principais:
- `processed_count`
- `failed_count`
- `dlq_count`

## Troubleshooting
- Se serviços falharem no startup, veja logs:
```bash
docker compose -f infra/docker-compose.yml logs -f --tail=200
```
- Verificar conectividade Kafka:
```bash
docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list
```
- Verificar filas Rabbit:
```bash
docker exec rabbitmq rabbitmqctl list_queues
```

## CI
O pipeline do GitHub Actions executa:
- build
- testes unitários/integração
- checagem de formato (`dotnet format --verify-no-changes`)
- smoke test com Docker Compose

## Documentação em Português
- `docs/architecture.md`
- `docs/scenarios.md`
- `docs/pt-BR/architecture.md`
- `docs/pt-BR/scenarios.md`
