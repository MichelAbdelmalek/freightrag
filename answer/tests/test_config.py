import pytest

from freightrag_answer.config import ConfigError, Settings


def test_defaults():
    s = Settings.from_environ({})
    assert (s.search_api_url, s.ollama_url, s.chat_model) == ("http://api:8080", "http://host.docker.internal:11434", "qwen3:8b")
    assert (s.num_ctx, s.num_predict, s.top_k) == (4096, 512, 5)
    assert s.llm_timeout_s == 180.0 and s.search_timeout_s == 30.0 and s.connect_timeout_s == 5.0


def test_values_come_from_the_environment():
    s = Settings.from_environ({"CHAT_MODEL": "qwen3:4b", "NUM_CTX": "4096", "NUM_PREDICT": "256", "TOP_K": "3", "LLM_TIMEOUT_S": "60"})
    assert (s.chat_model, s.num_ctx, s.num_predict, s.top_k, s.llm_timeout_s) == ("qwen3:4b", 4096, 256, 3, 60.0)


@pytest.mark.parametrize(("name", "value"), [
    ("NUM_CTX", "abc"), ("NUM_CTX", "512"), ("NUM_PREDICT", "0"), ("NUM_PREDICT", "9000"),   # must leave room in the window
    ("TOP_K", "0"), ("TOP_K", "21"), ("LLM_TIMEOUT_S", "0"), ("SEARCH_TIMEOUT_S", "-1"),
    ("SEARCH_API_URL", "ftp://x"), ("OLLAMA_URL", "not a url"), ("CHAT_MODEL", "  "),
])
def test_bad_values_fail_at_startup_and_name_the_variable(name, value):
    with pytest.raises(ConfigError, match=name):
        Settings.from_environ({name: value})
