"""Client for the .NET search API. The caller's key is forwarded UNCHANGED: this service never chooses, infers or
checks a tenant. The key alone decides it, inside the .NET API, so another tenant's chunks can't be retrieved here."""

import httpx

from freightrag_answer.headers import section_of
from freightrag_answer.models import Source

_READY_TIMEOUT = httpx.Timeout(3.0)


class SearchError(Exception):
    """The search call failed. The message is safe to return to the caller: it never contains the key or the question."""

    def __init__(self, status_code: int, message: str, retry_after: str | None = None):
        super().__init__(message)
        self.status_code = status_code
        self.retry_after = retry_after


class SearchClient:
    def __init__(self, base_url: str, top_k: int, timeout: float, connect_timeout: float,
                 transport: httpx.BaseTransport | None = None):
        self._top_k = top_k
        self._http = httpx.Client(base_url=base_url, timeout=httpx.Timeout(timeout, connect=connect_timeout),
                                  transport=transport)

    def close(self) -> None:
        self._http.close()

    def search(self, key: str, question: str) -> list[Source]:
        try:
            response = self._http.post("/api/v1/search", headers={"X-Api-Key": key},
                                       json={"query": question, "topK": self._top_k, "mode": "hybrid"})
        except httpx.TimeoutException:
            raise SearchError(504, "The search API timed out.") from None
        except httpx.TransportError:
            raise SearchError(502, "The search API is unreachable.") from None

        status = response.status_code
        if status == 401:
            raise SearchError(401, "The search API rejected the API key.")
        if status == 429:
            raise SearchError(429, "The search API's rate limit was reached; retry later.",
                              retry_after=response.headers.get("Retry-After"))
        if status == 400:
            raise SearchError(400, "The search API rejected the request.")
        if status != 200:
            raise SearchError(502, f"The search API returned an unexpected status ({status}).")

        try:
            return [
                Source(number=i, chunk_id=r["chunkId"], document_id=r["documentId"], title=r["title"],
                       section=section_of(r["content"]), chunk_index=r["chunkIndex"], content=r["content"])
                for i, r in enumerate(response.json(), start=1)
            ]
        except (ValueError, KeyError, TypeError):
            raise SearchError(502, "The search API returned an unreadable response.") from None

    def ready(self) -> bool:
        try:
            return self._http.get("/health/ready", timeout=_READY_TIMEOUT).status_code == 200
        except httpx.TransportError:
            return False
