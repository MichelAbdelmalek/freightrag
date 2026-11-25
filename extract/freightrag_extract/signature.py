"""The cheapest PDF check: the first five bytes. Strict on purpose (offset 0 only), the same rule as the .NET API."""

_MAGIC = b"%PDF-"


def is_pdf(data: bytes) -> bool:
    return data.startswith(_MAGIC)
