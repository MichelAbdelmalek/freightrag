import socket


def test_conftest_blocks_and_records_sockets(network_attempts):
    try:
        socket.create_connection(("example.invalid", 80))
    except OSError:
        pass
    assert len(network_attempts) == 1
