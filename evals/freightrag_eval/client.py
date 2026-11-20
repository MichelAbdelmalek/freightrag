"""HTTP client for the FreightRAG API. API keys come from environment variables and never appear in output."""

from collections.abc import Mapping

import httpx

from freightrag_eval.headers import section_of
from freightrag_eval.metrics import Hit

ENV_PREFIX = "FREIGHTRAG_API_KEY_"


class ApiError(Exception):
    """An API call failed. The message names the request and status only — never headers or keys."""


class MissingKeyError(Exception):
    pass


def env_var_for(tenant: str) -> str:
    """tenant-nile-freight → FREIGHTRAG_API_KEY_TENANT_NILE_FREIGHT"""
    return ENV_PREFIX + tenant.upper().replace("-", "_")


def api_key(tenant: str, environ: Mapping[str, str]) -> str:
    name = env_var_for(tenant)
    value = environ.get(name, "").strip()
    if not value:
        raise MissingKeyError(f"Set {name} to the API key of {tenant} (see evals/README.md).")
    return value


class SearchClient:
    def __init__(self, base_url: str, transport: httpx.BaseTransport | None = None, timeout: float = 120.0):
        self._http = httpx.Client(base_url=base_url, transport=transport, timeout=timeout)

    def close(self) -> None:
        self._http.close()

    def wait_until_ready(self, attempts: int = 30) -> None:
        """The API may still be starting (e.g. right after `docker compose up`)."""
        import time
        for attempt in range(attempts):
            try:
                if self._http.get("/health/ready").status_code == 200:
                    return
            except httpx.TransportError:
                pass
            if attempt < attempts - 1:
                time.sleep(2)
        raise ApiError("GET /health/ready did not return 200 (is `docker compose up -d api` running?)")

    def search(self, key: str, question: str, top_k: int) -> list[Hit]:
        body = self._call("POST", "/api/v1/search", key, json={"query": question, "topK": top_k})
        return [
            Hit(rank=i, document_id=r["documentId"], title=r["title"], section=section_of(r["content"]), distance=r["distance"])
            for i, r in enumerate(body, start=1)
        ]

    def document_ids(self, key: str) -> set[str]:
        """All document ids visible to the key's tenant (follows paging)."""
        ids: set[str] = set()
        page = 1
        while True:
            body = self._call("GET", "/api/v1/documents", key, params={"page": page, "pageSize": 100})
            ids.update(item["id"] for item in body["items"])
            if page >= body["totalPages"]:
                return ids
            page += 1

    def _call(self, method: str, path: str, key: str, **kwargs):
        try:
            response = self._http.request(method, path, headers={"X-Api-Key": key}, **kwargs)
        except httpx.TransportError as error:
            raise ApiError(f"{method} {path} failed: {type(error).__name__}") from None
        if response.status_code != 200:
            raise ApiError(f"{method} {path} returned {response.status_code}")
        return response.json()
