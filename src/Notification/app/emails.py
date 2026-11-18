"""Turns booking events into e-mails. Pure functions: no I/O, trivially unit-testable."""

from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Any


class PoisonMessage(Exception):
    """The message can never be processed (malformed, missing fields); it goes to the dead-letter queue."""


@dataclass(frozen=True, slots=True)
class Email:
    booking_id: str
    recipient: str
    subject: str
    body: str


def _money(amount: Any, currency: str) -> str:
    return f"{Decimal(str(amount)):,.2f} {currency}"


def _when(value: str) -> str:
    return datetime.fromisoformat(value).strftime("%d %b %Y, %H:%M UTC")


def _reference(booking_id: str) -> str:
    return booking_id.split("-")[0]


def _confirmed(m: Mapping[str, Any]) -> Email:
    seats = ", ".join(m["seatIds"])
    return Email(
        booking_id=m["bookingId"],
        recipient=m["userEmail"],
        subject=f"Your tickets for {m['eventTitle']} (booking {_reference(m['bookingId'])})",
        body=(
            f"Your booking is confirmed.\n\n"
            f"Event:   {m['eventTitle']}\n"
            f"When:    {_when(m['startsAt'])}\n"
            f"Seats:   {seats}\n"
            f"Total:   {_money(m['total'], m['currency'])}\n"
            f"Booking: {m['bookingId']}\n"
        ),
    )


def _cancelled(m: Mapping[str, Any]) -> Email:
    return Email(
        booking_id=m["bookingId"],
        recipient=m["userEmail"],
        subject=f"Your booking for {m['eventTitle']} was cancelled (booking {_reference(m['bookingId'])})",
        body=(
            f"Your seats ({', '.join(m['seatIds'])}) have been released.\n"
            f"Reason:  {m['reason']}\n"
            f"Booking: {m['bookingId']}\n"
        ),
    )


def _expired(m: Mapping[str, Any]) -> Email:
    return Email(
        booking_id=m["bookingId"],
        recipient=m["userEmail"],
        subject=f"Your seat hold for {m['eventTitle']} expired (booking {_reference(m['bookingId'])})",
        body=(
            f"We held {', '.join(m['seatIds'])} for you, but checkout did not complete in time.\n"
            f"The seats are back on sale.\n"
            f"Booking: {m['bookingId']}\n"
        ),
    )


def _refunded(m: Mapping[str, Any]) -> Email:
    return Email(
        booking_id=m["bookingId"],
        recipient=m["userEmail"],
        subject=f"Refund issued for {m['eventTitle']} (booking {_reference(m['bookingId'])})",
        body=(
            f"We refunded {_money(m['amount'], m['currency'])}.\nReason:  {m['reason']}\nBooking: {m['bookingId']}\n"
        ),
    )


RENDERERS: dict[str, Callable[[Mapping[str, Any]], Email]] = {
    "booking.confirmed": _confirmed,
    "booking.cancelled": _cancelled,
    "booking.expired": _expired,
    "booking.refunded": _refunded,
}


def render(message_type: str, payload: Mapping[str, Any]) -> Email:
    try:
        return RENDERERS[message_type](payload)
    except (KeyError, TypeError, ValueError, ArithmeticError) as error:
        raise PoisonMessage(f"{message_type} is missing or has an invalid field: {error}") from error
