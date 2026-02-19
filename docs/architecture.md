# Architecture

## Services
- `Order.Api`: receives REST order requests and writes `orders` + `outbox_messages` atomically.
- `Outbox.Publisher`: polls outbox and publishes `orders.created` to Kafka.
- `RiskEngine.Worker`: validates risk rules and sends `limit.reserve` command to RabbitMQ.
- `LimitService.Worker`: consumes command, reserves account limit, emits `limits.reserved` or `limits.rejected`.
- `Notification.Worker`: consumes result events and stores notification records.

## Messaging Topology
### Kafka topics
- `orders.created`
- `limits.reserved`
- `limits.rejected`
- `orders.dlq`

### RabbitMQ
- Exchange `limits.commands` (direct)
- Queue `limits.reserve`
- DLX `limits.commands.dlx`
- DLQ `limits.reserve.dlq`

## Persistence
- `orders.orders`
- `integration.outbox_messages`
- `integration.processed_messages`
- `limits.account_limits`
- `notifications.notifications`

## Reliability Patterns
- Outbox for publish consistency.
- Idempotent consumer via `(consumer_name, message_id)` PK.
- Retry with exponential backoff + jitter.
- DLQ on Rabbit and Kafka replay channel.
