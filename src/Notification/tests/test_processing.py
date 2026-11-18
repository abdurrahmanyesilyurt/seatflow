import json
from contextlib import asynccontextmanager
from uuid import UUID, uuid4

import pytest

from app.consumer import trace_carrier
from app.emails import Email, PoisonMessage, render
from app.processing import NotificationProcessor

BOOKING_ID = "24351c5e-30f9-577f-ac26-754231e3e4cf"

CONFIRMED = {
    "bookingId": BOOKING_ID,
    "userId": "usr_1",
    "userEmail": "alice@example.com",
    "eventId": str(uuid4()),
    "eventTitle": "Jazz Under the Stars",
    "startsAt": "2026-12-18T17:00:00+00:00",
    "seatIds": ["A-1-1", "A-1-2"],
    "total": 2400.0,
    "currency": "TRY",
}


class FakeClaim:
    def __init__(self, store: "FakeStore", message_id: UUID) -> None:
        self._store, self._id = store, message_id
        self.already_sent = message_id in store.sent

    async def mark_sent(self) -> None:
        self._store.sent.add(self._id)


class FakeStore:
    """Mirrors the Postgres store's contract: a failed send leaves the message unsent, so a retry sends it."""

    def __init__(self) -> None:
        self.sent: set[UUID] = set()

    @asynccontextmanager
    async def claim(self, message_id: UUID, message_type: str, email: Email):
        yield FakeClaim(self, message_id)


class FakeMailer:
    def __init__(self, failures: int = 0) -> None:
        self.outbox: list[Email] = []
        self._failures = failures

    async def send(self, email: Email) -> None:
        if self._failures:
            self._failures -= 1
            raise ConnectionError("SMTP down")
        self.outbox.append(email)


def body(payload: dict) -> bytes:
    return json.dumps(payload).encode()


def test_confirmation_email_lists_seats_total_and_reference():
    email = render("booking.confirmed", CONFIRMED)

    assert email.recipient == "alice@example.com"
    assert "Jazz Under the Stars" in email.subject
    assert "24351c5e" in email.subject
    assert "A-1-1, A-1-2" in email.body
    assert "2,400.00 TRY" in email.body
    assert BOOKING_ID in email.body


async def test_a_redelivered_message_sends_one_email():
    mailer = FakeMailer()
    processor = NotificationProcessor(FakeStore(), mailer)
    message_id = str(uuid4())

    first = await processor.process("booking.confirmed", message_id, body(CONFIRMED))
    second = await processor.process("booking.confirmed", message_id, body(CONFIRMED))

    assert (first, second) == ("sent", "duplicate")
    assert len(mailer.outbox) == 1


async def test_a_failed_send_is_retried_on_redelivery():
    mailer = FakeMailer(failures=1)
    processor = NotificationProcessor(FakeStore(), mailer)
    message_id = str(uuid4())

    with pytest.raises(ConnectionError):
        await processor.process("booking.confirmed", message_id, body(CONFIRMED))
    assert await processor.process("booking.confirmed", message_id, body(CONFIRMED)) == "sent"
    assert len(mailer.outbox) == 1


async def test_messages_this_service_does_not_handle_are_ignored():
    processor = NotificationProcessor(FakeStore(), FakeMailer())

    assert await processor.process("payment.succeeded", str(uuid4()), b"{}") == "ignored"


@pytest.mark.parametrize(
    ("message_id", "payload"),
    [
        (None, body(CONFIRMED)),  # no id: cannot be idempotent
        ("not-a-uuid", body(CONFIRMED)),
        (str(uuid4()), b"{not json"),
        (str(uuid4()), body({"bookingId": BOOKING_ID})),  # missing fields
    ],
)
async def test_malformed_messages_are_poison(message_id, payload):
    processor = NotificationProcessor(FakeStore(), FakeMailer())

    with pytest.raises(PoisonMessage):
        await processor.process("booking.confirmed", message_id, payload)


def test_trace_context_is_read_from_str_or_bytes_headers():
    traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"

    assert trace_carrier({"traceparent": traceparent.encode()}) == {"traceparent": traceparent}
    assert trace_carrier({"traceparent": traceparent, "other": 1}) == {"traceparent": traceparent}
    assert trace_carrier(None) == {}
