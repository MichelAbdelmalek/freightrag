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


from pathlib import Path   # noqa: E402

import pytest   # noqa: E402,F811

DATA = Path("/data") if Path("/data").exists() else Path(__file__).resolve().parents[2] / "data"


@pytest.fixture(scope="session")
def pdfs() -> dict[str, bytes]:
    """The synthetic PDFs of tools/make_fixtures.py, generated once per test run."""
    from tools.make_fixtures import build_all
    return build_all(DATA)
