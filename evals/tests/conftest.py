import socket

import pytest


@pytest.fixture(autouse=True)
def network_attempts(monkeypatch):
    """No test may touch the network. Attempts are recorded as well as refused, so code that swallows the error
    (telemetry clients do) is still caught by a test asserting this list stays empty."""
    attempts: list[object] = []

    def blocked(*args, **kwargs):
        attempts.append(args[:1])
        raise OSError("network access is blocked in tests")

    monkeypatch.setattr(socket.socket, "connect", blocked)
    monkeypatch.setattr(socket, "create_connection", blocked)
    monkeypatch.setattr(socket, "getaddrinfo", blocked)
    return attempts
