import asyncio
import json
import os
import subprocess
import sys

from freightrag_extract.app import run_worker
from freightrag_extract.config import Settings


def run_cli(data: bytes, env_extra=None):
    env = {**os.environ, **(env_extra or {})}
    return subprocess.run([sys.executable, "-E", "-s", "-m", "freightrag_extract.worker"], input=data, capture_output=True, env=env, cwd="/extract")


def test_the_worker_reads_a_text_pdf_from_stdin_and_writes_json(pdfs):
    done = run_cli(pdfs["rate-confirmation-48213-text.pdf"])
    payload = json.loads(done.stdout)
    assert done.returncode == 0 and "Load number: 48213" in payload["text"] and payload["report"]["pageCount"] >= 1


def test_a_rejected_pdf_is_json_with_a_code_not_a_crash(pdfs):
    assert json.loads(run_cli(pdfs["encrypted.pdf"]).stdout) == {"error": "encrypted_pdf"}
    assert json.loads(run_cli(pdfs["fake.pdf"]).stdout) == {"error": "not_pdf"}


def test_the_worker_leaves_no_files_behind(pdfs, tmp_path):
    run_cli(pdfs["rate-confirmation-48213-scan.pdf"], {"TMPDIR": str(tmp_path)})
    assert list(tmp_path.iterdir()) == []


def test_a_runaway_worker_is_killed_at_the_deadline():
    import asyncio
    result = asyncio.run(run_worker(b"x", Settings(), command=[sys.executable, "-c", "import time; time.sleep(60)"], deadline=1))
    assert result == {"error": "extraction_limit_exceeded"}


def test_a_worker_that_crashes_or_prints_garbage_is_a_limit_error():
    import asyncio
    for code in ("import sys; sys.exit(3)", "print('not json')", "print('[1]')"):
        assert asyncio.run(run_worker(b"x", Settings(), command=[sys.executable, "-c", code])) == {"error": "extraction_limit_exceeded"}


def test_the_memory_limit_is_enforced_in_the_worker_process():
    script = ("from freightrag_extract.config import Settings; from freightrag_extract.worker import apply_limits\n"
              "apply_limits(Settings(worker_memory_mb=128))\n"
              "try:\n    bytearray(300 * 1024 * 1024)\n    print('allocated')\nexcept MemoryError:\n    print('refused')\n")
    done = subprocess.run([sys.executable, "-E", "-s", "-c", script], capture_output=True, cwd="/extract", env={**os.environ, "PYTHONPATH": "/extract"})
    assert done.stdout.decode().strip() == "refused"


def test_a_normal_scan_still_fits_the_default_limits(pdfs):
    assert "text" in asyncio.run(run_worker(pdfs["rate-confirmation-48213-scan.pdf"], Settings(worker_memory_mb=640)))
