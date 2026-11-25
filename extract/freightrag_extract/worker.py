"""One extraction in a short-lived process with hard resource limits: python -E -s -m freightrag_extract.worker

Reads the PDF from stdin and writes one JSON object to stdout: {"text", "report"} or {"error": "<code>"}. If a parser
crashes or a limit kills the process, the exit code is not 0 and the caller reports extraction_limit_exceeded. Either
way only this process dies, not the service. Nothing is written to disk: input and output are pipes.
"""

import json
import os
import resource
import sys

from freightrag_extract.config import Settings


def apply_limits(settings: Settings) -> None:
    """Limits inherited by Tesseract too. Address space is virtual memory, so it is set well above the resident size the
    container's own memory limit enforces; its job is to stop a runaway allocation, not to meter normal use."""
    limit = settings.worker_memory_mb * 1024 * 1024
    resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
    resource.setrlimit(resource.RLIMIT_CPU, (settings.request_deadline_s, settings.request_deadline_s))
    resource.setrlimit(resource.RLIMIT_NOFILE, (64, 64))
    resource.setrlimit(resource.RLIMIT_FSIZE, (50 * 1024 * 1024, 50 * 1024 * 1024))


def main() -> int:
    settings = Settings.from_environ(os.environ)
    apply_limits(settings)
    data = sys.stdin.buffer.read(settings.max_body_bytes + 1)

    from freightrag_extract.extractor import extract    # imported after the limits are in force
    from freightrag_extract.pdf import ExtractError

    try:
        result = extract(data, settings)
        payload = {"text": result.text, "report": result.report}
    except ExtractError as error:
        payload = {"error": error.code}
    sys.stdout.write(json.dumps(payload))
    return 0


if __name__ == "__main__":
    sys.exit(main())
