# Evaluating answers

[Retrieval evaluation](hybrid-search.md) asks whether search found the right chunks. Once a model writes an answer from
them, a second question appears: **is the answer any good?** "Good" is three separate questions, and they fail
independently, so each gets its own measurement:

| Question | Measured by | Cost |
|---|---|---|
| Did it say the right things? | **Fact check**: deterministic string matching | free, instant |
| Did it answer when it should, and refuse when it should? | **Abstention table**: counting | free, instant |
| Is every claim backed by what it cited? | **Faithfulness**: an LLM judge | minutes, and itself needs checking |

No single "quality score" is reported, on purpose. An answer can be faithful to the wrong chunk (right citation, wrong
document), or correct while citing nothing that supports it. Side by side, the numbers show which part broke.

## Two stages, not one

```
generate:  questions ──► answer service ──► answers-<UTC>.json   (answers, citations, cited chunk text, timings)
judge:     answers-<UTC>.json ──► fact check + abstention table + faithfulness ──► answers-<UTC>.judged.json
```

Generating answers is the slow, expensive part (a local model on a CPU needs about a minute per question), and judging
is another slow step. Keeping them separate means:

- **Judge as often as you like.** Change the judge model, fix a fact in the dataset, or re-run after calibration without
  regenerating a single answer.
- **Every judgement traces to a saved answer.** The file holds exactly what the service said and exactly which chunks it
  cited, so a surprising score can be inspected instead of reproduced.
- **Different failure modes stay apart.** A judge that is down doesn't lose a generation run, and a service that is
  down doesn't waste a judging run.
- **The judge never touches the service.** It reads a file, so it can't change tenants, call anything by accident, or
  need an API key at all.

