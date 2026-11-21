"""Stage 2: judge a saved answers file. Pure logic over records plus a Judge; it never calls the answer service."""

from dataclasses import dataclass

from freightrag_eval import abstention
from freightrag_eval.dataset import Question
from freightrag_eval.facts import FactResult, check_facts
from freightrag_eval.judge import FaithfulnessResult, Judge


@dataclass(frozen=True)
class Judgement:
    id: str
    kind: str
    status: str
    outcome: str
    facts: FactResult | None                     # answerable, non-error questions only
    faithfulness: FaithfulnessResult | None      # answered questions only, when a judge was used
    reason: str | None


@dataclass(frozen=True)
class AnswerSummary:
    abstention: abstention.AbstentionSummary
    fact_recall_answered: float | None           # facts found / facts expected, over answered answerable questions
    fact_recall_all: float | None                # the same counting each refusal as zero: end to end
    faithfulness_mean: float | None
    faithfulness_judged: int
    unsupported_claims: int
    judge_errors: int


def cited_chunks(record: dict) -> list[str]:
    """Only what the answer cited reaches the judge: a claim true elsewhere but not cited is unsupported."""
    return [c["chunk_text"] for c in record.get("citations", []) if c.get("chunk_text")]


def judge_answers(records: list[dict], questions: dict[str, Question], judge: Judge | None,
                  progress=None) -> tuple[list[Judgement], AnswerSummary]:
    judgements: list[Judgement] = []
    for record in records:
        q = questions.get(record["id"])
        if q is None:
            raise ValueError(f"{record['id']} is not in the dataset (was the dataset changed since generating?)")
        status = record["status"]
        outcome = abstention.classify(q.kind, status)

        facts = None
        if q.kind == "answerable" and status != "error":
            facts = check_facts(q.answer_facts, record.get("answer") if status == "answered" else None)

        faith = None
        if judge is not None and status == "answered":
            contexts = cited_chunks(record)
            faith = (judge.faithfulness(q.question, record["answer"], contexts) if contexts
                     else FaithfulnessResult(None, 0, (), error="no_cited_chunks"))
        judgements.append(Judgement(q.id, q.kind, status, outcome, facts, faith, record.get("reason")))
        if progress:
            progress(judgements[-1])
    return judgements, summarize(judgements)


def _micro(results: list[FactResult]) -> float | None:
    total = sum(r.total for r in results)
    return sum(r.found for r in results) / total if total else None


def summarize(judgements: list[Judgement]) -> AnswerSummary:
    answered = [j.facts for j in judgements if j.outcome == abstention.ANSWERED and j.facts]
    everything = [j.facts for j in judgements if j.outcome in (abstention.ANSWERED, abstention.FALSE_REFUSAL) and j.facts]
    scored = [j.faithfulness.score for j in judgements if j.faithfulness and j.faithfulness.score is not None]
    return AnswerSummary(
        abstention=abstention.summarize({j.id: j.outcome for j in judgements}),
        fact_recall_answered=_micro(answered), fact_recall_all=_micro(everything),
        faithfulness_mean=sum(scored) / len(scored) if scored else None,
        faithfulness_judged=len(scored),
        unsupported_claims=sum(len(j.faithfulness.unsupported) for j in judgements if j.faithfulness),
        judge_errors=sum(1 for j in judgements if j.faithfulness and j.faithfulness.error),
    )
