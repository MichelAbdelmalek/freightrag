"""Answer-quality evaluation: does the answer service say the right things, abstain when it should, and cite honestly?

Two separate stages, so answers are generated once and judged as often as needed:

    docker compose --profile eval run --rm answer-eval generate [--limit N | --ids q01,q05]
    docker compose --profile eval run --rm answer-eval judge results/answers-<UTC>.json [--skip-faithfulness]
    docker compose --profile eval run --rm answer-eval calibrate

generate  asks POST /api/v1/answer every question and saves answers, citations and the cited chunk text.
judge     reads a saved file (never calls the answer service): fact check, abstention table, faithfulness.
calibrate runs the faithfulness judge on hand-labelled cases and reports how often it agrees with the labels.

Exit codes: 0 = done, 1 = TENANT ISOLATION FAILURE (generate) or a FALSE ANSWER (judge), 2 = setup error.
"""

import argparse
import hashlib
import json
import os
import sys
from collections.abc import Callable, Mapping
from pathlib import Path

import httpx

from freightrag_eval import answer_report, answers, calibration
from freightrag_eval import compare as compare_runs
from freightrag_eval.answer_judge import judge_answers
from freightrag_eval.client import ApiError, MissingKeyError, SearchClient, api_key
from freightrag_eval.dataset import DatasetError, load
from freightrag_eval.judge import Judge, JudgeConfig

HERE = Path(__file__).resolve().parent


def build_judge(environ: Mapping[str, str]) -> tuple[Judge, dict]:
    """The real judge. DeepEval is imported only here, after the privacy switches in deepeval_judge are set."""
    config = JudgeConfig.from_environ(environ)
    from freightrag_eval.deepeval_judge import DeepEvalJudge
    return DeepEvalJudge(config), config.describe()


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p: argparse.ArgumentParser) -> None:
        p.add_argument("--dataset", type=Path, default=HERE / "retrieval_set.jsonl")
        p.add_argument("--ids", type=lambda s: [i.strip() for i in s.split(",") if i.strip()], help="only these question ids")
        p.add_argument("--limit", type=int, help="only the first N questions")

    gen = sub.add_parser("generate", help="stage 1: ask the answer service")
    common(gen)
    gen.add_argument("--answer-url", default=os.environ.get("FREIGHTRAG_ANSWER_URL", "http://answer:8090"))
    gen.add_argument("--api-url", default=os.environ.get("FREIGHTRAG_API_URL", "http://api:8080"))
    gen.add_argument("--results-dir", type=Path, default=HERE / "results")
    gen.add_argument("--resume", type=Path, help="an answers file to continue: only missing or failed questions are asked, "
                     "and the file is updated in place")

    jud = sub.add_parser("judge", help="stage 2: judge a saved answers file")
    common(jud)
    jud.add_argument("answers_file", type=Path)
    jud.add_argument("--skip-faithfulness", action="store_true", help="deterministic checks only (fast, no judge model)")

    cmp_ = sub.add_parser("compare", help="compare judged runs (for example straight, retry, graph) on the same dataset")
    cmp_.add_argument("judged_files", type=Path, nargs="+")

    cal = sub.add_parser("calibrate", help="run the judge on hand-labelled cases")
    cal.add_argument("--calibration", type=Path, default=HERE / "judge_calibration.jsonl")
    return parser


