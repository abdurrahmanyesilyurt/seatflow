"""Message handling independent of the broker, so it can be tested with fakes."""

import json
import logging
from email.message import EmailMessage
from typing import Literal, Protocol
from uuid import UUID

import aiosmtplib
from opentelemetry import metrics

from app.emails import RENDERERS, Email, PoisonMessage, render
from app.repository import NotificationStore

log = logging.getLogger(__name__)
_sent = metrics.get_meter("seatflow.notification").create_counter(
    "seatflow.notification.emails", description="E-mails sent, by message type."
)

Outcome = Literal["sent", "duplicate", "ignored"]


class Mailer(Protocol):
    async def send(self, email: Email) -> None: ...


class SmtpMailer:
    def __init__(self, host: str, port: int, sender: str) -> None:
        self._host, self._port, self._sender = host, port, sender

    async def send(self, email: Email) -> None:
        message = EmailMessage()
        message["From"] = self._sender
        message["To"] = email.recipient
        message["Subject"] = email.subject
        message.set_content(email.body)
        await aiosmtplib.send(message, hostname=self._host, port=self._port, timeout=10)


class NotificationProcessor:
    def __init__(self, store: NotificationStore, mailer: Mailer) -> None:
        self._store = store
        self._mailer = mailer

    @staticmethod
    def handles() -> list[str]:
        return list(RENDERERS)

    async def process(self, message_type: str | None, message_id: str | None, body: bytes) -> Outcome:
        if message_type not in RENDERERS:
            return "ignored"

        try:
            identifier = UUID(message_id or "")
        except ValueError as error:
            raise PoisonMessage("message has no valid message-id, so it cannot be processed idempotently") from error

        try:
            payload = json.loads(body)
        except ValueError as error:
            raise PoisonMessage(f"body is not JSON: {error}") from error

        email = render(message_type, payload)

        async with self._store.claim(identifier, message_type, email) as claim:
            if claim.already_sent:
                log.info("Skipping duplicate %s %s", message_type, identifier)
                return "duplicate"
            await self._mailer.send(email)
            await claim.mark_sent()

        _sent.add(1, {"type": message_type})
        log.info("Sent %s e-mail for booking %s to %s", message_type, email.booking_id, email.recipient)
        return "sent"