The `generate` stage fetches the text of every cited chunk (through the document API, with that tenant's own key) and
stores it in the file, so the judge has what it needs without any service. A citation to a document the tenant cannot
see is recorded as a **tenant isolation failure**.

## Fact checks vs LLM judges

Each answerable question lists `answer_facts`: strings a correct answer must contain, such as `$50`, `912-555-0199` or
`Lena Morris`. The answer is checked for each, ignoring case, whitespace and number formatting (`$2,450.00` equals
`$2450`, `$50` equals `$50.00`, a Unicode minus equals `-`), and matching whole tokens (`50` is not found in `$500`,
`48213` is not found in `148213`).

This is **blunt on purpose**:

- It catches the errors that matter most for freight documents: a wrong or missing number, name or identifier. It is
  deterministic, free and explainable: you can see exactly which fact was missing.
- It cannot judge wording. A correct answer that paraphrases a date as "November 15" misses the fact `2026-11-15`. So facts
  are things that must appear verbatim, never sentences, and a fact miss is a prompt to look, not a verdict.

An **LLM judge** reads like a person and handles paraphrase, but it is slower, costs tokens, can be wrong, and varies with
the model. Use each for what it is good at: the cheap deterministic check for facts that must be exact, the judge for the
open-ended question of whether claims are supported. Two overall fact-recall numbers are reported: over the questions that
*were answered* (are the answers it gives right?), and end to end, where a refusal scores zero (is the system useful?).

## Faithfulness to citations

Faithfulness asks: **does the context support every claim in the answer?** The judge (DeepEval's `FaithfulnessMetric`) does
three things: it extracts the claims the answer makes, extracts what the context says, and gives each claim a verdict
(supported, unsupported or undecided). The score is supported claims divided by all claims. The unsupported claims are
listed with the judge's reason, because "0.67" alone doesn't tell you what was invented.

The key choice is **what counts as the context: only the chunks the answer cited.** The retrieval returned five chunks, but
the answer says `[1]`, and the citation is a promise that source 1 supports that sentence. If a claim is true somewhere
else in the corpus (say in a chunk that was retrieved but not cited), it is still unsupported *by its citation*. A reader
who follows the citation will not find it. Judging against everything retrieved would hide exactly this failure.

Faithfulness is not correctness: an answer can faithfully restate the wrong load's chunk. That is why fact recall and
faithfulness are reported next to each other.

## The abstention table

|  | answered | `insufficient_context` |
|---|---|---|
| **answerable** | answered (an attempt) | **false refusal** |
| **unanswerable** | **false answer** | correct abstention |

- **False answers are the costliest cell.** A confident reply to a question the documents cannot answer (a load that
  doesn't exist, another tenant's data, a fee nobody wrote down) is exactly the failure RAG exists to avoid. They are listed
  by id first and last in the output, and any one makes the command exit with code 1, so CI or a script can gate on it.
- **False refusals cost usefulness, not safety.** The rate matters, but a refusal is the safe direction when in doubt.
- "answered" here only means *it attempted an answer*. Whether the answer is right is the fact check's job.

## Judge calibration

A judge is a model too, so its numbers mean nothing until you know how often it is wrong. `judge_calibration.jsonl` holds
about ten hand-labelled cases: an answer, the chunks it cited, and a label of faithful or unfaithful. Several are
deliberately unfaithful in the ways that matter:

- a **wrong number** (the $60 rate of another load),
- an **added claim** (a cap on detention hours that the chunk never states),
- a claim that is true in the corpus but comes from an **uncited chunk**,
- a **contradiction** of the chunk.

`calibrate` runs the judge on them and reports its agreement with the labels, a confusion matrix and each disagreement with
the claims it flagged. A judge that misses invented numbers is not safe to read results from, whatever its average score.
The labels are a human's judgement and ten cases is a small sample: it is a sanity check, not a benchmark.

**Self-preference.** By default the judge is the same model that wrote the answers (`qwen3:8b`). Models tend to rate text in
their own style more kindly, so faithfulness scores here may be optimistic. Calibration measures this for the cases it
contains, and a different judge model (or a hosted one, at the price of sending answers and chunks to a provider) is a
configuration change away.

## What it showed on our data

One run on the real stack: `qwen3:8b` writing the answers and also judging them, CPU only, 45 questions, nothing tuned. Read
it as a first measurement with known limits, not a verdict.

| Measure | Result |
|---|---|
| Answered / abstained / failed | 30 / 15 / 0 |
| **False answers** (unanswerable, but answered) | **0 of 8**: three abstentions came from the guard (median 0.4 s, no model call), five from the model |
| False refusals (answerable, but abstained) | 7 of 37 (18.9%), all the model judging its sources insufficient, none from the guard |
| Fact recall | 91.4% over answered questions; 69.6% end to end (a refusal counts as 0) |
| Faithfulness | mean 0.942 over 30 answers; 3 claims flagged unsupported |
| Time per answer that called the model | median 34 s |

**What the numbers do not say.** Three things need care:

- **The flagged claims were mostly the judge's mistakes.** Of the three, one was real (q38: asked about load 77118, the answer
  cited load 77139's bill of lading and named its receiver). Two were not: the judge flagged a claim that *is* in the cited chunk
  (q11, with a garbled reason) and a true statement that the chunk contains *no* temperature (q12). Judges are weak at claims of
  absence. Always read the flagged claims, not just the score.
- **Calibration agreed with the labels on 9 of 11 cases (82%).** It caught the wrong number, the wrong identifier, both claims
  from an uncited chunk and the contradiction, but **missed the added claim** (a cap on detention hours that the chunk never states) and
  flagged one paraphrase. So a faithfulness score of 1.00 does not rule out an invented detail, which is exactly the failure
  that matters on a rate confirmation. With only eleven cases, and the judge being the same model that wrote the answers, treat
  the faithfulness score as a rough screen.
- **Faithful is not correct.** q17 scored 1.00 for faithfulness and 0 of 1 on its fact: the answer was backed by what it cited, and
  wrong. The fact check and the faithfulness score catch different failures.

**What it shows about the system.** q38 exposed a gap in the [guard](answer-service.md): it checked that the identifier appears in
*some retrieved* chunk, not in the chunk that was *cited*. Load 77118 was in the retrieved set, so the guard let through an answer
built on load 77139's chunk. A second, deterministic check now requires each identifier in the question to appear in a cited
chunk (`cited_chunk_lacks_identifier`). The numbers above were measured **before** it. Re-running eight questions live, q38 now
abstains with the new reason and the controls (q01, q20, q31, q36) still answer. The price is visible in the abstention table:
q38 counts as a false refusal, so the full set would show 8 of 37 instead of 7. q17, the other wrong answer, is **not** fixed:
it cited the right load's DELIVERY section but answered with the delivery location instead of the receiver (a wrong field from the
right document), and the bill of lading that holds the receiver was never retrieved. Both are retrieval misses, and this check
turns one wrong answer into a refusal without improving recall. The questions without a load number (q11) are ambiguous, and the model answered with
several loads' temperatures; the fact `-10` happens to match more than one load, so the fact check passes it. A good fact
distinguishes the intended answer from its look-alikes.

## Keeping it local

DeepEval is a framework with an optional cloud service. For this project it is held to local use: telemetry is disabled,
it is told not to read `.env` files (it does so by default) or write to disk, and only `metric.measure()` is called, never the
evaluation, login or dashboard features. These switches are set in the image and in code before DeepEval is imported, and the
tests refuse any network access and assert that none is attempted. The judge's API key, when a hosted judge is used, comes
only from an environment variable and is never printed, logged or saved.

See also: [Answer service](answer-service.md) for what is being evaluated, and `evals/README.md` for the commands.
