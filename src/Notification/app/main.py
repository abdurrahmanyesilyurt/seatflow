"""SeatFlow notification service: booking events in (RabbitMQ), e-mails out (SMTP), a small read API."""

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator, Awaitable, Callable
from contextlib import asynccontextmanager
from typing import Annotated, Any
from uuid import UUID

import aio_pika
import asyncpg
from fastapi import Depends, FastAPI, HTTPException, Request, Response, status
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
from opentelemetry.instrumentation.utils import suppress_instrumentation

from app.auth import current_user
from app.config import get_settings
from app.consumer import BookingEventConsumer
from app.processing import NotificationProcessor, SmtpMailer
from app.repository import PostgresNotificationStore
from app.telemetry import setup_telemetry

settings = get_settings()
setup_telemetry(settings.service_name)
log = logging.getLogger("app.main")


async def with_retry[T](what: str, start: Callable[[], Awaitable[T]]) -> T:
    """Keep trying until a dependency answers (a fresh cluster may still be pulling its images)."""
    attempt = 0
    while True:
        attempt += 1
        try:
            return await start()
        except (OSError, asyncpg.PostgresError, aio_pika.exceptions.AMQPConnectionError) as error:
            log.warning("%s not reachable yet (attempt %d): %s", what, attempt, error)
            await asyncio.sleep(min(2 * attempt, 10))


async def connect_dependencies(app: FastAPI) -> None:
    store = await with_retry("PostgreSQL", lambda: PostgresNotificationStore.connect(settings.database_url))
    app.state.store = store
    processor = NotificationProcessor(store, SmtpMailer(settings.smtp_host, settings.smtp_port, settings.mail_from))
    consumer = BookingEventConsumer(settings, processor)
    await with_retry("RabbitMQ", consumer.start)
    app.state.consumer = consumer


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    # Connect in the background, like the .NET services: the process is live at once (liveness probe passes) and
    # becomes ready once PostgreSQL and RabbitMQ answer, instead of being restarted while it waits for them.
    app.state.store, app.state.consumer = None, None
    startup = asyncio.create_task(connect_dependencies(app))
    try:
        yield
    finally:
        startup.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await startup
        if app.state.consumer is not None:
            await app.state.consumer.stop()
        if app.state.store is not None:
            await app.state.store.close()


app = FastAPI(title="SeatFlow Notification", version="1.0.0", lifespan=lifespan)
FastAPIInstrumentor.instrument_app(app, excluded_urls="health")


@app.get("/health/live")
async def live() -> dict[str, str]:
    return {"status": "Healthy"}


@app.get("/health/ready")
async def ready(request: Request, response: Response) -> dict[str, Any]:
    store: PostgresNotificationStore | None = request.app.state.store
    consumer: BookingEventConsumer | None = request.app.state.consumer
    checks: dict[str, str] = {}
    try:
        if store is None:
            raise ConnectionError("not connected yet")
        with suppress_instrumentation():  # the 5-second probe should not show up as traces
            await store.ping()
        checks["postgres"] = "Healthy"
    except Exception:
        checks["postgres"] = "Unhealthy"
    checks["rabbitmq"] = "Healthy" if consumer is not None and consumer.connected else "Unhealthy"
    healthy = all(v == "Healthy" for v in checks.values())
    if not healthy:
        response.status_code = status.HTTP_503_SERVICE_UNAVAILABLE
    return {"status": "Healthy" if healthy else "Unhealthy", "checks": checks}


@app.get("/api/notifications/{booking_id}")
async def notifications_for_booking(
    booking_id: UUID,
    request: Request,
    user: Annotated[dict[str, Any], Depends(current_user)],
) -> list[dict[str, Any]]:
    """The e-mails sent about one of the caller's bookings."""
    store: PostgresNotificationStore | None = request.app.state.store
    if store is None:
        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "Starting up")
    return await store.for_booking(booking_id, user["email"])
