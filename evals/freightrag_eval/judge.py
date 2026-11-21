"""The judge's configuration, result types and HTTP backends. No DeepEval import here, so everything that only
needs the types (tests, reports) stays fast and cannot trigger DeepEval's start-up code.

The judge model is configurable: a local Ollama model, or any OpenAI-compatible endpoint. Temperature is always 0.
The API key for a hosted judge comes from the JUDGE_API_KEY environment variable and is never printed, logged,
stored or put in an error message.
"""

from collections.abc import Mapping
from dataclasses import dataclass, field
from typing import Protocol

import httpx


class JudgeError(Exception):
    """A judge call failed. The message is a category and never includes prompts, answers or keys."""


@dataclass(frozen=True)
class JudgeConfig:
    provider: str                                # "ollama" | "openai"
    model: str
    base_url: str
    api_key: str | None = field(default=None, repr=False)     # repr=False: never shows up in logs or tracebacks
    timeout_s: float = 900.0                     # a local model on a CPU can need minutes per call

    @classmethod
    def from_environ(cls, environ: Mapping[str, str]) -> "JudgeConfig":
        provider = environ.get("JUDGE_PROVIDER", "ollama").strip().lower()
        if provider not in ("ollama", "openai"):
            raise ValueError(f"JUDGE_PROVIDER must be 'ollama' or 'openai', got {provider!r}.")
        model = environ.get("JUDGE_MODEL", "").strip() or "qwen3:8b"
        base_url = environ.get("JUDGE_BASE_URL", "").strip()
        api_key = environ.get("JUDGE_API_KEY", "").strip() or None
        if provider == "ollama":
            base_url = base_url or "http://host.docker.internal:11434"
        else:
            if not base_url:
                raise ValueError("Set JUDGE_BASE_URL (e.g. https://api.openai.com/v1) for JUDGE_PROVIDER=openai.")
            if not api_key:
                raise ValueError("Set JUDGE_API_KEY (an environment variable; it is never printed) for JUDGE_PROVIDER=openai.")
        try:
            timeout = float(environ.get("JUDGE_TIMEOUT_S", "900"))
        except ValueError:
            raise ValueError("JUDGE_TIMEOUT_S must be a number of seconds.") from None
        return cls(provider, model, base_url.rstrip("/"), api_key, timeout)

    def describe(self) -> dict[str, str]:
        """What may be written into results files: never the key."""
        return {"provider": self.provider, "model": self.model}


@dataclass(frozen=True)
class Unsupported:
    claim: str
    reason: str | None


@dataclass(frozen=True)
class FaithfulnessResult:
    score: float | None                # supported claims / all claims; None when the judge failed
    claims: int
    unsupported: tuple[Unsupported, ...]
    uncertain: int = 0                 # claims the judge could not decide ("idk"), counted as supported by DeepEval
    error: str | None = None           # a failure category, e.g. "judge_unreachable"


class Judge(Protocol):
    def faithfulness(self, question: str, answer: str, contexts: list[str]) -> FaithfulnessResult: ...


class LlmBackend:
    """One method: prompt (+ optional JSON schema) in, text out. Talks to Ollama or an OpenAI-compatible server."""

    def __init__(self, config: JudgeConfig, transport: httpx.BaseTransport | None = None):
        self._config = config
        self._http = httpx.Client(timeout=config.timeout_s, transport=transport)

    def close(self) -> None:
        self._http.close()

    def complete(self, prompt: str, json_schema: dict | None) -> str:
        c = self._config
        messages = [{"role": "user", "content": prompt}]
        if c.provider == "ollama":
            url = f"{c.base_url}/api/chat"
            headers: dict[str, str] = {}
            body = {"model": c.model, "messages": messages, "stream": False, "think": False,
                    "format": json_schema or "json", "options": {"temperature": 0}}
        else:
            path = "/chat/completions" if c.base_url.endswith("/v1") else "/v1/chat/completions"
            url = f"{c.base_url}{path}"
            headers = {"Authorization": f"Bearer {c.api_key}"}
            body = {"model": c.model, "messages": messages, "temperature": 0}
            if json_schema:
                body["response_format"] = {"type": "json_object"}
        try:
            response = self._http.post(url, json=body, headers=headers)
        except httpx.TimeoutException:
            raise JudgeError("judge_timeout") from None
        except httpx.TransportError:
            raise JudgeError("judge_unreachable") from None
        if response.status_code in (401, 403):
            raise JudgeError("judge_rejected_credentials")
        if response.status_code != 200:
            raise JudgeError(f"judge_http_{response.status_code}")
        try:
            data = response.json()
            if c.provider == "ollama":
                return data["message"]["content"]
            return data["choices"][0]["message"]["content"]
        except (ValueError, KeyError, IndexError, TypeError):
            raise JudgeError("judge_unreadable_response") from None
