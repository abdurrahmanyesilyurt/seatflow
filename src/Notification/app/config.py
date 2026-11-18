from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Configuration from environment variables (same names style as the .NET services, e.g. RABBITMQ_URL)."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    service_name: str = "notification"

    database_url: str = "postgresql://notification:notification@localhost:5433/notification"
    rabbitmq_url: str = "amqp://seatflow:seatflow@localhost:5672/"

    # Must match the .NET MessagingOptions so all services share one topology.
    exchange: str = "seatflow"
    dead_letter_exchange: str = "seatflow.dlx"
    queue: str = "notification"
    prefetch: int = 16
    delivery_limit: int = 5

    smtp_host: str = "localhost"
    smtp_port: int = 1025
    mail_from: str = "SeatFlow <tickets@seatflow.local>"

    jwks_url: str = "http://localhost:8080/.well-known/jwks.json"
    jwt_issuer: str = "seatflow-gateway"
    jwt_audience: str = "seatflow"


@lru_cache
def get_settings() -> Settings:
    return Settings()
