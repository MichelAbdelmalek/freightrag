"""Faithfulness with DeepEval's FaithfulnessMetric, behind our own small adapter.

How the metric works (three model calls per answer): it extracts the CLAIMS the answer makes, extracts the TRUTHS
from the retrieval context, then asks for a verdict per claim (yes / no / idk) against the context. The score is
supported claims divided by all claims. We pass ONLY the chunks the answer cited as the context, so a claim that is
true elsewhere in the corpus but not in a cited chunk counts as unsupported, which is what a citation promises.

Privacy: the three switches below are set BEFORE deepeval is imported. DeepEval otherwise reports usage telemetry,
loads .env files from the working directory, and writes files. We call metric.measure() directly and never DeepEval's
evaluate() / login / dashboard machinery, so nothing is uploaded anywhere.
"""

import os

os.environ["DEEPEVAL_TELEMETRY_OPT_OUT"] = "1"
os.environ["DEEPEVAL_DISABLE_DOTENV"] = "1"
os.environ["DEEPEVAL_FILE_SYSTEM"] = "READ_ONLY"

from deepeval.metrics import FaithfulnessMetric  # noqa: E402
from deepeval.models import DeepEvalBaseLLM  # noqa: E402
from deepeval.test_case import LLMTestCase  # noqa: E402
from pydantic import ValidationError  # noqa: E402

from freightrag_eval.judge import FaithfulnessResult, JudgeConfig, JudgeError, LlmBackend, Unsupported  # noqa: E402


class _JudgeModel(DeepEvalBaseLLM):
    """Lets DeepEval use our backend. DeepEval passes a pydantic schema; we pass it on as the JSON schema."""

    def __init__(self, backend: LlmBackend, name: str):
        self._backend = backend
        self._name = name
        super().__init__(name)

    def load_model(self):
        return self

    def generate(self, prompt: str, schema=None):
        text = self._backend.complete(prompt, schema.model_json_schema() if schema else None)
        if schema is None:
            return text
        try:
            return schema.model_validate_json(text)
        except ValidationError:
            return text   # DeepEval parses (or rejects) the raw text itself

    async def a_generate(self, prompt: str, schema=None):
        return self.generate(prompt, schema)

    def get_model_name(self) -> str:
        return self._name


class DeepEvalJudge:
    def __init__(self, config: JudgeConfig, transport=None):
        self._backend = LlmBackend(config, transport)
        self._model = _JudgeModel(self._backend, config.model)

    def close(self) -> None:
        self._backend.close()

    def faithfulness(self, question: str, answer: str, contexts: list[str]) -> FaithfulnessResult:
        # include_reason=False skips a fourth call; the per-claim verdict reasons are what we report.
        metric = FaithfulnessMetric(model=self._model, include_reason=False, async_mode=False, verbose_mode=False)
        case = LLMTestCase(input=question, actual_output=answer, retrieval_context=contexts)
        try:
            metric.measure(case, _show_indicator=False)
        except JudgeError as error:
            return FaithfulnessResult(None, 0, (), error=str(error))
        except Exception as error:  # noqa: BLE001 - DeepEval raises its own types; only the class name is kept
            return FaithfulnessResult(None, 0, (), error=f"judge_failed_{type(error).__name__}")

        claims, verdicts = list(metric.claims or []), list(metric.verdicts or [])
        unsupported, uncertain = [], 0
        for claim, verdict in zip(claims, verdicts, strict=False):
            value = str(getattr(verdict.verdict, "value", verdict.verdict)).lower()
            if value == "no":
                unsupported.append(Unsupported(claim, verdict.reason))
            elif value == "idk":
                uncertain += 1
        return FaithfulnessResult(score=metric.score, claims=len(claims), unsupported=tuple(unsupported), uncertain=uncertain)
