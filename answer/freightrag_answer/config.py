"""Settings from environment variables, validated once at startup so a typo fails fast and names the variable."""

from collections.abc import Mapping
from dataclasses import dataclass
from urllib.parse import urlparse


class ConfigError(Exception):
    pass


def _text(environ: Mapping[str, str], name: str, default: str) -> str:
    value = environ.get(name, default).strip()
    if not value:
        raise ConfigError(f"{name} must not be empty.")
    return value


def _url(environ: Mapping[str, str], name: str, default: str) -> str:
    value = _text(environ, name, default).rstrip("/")
    parsed = urlparse(value)
    if parsed.scheme not in ("http", "https") or not parsed.netloc:
        raise ConfigError(f"{name} must be an http(s) URL, got {value!r}.")
    return value


def _int(environ: Mapping[str, str], name: str, default: int, low: int, high: int | None = None) -> int:
    raw = environ.get(name, str(default)).strip()
    try:
        value = int(raw)
    except ValueError:
        raise ConfigError(f"{name} must be a whole number, got {raw!r}.") from None
    if value < low or (high is not None and value > high):
        bounds = f"at least {low}" if high is None else f"between {low} and {high}"
        raise ConfigError(f"{name} must be {bounds}, got {value}.")
    return value


def _seconds(environ: Mapping[str, str], name: str, default: float) -> float:
    raw = environ.get(name, str(default)).strip()
    try:
        value = float(raw)
    except ValueError:
        raise ConfigError(f"{name} must be a number of seconds, got {raw!r}.") from None
    if not 0 < value <= 3600:
        raise ConfigError(f"{name} must be between 0 and 3600 seconds, got {value}.")
    return value


@dataclass(frozen=True)
class Settings:
    search_api_url: str
    ollama_url: str
    chat_model: str
    num_ctx: int          # the context window Ollama is asked to use (prompt + answer)
    num_predict: int      # the most tokens the answer may use; reserved inside num_ctx
    top_k: int
    search_timeout_s: float
    llm_timeout_s: float  # generous: a local model on a CPU can need a minute or more
    connect_timeout_s: float

    @classmethod
    def from_environ(cls, environ: Mapping[str, str]) -> "Settings":
        num_ctx = _int(environ, "NUM_CTX", 8192, low=1024)
        num_predict = _int(environ, "NUM_PREDICT", 512, low=1)
        if num_predict >= num_ctx:
            raise ConfigError(f"NUM_PREDICT ({num_predict}) must be smaller than NUM_CTX ({num_ctx}): the prompt needs room too.")
        return cls(
            search_api_url=_url(environ, "SEARCH_API_URL", "http://api:8080"),
            ollama_url=_url(environ, "OLLAMA_URL", "http://host.docker.internal:11434"),
            chat_model=_text(environ, "CHAT_MODEL", "qwen3:8b"),
            num_ctx=num_ctx,
            num_predict=num_predict,
            top_k=_int(environ, "TOP_K", 5, low=1, high=20),
            search_timeout_s=_seconds(environ, "SEARCH_TIMEOUT_S", 30.0),
            llm_timeout_s=_seconds(environ, "LLM_TIMEOUT_S", 180.0),
            connect_timeout_s=_seconds(environ, "CONNECT_TIMEOUT_S", 5.0),
        )
