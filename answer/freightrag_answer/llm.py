"""Ollama chat client with structured output.

Three behaviours matter and are easy to get wrong:
- format=<JSON schema> makes Ollama constrain the model to the schema (a malformed reply becomes unlikely, not
  impossible, so validate.py still checks everything).
- num_ctx is the context window for prompt + answer. If the prompt is longer, Ollama silently drops the start of it
  (the rules!) and still answers. So we estimate the prompt before the call and refuse, and we check the counts
  Ollama reports afterwards.
- think=false: qwen3 models can "think" before answering; with a JSON schema that only adds minutes on a CPU.
"""

from dataclasses import dataclass

import httpx

from freightrag_answer.prompt import estimate_tokens

RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "status": {"type": "string", "enum": ["answered", "insufficient_context"]},
        "answer": {"type": "string"},
        "citations": {"type": "array", "items": {"type": "integer"}},
    },
    "required": ["status", "answer", "citations"],
    "additionalProperties": False,
}

_READY_TIMEOUT = httpx.Timeout(3.0)


class LlmError(Exception):
    def __init__(self, status_code: int, message: str):
        super().__init__(message)
        self.status_code = status_code


class ContextWindowExceeded(Exception):
    """The prompt (plus room for the answer) would not fit in num_ctx. The message never contains prompt text."""

    def __init__(self, needed_tokens: int, num_ctx: int):
        super().__init__(
            f"The prompt needs about {needed_tokens} tokens (including room for the answer) but the model's context "
            f"window is {num_ctx}. Use fewer or shorter sources, or raise NUM_CTX.")
        self.needed_tokens = needed_tokens
        self.num_ctx = num_ctx


@dataclass(frozen=True)
class LlmReply:
    text: str
    prompt_tokens: int
    output_tokens: int
    done_reason: str


def truncation_reason(reply: LlmReply, num_ctx: int) -> str | None:
    """Ollama does not error when it truncates. A full window or a cut-off answer means the reply can't be trusted."""
    if reply.prompt_tokens + reply.output_tokens >= num_ctx:
        return "context_truncated"
    if reply.done_reason == "length":
        return "output_truncated"
    return None


class OllamaChat:
    def __init__(self, base_url: str, model: str, num_ctx: int, num_predict: int, llm_timeout: float,
                 connect_timeout: float, transport: httpx.BaseTransport | None = None):
        self.model = model
        self.num_ctx = num_ctx
        self._num_predict = num_predict
        self._http = httpx.Client(base_url=base_url, timeout=httpx.Timeout(llm_timeout, connect=connect_timeout),
                                  transport=transport)

    def close(self) -> None:
        self._http.close()

    def generate(self, messages: list[dict[str, str]]) -> LlmReply:
        needed = estimate_tokens(messages) + self._num_predict
        if needed > self.num_ctx:
            raise ContextWindowExceeded(needed, self.num_ctx)

        body = {
            "model": self.model,
            "messages": messages,
            "stream": False,
            "think": False,
            "format": RESPONSE_SCHEMA,
            "options": {"temperature": 0, "num_ctx": self.num_ctx, "num_predict": self._num_predict},
        }
        try:
            response = self._http.post("/api/chat", json=body)
        except httpx.TimeoutException:
            raise LlmError(504, "The model timed out.") from None
        except httpx.TransportError:
            raise LlmError(502, "The model server is unreachable.") from None
        if response.status_code != 200:
            raise LlmError(502, f"The model server returned an unexpected status ({response.status_code}).")

        try:
            data = response.json()
            return LlmReply(text=data["message"]["content"], prompt_tokens=int(data.get("prompt_eval_count", 0)),
                            output_tokens=int(data.get("eval_count", 0)), done_reason=str(data.get("done_reason", "")))
        except (ValueError, KeyError, TypeError):
            raise LlmError(502, "The model server returned an unreadable response.") from None

    def ready(self) -> bool:
        """True when Ollama answers and the configured model has been pulled."""
        try:
            response = self._http.get("/api/tags", timeout=_READY_TIMEOUT)
            if response.status_code != 200:
                return False
            names = {m["name"] for m in response.json().get("models", [])}
        except (httpx.TransportError, ValueError, KeyError, TypeError):
            return False
        return self.model in names or f"{self.model}:latest" in names
