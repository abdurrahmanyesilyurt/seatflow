"""PostgreSQL persistence: the notifications table doubles as this service's inbox (idempotency by message id)."""

from collections.abc import AsyncIterator
from contextlib import AbstractAsyncContextManager, asynccontextmanager
from typing import Protocol
from uuid import UUID

import asyncpg

from app.emails import Email

SCHEMA = """
CREATE TABLE IF NOT EXISTS notifications (
    message_id   uuid PRIMARY KEY,
    message_type text        NOT NULL,
    booking_id   uuid        NOT NULL,
    recipient    text        NOT NULL,
    subject      text        NOT NULL,
    body         text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    sent_at      timestamptz
);
CREATE INDEX IF NOT EXISTS ix_notifications_booking ON notifications (booking_id, created_at);
"""


class Claim(Protocol):
    already_sent: bool

    async def mark_sent(self) -> None: ...


class NotificationStore(Protocol):
    def claim(self, message_id: UUID, message_type: str, email: Email) -> AbstractAsyncContextManager[Claim]: ...


class _PgClaim:
    def __init__(self, connection: asyncpg.Connection, message_id: UUID, already_sent: bool) -> None:
        self._connection = connection
        self._message_id = message_id
        self.already_sent = already_sent

    async def mark_sent(self) -> None:
        await self._connection.execute(
            "UPDATE notifications SET sent_at = now() WHERE message_id = $1", self._message_id
        )


class PostgresNotificationStore:
    def __init__(self, pool: asyncpg.Pool) -> None:
        self._pool = pool

    @classmethod
    async def connect(cls, dsn: str) -> "PostgresNotificationStore":
        pool = await asyncpg.create_pool(dsn, min_size=1, max_size=10)
        # Serialise schema creation across replicas starting at the same time.
        async with pool.acquire() as connection, connection.transaction():
            await connection.execute("SELECT pg_advisory_xact_lock(hashtext('notification-schema'))")
            await connection.execute(SCHEMA)
        return cls(pool)

    @asynccontextmanager
    async def claim(self, message_id: UUID, message_type: str, email: Email) -> AsyncIterator[Claim]:
        """
        Insert-if-new, then lock the row for the duration of the send. A redelivered message finds sent_at set and is
        skipped; two concurrent deliveries of the same message serialise on the row lock, so the e-mail goes out once.
        If sending fails the transaction rolls back and the redelivery tries again.
        """
        async with self._pool.acquire() as connection, connection.transaction():
            await connection.execute(
                """
                INSERT INTO notifications (message_id, message_type, booking_id, recipient, subject, body)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (message_id) DO NOTHING
                """,
                message_id,
                message_type,
                UUID(email.booking_id),
                email.recipient,
                email.subject,
                email.body,
            )
            sent_at = await connection.fetchval(
                "SELECT sent_at FROM notifications WHERE message_id = $1 FOR UPDATE", message_id
            )
            yield _PgClaim(connection, message_id, already_sent=sent_at is not None)

    async def for_booking(self, booking_id: UUID, recipient: str) -> list[dict]:
        rows = await self._pool.fetch(
            """
            SELECT message_id, message_type, subject, created_at, sent_at
            FROM notifications WHERE booking_id = $1 AND recipient = $2 ORDER BY created_at
            """,
            booking_id,
            recipient,
        )
        return [dict(row) for row in rows]

    async def ping(self) -> None:
        await self._pool.fetchval("SELECT 1")

    async def close(self) -> None:
        await self._pool.close()
