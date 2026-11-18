"""Validates the gateway's RS256 JWTs against its JWKS, like the .NET services do."""

import asyncio
from typing import Annotated, Any

import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from app.config import Settings, get_settings

_bearer = HTTPBearer(auto_error=False)
_jwks_clients: dict[str, jwt.PyJWKClient] = {}


def _jwks(url: str) -> jwt.PyJWKClient:
    if url not in _jwks_clients:
        _jwks_clients[url] = jwt.PyJWKClient(url, cache_keys=True, lifespan=300)
    return _jwks_clients[url]


async def current_user(
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(_bearer)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> dict[str, Any]:
    if credentials is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Missing bearer token", {"WWW-Authenticate": "Bearer"})
    token = credentials.credentials
    try:
        # PyJWKClient uses blocking urllib; keep it off the event loop.
        key = await asyncio.to_thread(_jwks(settings.jwks_url).get_signing_key_from_jwt, token)
        return jwt.decode(
            token,
            key.key,
            algorithms=["RS256"],
            audience=settings.jwt_audience,
            issuer=settings.jwt_issuer,
            options={"require": ["exp", "sub", "email"]},
        )
    except jwt.PyJWTError as error:
        raise HTTPException(
            status.HTTP_401_UNAUTHORIZED, f"Invalid token: {error}", {"WWW-Authenticate": "Bearer"}
        ) from error
