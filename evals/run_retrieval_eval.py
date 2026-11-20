"""Retrieval evaluation: does the search API find the right chunks?

    docker compose up -d api
    docker compose --profile eval run --rm retrieval-eval

Exit codes: 0 = run completed, 1 = TENANT ISOLATION FAILURE, 2 = setup error (missing keys, bad dataset, API down).
"""

import argparse
import hashlib
import os
import sys
from collections.abc import Mapping
from pathlib import Path

import httpx

from freightrag_eval.client import ApiError, MissingKeyError, SearchClient, api_key
from freightrag_eval.dataset import DatasetError, load
from freightrag_eval.metrics import score_question, summarize
from freightrag_eval.report import summary_text, table, write_results

HERE = Path(__file__).resolve().parent


def main(argv: list[str] | None = None, environ: Mapping[str, str] | None = None,
         transport: httpx.BaseTransport | None = None) -> int:
    environ = os.environ if environ is None else environ
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--api-url", default=environ.get("FREIGHTRAG_API_URL", "http://api:8080"))
    parser.add_argument("--dataset", type=Path, default=HERE / "retrieval_set.jsonl")
    parser.add_argument("--top-k", type=int, default=5)
    parser.add_argument("--results-dir", type=Path, default=HERE / "results")
    args = parser.parse_args(argv)

    try:
        questions = load(args.dataset)
        tenants = sorted({q.tenant for q in questions})
        keys, missing = {}, []
        for tenant in tenants:  # check every key up front, so all missing ones are named at once
            try:
                keys[tenant] = api_key(tenant, environ)
            except MissingKeyError as error:
                missing.append(str(error))
        if missing:
            print("\n".join(missing), file=sys.stderr)
            return 2

        client = SearchClient(args.api_url, transport=transport)
        try:
            client.wait_until_ready()
            # Each tenant's own document ids: the ground truth for the isolation check.
            own = {tenant: client.document_ids(keys[tenant]) for tenant in tenants}

            hits, scores, isolation_failures = {}, [], []
            for q in questions:
                hits[q.id] = client.search(keys[q.tenant], q.question, args.top_k)
                for h in hits[q.id]:
                    if h.document_id not in own[q.tenant]:
                        isolation_failures.append(f"{q.id}: {q.tenant} received document {h.document_id} ({h.title})")
                scores.append(score_question(q.id, q.kind, hits[q.id], q.expected))
        finally:
            client.close()
    except (DatasetError, ApiError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    summary = summarize(scores)
    print(table(questions, scores, hits))
    print()
    print(summary_text(summary, args.top_k))

    path = write_results(
        args.results_dir,
        dataset_sha256=hashlib.sha256(args.dataset.read_bytes()).hexdigest(),
        api_url=args.api_url, top_k=args.top_k, questions=questions, scores=scores, hits=hits,
        summary=summary, isolation_failures=isolation_failures)
    print(f"\nresults: {path}")

    if isolation_failures:
        print("\nTENANT ISOLATION FAILURE — the API returned another tenant's documents:")
        print("\n".join(f"  {f}" for f in isolation_failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
