import json

import httpx
import pytest

from freightrag_eval.judge import JudgeConfig, JudgeError, LlmBackend

SECRET = "sk-judge-secret-not-real-0123456789"


def backend(config, handler):
    return LlmBackend(config, transport=httpx.MockTransport(handler))


def ollama():
    return JudgeConfig.from_environ({})


def hosted(base="https://llm.example/v1"):
    return JudgeConfig.from_environ({"JUDGE_PROVIDER": "openai", "JUDGE_BASE_URL": base, "JUDGE_MODEL": "m1", "JUDGE_API_KEY": SECRET})


def test_defaults_to_a_local_ollama_judge():
    c = ollama()
    assert (c.provider, c.model, c.base_url) == ("ollama", "qwen3:8b", "http://host.docker.internal:11434")
    assert c.api_key is None


def test_a_hosted_judge_needs_a_url_and_a_key_from_the_environment():
    with pytest.raises(ValueError, match="JUDGE_BASE_URL"):
        JudgeConfig.from_environ({"JUDGE_PROVIDER": "openai", "JUDGE_API_KEY": SECRET})
    with pytest.raises(ValueError, match="JUDGE_API_KEY"):
        JudgeConfig.from_environ({"JUDGE_PROVIDER": "openai", "JUDGE_BASE_URL": "https://x/v1"})
    with pytest.raises(ValueError, match="JUDGE_PROVIDER"):
        JudgeConfig.from_environ({"JUDGE_PROVIDER": "anthropic"})


def test_the_key_never_shows_in_repr_or_in_what_is_saved():
    c = hosted()
    assert SECRET not in repr(c) and SECRET not in json.dumps(c.describe())
    assert c.describe() == {"provider": "openai", "model": "m1"}


def test_ollama_request_is_deterministic_structured_and_unthinking():
    seen = {}

    def handler(request):
        seen.update(path=request.url.path, body=json.loads(request.content), auth=request.headers.get("Authorization"))
        return httpx.Response(200, json={"message": {"content": '{"a": 1}'}})

    schema = {"type": "object", "properties": {"a": {"type": "integer"}}}
    assert backend(ollama(), handler).complete("judge this", schema) == '{"a": 1}'
    body = seen["body"]
    assert seen["path"] == "/api/chat" and seen["auth"] is None
    assert body["model"] == "qwen3:8b" and body["think"] is False and body["stream"] is False
    assert body["format"] == schema and body["options"] == {"temperature": 0}
    assert body["messages"] == [{"role": "user", "content": "judge this"}]


@pytest.mark.parametrize(("base", "path"), [("https://llm.example/v1", "/v1/chat/completions"), ("https://llm.example", "/v1/chat/completions")])
def test_openai_request_uses_the_key_only_as_a_bearer_header(base, path):
    seen = {}

    def handler(request):
        seen.update(path=request.url.path, body=json.loads(request.content), auth=request.headers["Authorization"],
                    body_text=request.content.decode())
        return httpx.Response(200, json={"choices": [{"message": {"content": "{}"}}]})

    config = hosted(base)
    assert backend(config, handler).complete("p", {"type": "object"}) == "{}"
    assert seen["path"] == path.replace("/v1/v1", "/v1")
    assert seen["auth"] == f"Bearer {SECRET}" and SECRET not in seen["body_text"]
    assert seen["body"]["temperature"] == 0 and seen["body"]["response_format"] == {"type": "json_object"}


@pytest.mark.parametrize(("response", "category"), [
    (httpx.Response(401), "judge_rejected_credentials"),
    (httpx.Response(500), "judge_http_500"),
    (httpx.Response(200, text="not json"), "judge_unreadable_response"),
    (httpx.Response(200, json={"unexpected": 1}), "judge_unreadable_response"),
])
def test_failures_are_categories_that_never_contain_the_key(response, category):
    with pytest.raises(JudgeError) as error:
        backend(hosted(), lambda r: response).complete("prompt with SECRET-PROMPT-TEXT", None)
    assert str(error.value) == category and SECRET not in str(error.value) and "SECRET-PROMPT" not in str(error.value)


@pytest.mark.parametrize(("exc", "category"), [(httpx.ReadTimeout("t"), "judge_timeout"), (httpx.ConnectError("c"), "judge_unreachable")])
def test_transport_failures_are_categories(exc, category):
    def handler(request):
        raise exc

    with pytest.raises(JudgeError, match=category):
        backend(ollama(), handler).complete("p", None)