def main(argv: list[str] | None = None, environ: Mapping[str, str] | None = None,
         transport: httpx.BaseTransport | None = None,
         judge_factory: Callable[[Mapping[str, str]], tuple[Judge, dict]] | None = None) -> int:
    environ = os.environ if environ is None else environ
    args = _parser().parse_args(argv)
    try:
        if args.command == "generate":
            return _generate(args, environ, transport)
        if args.command == "judge":
            return _judge(args, environ, judge_factory or build_judge)
        if args.command == "compare":
            return _compare(args)
        return _calibrate(args, environ, judge_factory or build_judge)
    except (compare_runs.CompareError, DatasetError, ApiError, calibration.CalibrationError, ValueError, FileNotFoundError, MissingKeyError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


def _generate(args, environ, transport) -> int:
    questions = answers.select(load(args.dataset), args.ids, args.limit)
    keys, missing = {}, []
    for tenant in sorted({q.tenant for q in questions}):
        try:
            keys[tenant] = api_key(tenant, environ)
        except MissingKeyError as error:
            missing.append(str(error))
    if missing:
        print("\n".join(missing), file=sys.stderr)
        return 2

    previous = answers.load_previous(args.resume) if args.resume else None
    path = args.resume or answers.new_path(args.results_dir)
    dataset_sha = hashlib.sha256(args.dataset.read_bytes()).hexdigest()

    def save_to(records, failures, complete):
        answers.save(path, dataset_sha256=dataset_sha, answer_url=args.answer_url, api_url=args.api_url,
                     records=records, failures=failures, complete=complete)

    api = SearchClient(args.api_url, transport=transport)
    answer_client = answers.AnswerClient(args.answer_url, transport=transport)
    try:
        # The answer service's /health/ready covers the search API and the model, so one check is enough.
        if not answer_client.ready():
            print("error: the answer service is not ready (is `docker compose up -d answer` running and the model pulled?)",
                  file=sys.stderr)
            return 2
        generation = answers.generate(
            questions, keys, answer_client, api, previous=previous,
            progress=lambda r: print(f"{r['id']:<5} {r['status']:<21} {r['client_ms'] / 1000:>6.1f}s", flush=True),
            checkpoint=lambda records, failures: save_to(records, failures, complete=False))
    finally:
        api.close()
        answer_client.close()

    save_to(generation.records, generation.isolation_failures, complete=True)
    print(f"\nanswers: {path}")
    if generation.isolation_failures:
        print("\nTENANT ISOLATION FAILURE — a citation pointed at a document the tenant cannot see:")
        print("\n".join(f"  {f}" for f in generation.isolation_failures))
        return 1
    return 0


def _judge(args, environ, factory) -> int:
    payload = json.loads(args.answers_file.read_text(encoding="utf-8"))
    questions = {q.id: q for q in load(args.dataset)}
    records = payload["questions"]
    if args.ids:
        records = [r for r in records if r["id"] in set(args.ids)]
    if args.limit is not None:
        records = records[: args.limit]

    dataset_sha = hashlib.sha256(args.dataset.read_bytes()).hexdigest()
    if payload.get("complete") is False:
        print("note: this answers file is incomplete (the run did not finish); only the questions it holds are judged. "
              "Continue it with: generate --resume <file>")
    if payload.get("dataset_sha256") != dataset_sha:
        print("note: the dataset changed since these answers were generated (answer_facts may have been edited).")

    judge, judge_info = (None, None) if args.skip_faithfulness else factory(environ)
    # The false answers are known before the slow faithfulness pass: show them first.
    from freightrag_eval import abstention
    early = abstention.summarize({r["id"]: abstention.classify(questions[r["id"]].kind, r["status"]) for r in records
                                  if r["id"] in questions})
    if banner := answer_report.false_answer_banner(early):
        print(banner + "\n")

    try:
        judgements, summary = judge_answers(records, questions, judge, progress=lambda j: print(
            f"judged {j.id:<5} {j.outcome}", flush=True) if judge else None)
    finally:
        if hasattr(judge, "close"):
            judge.close()

    print()
    print(answer_report.question_table(judgements))
    print()
    print(answer_report.findings(judgements))
    print()
    print(answer_report.summary_text(summary))
    path = answer_report.write_judged(args.answers_file, answers=payload, dataset_sha256_now=dataset_sha,
                                      judge_info=judge_info, judgements=judgements, summary=summary)
    print(f"\njudged: {path}")
    return 1 if summary.abstention.false_answer_ids else 0


def _compare(args) -> int:
    runs = [compare_runs.load_run(p) for p in args.judged_files]
    result = compare_runs.compare(runs)
    print(compare_runs.report_text(result))
    return 1 if any(s.counts["false_answer"] or s.isolation_failures for s in result.stats) else 0


def _calibrate(args, environ, factory) -> int:
    cases = calibration.load(args.calibration)
    judge, info = factory(environ)
    print(f"judge: {info['provider']} / {info['model']}   cases: {len(cases)}\n")
    try:
        report = calibration.run(cases, judge, progress=lambda o: print(f"{o.case.id}: {o.predicted}", flush=True))
    finally:
        if hasattr(judge, "close"):
            judge.close()
    print()
    print(calibration.report_text(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
