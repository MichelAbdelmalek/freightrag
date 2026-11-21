"""Fakes for the two things the service talks to. No network anywhere in the tests."""

import json
from dataclasses import dataclass, field

from freightrag_answer.llm import LlmReply
from freightrag_answer.models import Source

NUM_CTX = 8192


def source(number=1, title="rate-confirmation-48213", section="RATE", content=None, chunk_index=0,
           document_id=None, chunk_id=None):
    if content is None:
        content = f"Document: rate-confirmation | Load: 48213 | Section: {section}\nLine haul: $2,450.00"
    return Source(number=number, chunk_id=chunk_id or f"chunk-{number}", document_id=document_id or f"doc-{number}",
                  title=title, section=section, chunk_index=chunk_index, content=content)


def reply_json(status="answered", answer="Detention is $50.00 per hour [1].", citations=(1,)):
    return json.dumps({"status": status, "answer": answer, "citations": list(citations)})


@dataclass
class FakeSearch:
    sources: list = field(default_factory=list)
    error: Exception | None = None
    calls: list = field(default_factory=list)  # (key, question)
    ready_result: bool = True

    def search(self, key, question):
        self.calls.append((key, question))
        if self.error:
            raise self.error
        return list(self.sources)

    def ready(self):
        return self.ready_result


@dataclass
class FakeLlm:
    text: str = field(default_factory=reply_json)
    error: Exception | None = None
    prompt_tokens: int = 500
    output_tokens: int = 40
    done_reason: str = "stop"
    num_ctx: int = NUM_CTX
    calls: list = field(default_factory=list)  # the messages of each call
    ready_result: bool = True

    def generate(self, messages):
        self.calls.append(messages)
        if self.error:
            raise self.error
        return LlmReply(self.text, self.prompt_tokens, self.output_tokens, self.done_reason)

    def ready(self):
        return self.ready_result
