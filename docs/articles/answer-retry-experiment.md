# Experiment: does a retry help, and does it need LangGraph?

The [answer service](answer-service.md) is a straight line. The first real branch it could grow is: when it abstains
because retrieval probably missed, rewrite the question once, search again and answer from the new chunks. This page is the
experiment that decides whether that branch is worth keeping, and whether LangGraph is the right way to write it. The
criteria below were written **before** any run, so the numbers cannot move the goalposts.

## The three flows

| Flow (`ANSWER_FLOW`) | What it is |
|---|---|
| `straight` (default) | Today's pipeline. |
| `retry` | The same pipeline plus one rewrite-and-retry, in plain Python. Built and measured, then **removed** from the code (it lives in commit `4cee73b` of the branch history, and its revert is the commit after it). |
| `graph` | The same retry as a LangGraph `StateGraph`. **Not built**: it failed the dependency gate (below). |

A retry happens only after an abstention with the reason `identifier_not_found`, `cited_chunk_lacks_identifier` or
`model_abstained`, and only once (at most two searches and three model calls per request). It never fires for an empty
search, a truncated reply, an invented citation or malformed JSON.

### How the rewrite is kept safe

- The rewrite only becomes a **search query**. The guard, the answer prompt and the cited-chunk check all keep using the
  **original question**, so a rewrite cannot change what is asked or which identifiers must be present.
- Its identifiers must equal the question's. A rewrite that drops, changes or adds a load number (another tenant's, say) is
  rejected and the first abstention stands. Length, line and markup limits apply, and the question is escaped in the
  rewrite prompt.
- **No chunk text goes into the rewrite prompt**, so documents cannot steer it. The price is recall: the rewrite only knows
  the question and why the first search failed.
- The rewrite is never logged, returned or saved. `diagnostics` carries only categories and counts (`flow`, `attempts`,
  `firstReason`, `rewriteStatus`, `rewriteMs`, `modelCalls`).

## Criteria, fixed before running

Measured on all 45 questions, with the straight flow re-run the same day on the same stack.

| Gate | Rule |
|---|---|
| G0 safety | False answers stay at 0 of 8 and tenant isolation failures at 0. Any breach rejects the retry. |
| G1 it helps | False refusals fall by at least 2 against the straight run, and every recovered answer passes a manual read (the cited chunk holds the fact). |
| G2 no regression | Fact recall over answered questions at least 88%; end-to-end fact recall at least the straight run's; every question the straight run answered is answered identically. |
| G3 latency | Median `client_ms` at most 1.2x the straight run; mean model calls per question at most 1.5. |
| G5 framework cost | LangGraph is adopted only if it adds at most 15 locked packages and at most 40 MB, `graph.py` is no longer than `retry.py`, and tracing is provably off. |

**Prediction, also written first:** the retry recovers 1 to 3 of the false refusals. It cannot recover a missing document, and
it never runs on q17 (an answered, wrong-field question). LangGraph would not change the answers, only the code, so the
expected decision is to keep the retry in plain Python if it passes and not adopt LangGraph.

## Result: retry rejected, LangGraph not adopted

Run on 2026-11-25: `qwen3:8b` on CPU, `NUM_CTX` 4096, the 18-question **subset gate** the plan sets before the full run
(the five retrieval misses q02, q23, q26, q37 and q38, q17, four answered controls, and the eight unanswerable questions).

| Measure | straight | retry |
|---|---|---|
| False answers (of 8 unanswerable) | 0 | **0** |
| Tenant isolation failures | 0 | 0 |
| Retrieval misses recovered (q02, q23, q26, q37, q38) | n/a | **0 of 5** after manual review |
| False refusals in the subset | 4 plus 2 timeouts | 4 |

- **Safety held (G0).** Every unanswerable question still ended in an abstention. The retry rewrote the question for
  seven of the eight (the guard's or the model's abstention triggered it); the eighth rewrite, for q43, was rejected because it changed an
  identifier, and the first abstention stood. Nothing got through.
- **It did not help (G1).** q02, q23, q26 and q37 were retried (the rewrite was accepted) and still abstained: the right
  chunk did not appear in the second search either. The only question whose outcome changed was q38: it went from the
  guard's refusal to an **answer**, "Peachtree Distribution Center received load 77118 [4]". That is the cited chunk's delivery
  *location*, not the receiver (the expected fact, James Okafor, is in a bill of lading that was never retrieved). It is the same
  failure as q17: a wrong field from the right document, which no identifier check can catch. The retry **turned a safe refusal
  into a confident wrong answer**.
- **It cost more.** Each retried question made three model calls instead of one (a rewrite of 4 to 20 s, then a second
  answer); several retried questions ended up at 60 to 80 s.
- **The criteria say stop.** The plan's subset gate asks for at least one real recovery among those five. There was none, so
  the full 45-question runs were not started, the retry code was removed from the branch, and `ANSWER_FLOW` is gone.

### LangGraph (G5)

The dependency check came first and failed on its own. Adding `langgraph` with the two-week cutoff (2026-11-10) resolved to
`langgraph 1.2.20` and **27 new locked packages** (`langchain-core`, `langsmith`, `langgraph-checkpoint`, `httpx2`, `requests`,
`orjson`, `pyyaml`, `urllib3`, `zstandard` and others), against a limit of 15. No existing pin moved. Two of them are worth
naming for a security review: `langsmith` is the client of a hosted tracing service (it needs switching off in the image and in
code), and the extra HTTP stacks (`requests`, `httpx2`) are new network-capable surface. Nothing would have changed in the answers,
because the graph only reorders the same steps, so there was no accuracy to win and a lot of dependency to pay for.

### What this tells us

- The five misses are **retrieval** misses, and a rewrite that sees only the question cannot invent the words of a section it
  has never read. Giving it chunk text would risk letting untrusted documents steer it, which the design refused to do.
- A retry makes refusals *less* safe whenever the first answer was blocked by a guard but the second search finds the
  right document and the wrong section. The next useful work is in retrieval and chunking (for example, keeping short
  header-style sections from taking the top-5 slots), or in a check that the *field* asked about appears in the cited
  chunk. It is not an orchestration framework.
- Limits of this result: n is 18 questions, one model, no full run, and the straight arm lost q01 and q38 to a 180 s model
  timeout on a cold start, so its refusal count in the subset is a floor. The decision does not depend on those: no
  recovery means the gate fails whatever the straight run's number is.
