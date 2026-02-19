# Arquitetura

## Serviços
- `Order.Api`: recebe ordens via REST e persiste `orders` + `outbox_messages` na mesma transação.
- `Outbox.Publisher`: lê outbox e publica `orders.created` no Kafka.
- `RiskEngine.Worker`: valida regras de risco e envia comando `limit.reserve` para RabbitMQ.
- `LimitService.Worker`: consome comando, reserva limite e publica `limits.reserved` ou `limits.rejected`.
- `Notification.Worker`: consome eventos finais e persiste notificações.

## Topologia de mensageria
### Tópicos Kafka
- `orders.created`
- `limits.reserved`
- `limits.rejected`
- `orders.dlq`

### RabbitMQ
- Exchange `limits.commands` (direct)
- Queue `limits.reserve`
- DLX `limits.commands.dlx`
- DLQ `limits.reserve.dlq`

## Persistência
- `orders.orders`
- `integration.outbox_messages`
- `integration.processed_messages`
- `limits.account_limits`
- `notifications.notifications`

## Padrões de confiabilidade
- Outbox para consistência entre banco e broker.
- Consumidor idempotente via PK `(consumer_name, message_id)`.
- Retry com backoff exponencial e jitter.
- DLQ no Rabbit e canal de replay no Kafka.
