from dataclasses import dataclass


@dataclass(frozen=True)
class Source:
    """One retrieved chunk, numbered from 1 in the order the search returned it. The number is the citation id."""
    number: int
    chunk_id: str
    document_id: str
    title: str
    section: str | None
    chunk_index: int
    content: str
