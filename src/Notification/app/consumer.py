"""RabbitMQ consumer. Declares exactly the same topology as the .NET services (quorum queue + dead-letter queue)."""

import asyncio
import logging

import aio_pika
from aio_pika.abc import AbstractIncomingMessage, AbstractRobustConnection
from opentelemetry import context, trace
from opentelemetry.trace import SpanKind, Status, StatusCode
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator

from app.config import Settings
from app.emails import PoisonMessage
from app.processing import NotificationProcessor

log = logging.getLogger(__name__)
tracer = trace.get_tracer("seatflow.notification")
propagator = TraceContextTextMapPropagator()


def trace_carrier(headers: dict | None) -> dict[str, str]:
    """AMQP header values arrive as str or bytes depending on the publisher; the propagator wants str."""
    carrier: dict[str, str] = {}
    for key in ("traceparent", "tracestate"):
        value = (headers or {}).get(key)
        if isinstance(value, bytes):
            value = value.decode()
        if isinstance(value, str):
            carrier[key] = value
    return carrier


class BookingEventConsumer:
    def __init__(self, settings: Settings, processor: NotificationProcessor) -> None:
        self._settings = settings
        self._processor = processor
        self._connection: AbstractRobustConnection | None = None

    @property
    def connected(self) -> bool:
        return self._connection is not None and not self._connection.is_closed

    async def start(self) -> None:
        s = self._settings
        self._connection = await aio_pika.connect_robust(
            s.rabbitmq_url, client_properties={"connection_name": s.service_name}
        )
        channel = await self._connection.channel()
        await channel.set_qos(prefetch_count=s.prefetch)

        exchange = await channel.declare_exchange(s.exchange, aio_pika.ExchangeType.TOPIC, durable=True)
        dead_letters = await channel.declare_exchange(
            s.dead_letter_exchange, aio_pika.ExchangeType.DIRECT, durable=True
        )

        dlq = await channel.declare_queue(f"{s.queue}.dead-letter", durable=True, arguments={"x-queue-type": "quorum"})
        await dlq.bind(dead_letters, routing_key=s.queue)

        queue = await channel.declare_queue(
            s.queue,
            durable=True,
            arguments={
                "x-queue-type": "quorum",
                "x-delivery-limit": s.delivery_limit,
                "x-dead-letter-exchange": s.dead_letter_exchange,
                "x-dead-letter-routing-key": s.queue,
            },
        )
        for routing_key in self._processor.handles():
            await queue.bind(exchange, routing_key=routing_key)

        await queue.consume(self._on_message)
        log.info("Consuming %s for %s", s.queue, ", ".join(self._processor.handles()))

    async def _on_message(self, message: AbstractIncomingMessage) -> None:
        parent = propagator.extract(trace_carrier(message.headers))
        token = context.attach(parent)
        try:
            with tracer.start_as_current_span(
                f"process {message.type}",
                kind=SpanKind.CONSUMER,
                attributes={
                    "messaging.system": "rabbitmq",
                    "messaging.operation.type": "process",
                    "messaging.destination.name": self._settings.queue,
                    "messaging.rabbitmq.destination.routing_key": message.routing_key or "",
                    "messaging.message.id": message.message_id or "",
                },
            ) as span:
                await self._handle(message, span)
        finally:
            context.detach(token)

    async def _handle(self, message: AbstractIncomingMessage, span: trace.Span) -> None:
        try:
            outcome = await self._processor.process(message.type, message.message_id, message.body)
            span.set_attribute("seatflow.outcome", outcome)
            await message.ack()
        except PoisonMessage as error:
            span.set_status(Status(StatusCode.ERROR, str(error)))
            log.error("Dead-lettering %s %s: %s", message.type, message.message_id, error)
            await message.reject(requeue=False)
        except Exception as error:
            span.record_exception(error)
            span.set_status(Status(StatusCode.ERROR, str(error)))
            attempt = int((message.headers or {}).get("x-delivery-count", 0)) + 1
            log.warning("Handling %s %s failed (attempt %d); requeueing", message.type, message.message_id, attempt)
            await asyncio.sleep(min(0.2 * attempt * attempt, 5))
            await message.nack(requeue=True)

    async def stop(self) -> None:
        if self._connection is not None:
            await self._connection.close()
