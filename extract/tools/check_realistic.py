"""Runs the extractor over samples-realistic/ and reports which printed and handwritten facts it read.

    docker run --rm --network none -v "$PWD/samples-realistic":/samples:ro --entrypoint python freightrag-extract-test \
        -m tools.check_realistic /samples
"""

import json
import re
import sys
from pathlib import Path

from freightrag_extract.config import Settings
from freightrag_extract.extractor import extract
from freightrag_extract.pdf import ExtractError


def squash(text: str) -> str:
    return re.sub(r"\s+", " ", text)


def main(root: Path) -> None:
    for truth_file in sorted((root / "truth").glob("*.json")):
        truth = json.loads(truth_file.read_text())
        name = truth_file.stem
        print(f"\n{name}")
        for variant in ("clean", "scanned"):
            try:
                result = extract((root / variant / f"{name}.pdf").read_bytes(), Settings())
            except ExtractError as error:
                print(f"  {variant:8} rejected: {error.code}")
                continue
            text = squash(result.text)
            printed = [fact for fact in truth["printed"] if squash(fact) in text]
            hand = [fact for fact in truth["handwritten"] if squash(fact).lower() in text.lower()]
            r = result.report
            print(f"  {variant:8} printed {len(printed)}/{len(truth['printed'])}  handwritten {len(hand)}/{len(truth['handwritten'])}  "
                  f"confidence {r['meanOcrConfidence']}  headings {r['headingCount']}  corrections {r['numericCorrections']}  warnings {r['warnings']}")
            missed = [fact for fact in truth["printed"] if squash(fact) not in text]
            if missed:
                print(f"           printed facts missed: {missed}")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
