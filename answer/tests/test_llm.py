import json

import httpx
import pytest

from freightrag_answer.llm import (ContextWindowExceeded, LlmError, OllamaChat, RESPONSE_SCHEMA, truncation_reason,
                                   LlmReply)
from freightrag_answer.prompt import build_messages
from tests.fakes import source


def chat(handler, num_ctx=8192, num_predict=512, model="qwen3:8b"):
    return OllamaChat("http://ollama:11434", model, num_ctx, num_predict, llm_timeout=180.0, connect_timeout=5.0,
                      transport=httpx.MockTransport(handler))


def ok_body(text='{"status":"answered","answer":"x [1]","citations":[1]}', prompt=300, out=20, reason="stop"):
    return {"message": {"role": "assistant", "content": text}, "done_reason": reason,
            "prompt_eval_count": prompt, "eval_count": out}


MESSAGES = build_messages("What is the rate?", [source(1)])


def test_the_request_asks_for_deterministic_structured_output_without_thinking():
    seen = {}

    def handler(request):
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json=ok_body())

    reply = chat(handler).generate(MESSAGES)

    body = seen["body"]
    assert seen["path"] == "/api/chat"
    assert body["model"] == "qwen3:8b" and body["stream"] is False and body["think"] is False
    assert body["format"] == RESPONSE_SCHEMA
    assert body["options"] == {"temperature": 0, "num_ctx": 8192, "num_predict": 512}
    assert body["messages"] == MESSAGES
    assert (reply.text, reply.prompt_tokens, reply.output_tokens, reply.done_reason) == (ok_body()["message"]["content"], 300, 20, "stop")


def test_the_response_schema_is_strict():
    assert RESPONSE_SCHEMA["additionalProperties"] is False
    assert set(RESPONSE_SCHEMA["required"]) == {"status", "answer", "citations"}
    assert RESPONSE_SCHEMA["properties"]["status"]["enum"] == ["answered", "insufficient_context"]


def test_every_call_has_timeouts():
    llm = chat(lambda r: httpx.Response(200, json=ok_body()))
    timeout = llm._http.timeout
    assert timeout.read == 180.0 and timeout.connect == 5.0


def test_a_prompt_that_cannot_fit_fails_before_any_http_call():
    def handler(request):
        raise AssertionError("must not call Ollama")

    huge = build_messages("q?", [source(1, content="x" * 40_000)])
    with pytest.raises(ContextWindowExceeded) as error:
        chat(handler, num_ctx=8192, num_predict=512).generate(huge)
    assert "8192" in str(error.value)
    assert "xxxx" not in str(error.value)          # the message never contains prompt text


def test_the_room_for_the_answer_counts_against_the_window():
    messages = build_messages("q?", [source(1, content="x" * 3000)])
    with pytest.raises(ContextWindowExceeded):
        chat(lambda r: httpx.Response(200, json=ok_body()), num_ctx=1200, num_predict=512).generate(messages)


@pytest.mark.parametrize(("error", "status"), [
    (httpx.ReadTimeout("slow"), 504),
    (httpx.ConnectTimeout("slow"), 504),
    (httpx.ConnectError("down"), 502),
])
def test_transport_failures_map_to_gateway_errors(error, status):
    def handler(request):
        raise error

    with pytest.raises(LlmError) as raised:
        chat(handler).generate(MESSAGES)
    assert raised.value.status_code == status


@pytest.mark.parametrize("response", [
    httpx.Response(500, text="boom"),
    httpx.Response(200, text="not json"),
    httpx.Response(200, json={"unexpected": True}),
])
def test_bad_ollama_responses_are_gateway_errors(response):
    with pytest.raises(LlmError) as raised:
        chat(lambda r: response).generate(MESSAGES)
    assert raised.value.status_code == 502


def test_a_full_window_or_a_cut_off_answer_is_detected():
    assert truncation_reason(LlmReply("x", 300, 20, "stop"), 8192) is None
    assert truncation_reason(LlmReply("x", 8192, 5, "stop"), 8192) == "context_truncated"     # prompt filled the window
    assert truncation_reason(LlmReply("x", 7700, 492, "stop"), 8192) == "context_truncated"
    assert truncation_reason(LlmReply("x", 300, 512, "length"), 8192) == "output_truncated"


def test_ready_checks_that_the_model_is_pulled():
    def handler(request):
        assert request.url.path == "/api/tags"
        return httpx.Response(200, json={"models": [{"name": "nomic-embed-text:latest"}, {"name": "qwen3:8b"}]})

    assert chat(handler).ready() is True
    assert chat(handler, model="qwen3:14b").ready() is False
    assert chat(handler, model="nomic-embed-text").ready() is True        # an implicit :latest tag


def test_ready_is_false_when_ollama_is_down():
    def handler(request):
        raise httpx.ConnectError("down")

    assert chat(handler).ready() is False


def test_a_custom_schema_is_sent_as_format():
    seen = {}
    custom = {"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}

    def handler(request):
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json=ok_body())

    chat(handler).generate(MESSAGES, schema=custom)
    assert seen["body"]["format"] == custom


def test_the_default_schema_is_the_answer_schema():
    seen = {}

    def handler(request):
        seen["body"] = json.loads(request.content)
        return httpx.Response(200, json=ok_body())

    chat(handler).generate(MESSAGES)
    assert seen["body"]["format"] == RESPONSE_SCHEMA
