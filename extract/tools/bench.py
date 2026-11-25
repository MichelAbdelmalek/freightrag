"""Time and peak memory of real extraction on synthetic scans. Run in Docker with the container's limits, for example:
    docker run --rm --network none --cpus 1 --memory 768m --entrypoint python freightrag-extract-test tools/bench.py
"""

import resource
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, ".")
from freightrag_extract.config import Settings  # noqa: E402
from freightrag_extract.extractor import extract  # noqa: E402
from tools.make_fixtures import scanned_pdf  # noqa: E402

DATA = Path("/data")
source = (DATA / "tenant-nile-freight" / "rate-confirmation-48213.txt").read_text(encoding="utf-8").splitlines()
for label, lines in [("1 page", source), ("5 pages", source * 5)]:
    pdf = scanned_pdf(lines)
    times = []
    for _ in range(3):
        started = time.perf_counter()
        result = extract(pdf, Settings())
        times.append(time.perf_counter() - started)
    pages = result.report["pageCount"]
    print(f"{label}: {pages} page(s), median {statistics.median(times):.1f}s total = {statistics.median(times) / pages:.1f}s per page, "
          f"confidence {result.report['meanOcrConfidence']}")
print(f"peak memory: python {resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024:.0f} MB, "
      f"tesseract (largest child) {resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss / 1024:.0f} MB")
