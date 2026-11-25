# Experiment: does a retry help, and does it need LangGraph?

The [answer service](answer-service.md) is a straight line. The first real branch it could grow is: when it abstains
because retrieval probably missed, rewrite the question once, search again and answer from the new chunks. This page is the
experiment that decides whether that branch is worth keeping, and whether LangGraph is the right way to write it. The
criteria below were written **before** any run, so the numbers cannot move the goalposts.

## The three flows

| Flow (`ANSWER_FLOW`) | What it is |
|---|---|
| `straight` (default) | Today's pipeline. |
| `retry` | The same pipeline plus one rewrite-and-retry, in plain Python (`retry.py`). |
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
