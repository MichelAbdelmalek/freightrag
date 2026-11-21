"""The real DeepEval FaithfulnessMetric, driven through our adapter against a canned model server: no network, and
the autouse fixture records any connection attempt (telemetry clients swallow errors, so we assert none were made)."""

import json
import os

import httpx

from freightrag_eval.deepeval_judge import DeepEvalJudge
from freightrag_eval.judge import JudgeConfig

CLAIMS = ["Detention is $50.00 per hour", "Detention is capped at 8 hours"]


def canned(prompts=None):
    def handler(request: httpx.Request) -> httpx.Response:
        body = json.loads(request.content)
        if prompts is not None:
            prompts.append(body["messages"][0]["content"])
        props = set(body["format"]["properties"])
        if "truths" in props:
            content = {"truths": ["Detention: $50.00 per hour after 2 free hours at pickup or delivery"]}
        elif "claims" in props:
            content = {"claims": CLAIMS}
        elif "verdicts" in props:
            content = {"verdicts": [{"verdict": "yes", "reason": None},
                                    {"verdict": "no", "reason": "the context has no cap on detention hours"}]}
        else:
            raise AssertionError(f"unexpected schema {props}")
        return httpx.Response(200, json={"message": {"content": json.dumps(content)}})
    return handler


def judge(handler):
    return DeepEvalJudge(JudgeConfig.from_environ({}), transport=httpx.MockTransport(handler))


def test_unsupported_claims_are_extracted_with_the_judges_reason(network_attempts):
    result = judge(canned()).faithfulness("What is the detention rate?", "Detention is $50.00 per hour, capped at 8 hours.",
                                          ["Detention: $50.00 per hour after 2 free hours at pickup or delivery"])

    assert result.error is None
    assert result.score == 0.5 and result.claims == 2
    assert [(u.claim, u.reason) for u in result.unsupported] == [("Detention is capped at 8 hours", "the context has no cap on detention hours")]
    assert network_attempts == []                       # no telemetry, no upload, no login


def test_the_judge_sees_only_the_context_it_is_given():
    prompts = []
    judge(canned(prompts)).faithfulness("q?", "an answer", ["ONLY-THIS-CHUNK"])
    assert any("ONLY-THIS-CHUNK" in p for p in prompts)
    assert not any("some other chunk" in p for p in prompts)


def test_privacy_switches_are_set_before_deepeval_runs():
    assert os.environ["DEEPEVAL_TELEMETRY_OPT_OUT"] == "1"
    assert os.environ["DEEPEVAL_DISABLE_DOTENV"] == "1"          # DeepEval would otherwise read .env files
    assert os.environ["DEEPEVAL_FILE_SYSTEM"] == "READ_ONLY"


def test_an_unreachable_judge_is_a_category_not_a_crash():
    def handler(request):
        raise httpx.ConnectError("down")

    result = judge(handler).faithfulness("q?", "an answer", ["ctx"])
    assert result.score is None and result.error is not None and result.error.startswith("judge_")
