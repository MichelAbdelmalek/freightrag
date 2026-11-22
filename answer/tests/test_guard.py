import pytest

from freightrag_answer.guard import extract_identifiers, missing_identifiers, uncited_identifiers
from tests.fakes import source


@pytest.mark.parametrize(("question", "expected"), [
    ("What is the detention rate for load 48213?", ["48213"]),
    ("Why is invoice NFL-2026-1187 higher than the agreed rate?", ["NFL-2026-1187"]),
    ("Were any pallets damaged on BOL SAV-779102?", ["SAV-779102"]),
    ("How much detention is on invoice DH-26-0431?", ["DH-26-0431"]),
    ("what about sav-778431", ["SAV-778431"]),                      # case-insensitive, normalised
    ("Compare loads 48213 and 48227", ["48213", "48227"]),
    ("load 48213 and load 48213 again", ["48213"]),                 # no duplicates
    ("Is $2,705.00 the total in 2026 after a 10-hour break?", []),   # prices, years, durations are not identifiers
    ("Does a Class-8 truck fit? Any COVID-19 rules?", []),          # too few digits to be a code
    ("What is the overtime pay rate for drivers?", []),
])
def test_extracts_identifiers(question, expected):
    assert extract_identifiers(question) == expected


def test_q39_an_identifier_that_no_chunk_contains_is_missing():
    # "load 48999" does not exist; the retrieved RATE sections of other loads look very similar.
    sources = [source(1, content="Document: rate-confirmation | Load: 48213 | Section: RATE\nDetention: $50.00 per hour"),
               source(2, content="Document: rate-confirmation | Load: 48227 | Section: RATE\nDetention: $75.00 per hour")]
    assert missing_identifiers("What is the detention rate for load 48999?", sources) == ["48999"]


def test_an_identifier_found_in_a_chunk_is_not_missing():
    sources = [source(1, content="Document: rate-confirmation | Load: 48213 | Section: RATE\nx")]
    assert missing_identifiers("What is the detention rate for load 48213?", sources) == []


def test_matching_is_case_insensitive():
    sources = [source(1, content="Document: bill-of-lading | BOL: SAV-778431 | Section: SHIPPER\nx")]
    assert missing_identifiers("who is the shipper on sav-778431?", sources) == []


def test_a_bare_number_matches_the_number_inside_a_code():
    sources = [source(1, content="Document: bill-of-lading | BOL: SAV-778431 | Section: SHIPPER\nx")]
    assert missing_identifiers("BOL 778431 shipper?", sources) == []


@pytest.mark.parametrize("content", ["Load: 148213", "Load: 482139", "Load: A48213"])
def test_matching_is_by_whole_token_not_substring(content):
    assert missing_identifiers("detention for load 48213?", [source(1, content=content)]) == ["48213"]


def test_one_missing_identifier_among_several_is_reported():
    sources = [source(1, content="Load: 48213 | Section: RATE\nx")]
    assert missing_identifiers("compare loads 48213 and 48999", sources) == ["48999"]


def test_a_question_without_identifiers_has_nothing_missing():
    assert missing_identifiers("What protective gear is required?", [source(1)]) == []


# --- the cited-chunk check: the identifier must be in a chunk the answer actually CITES, not just somewhere retrieved ---

LOAD_77139_POD = source(2, "bill-of-lading-HOU-221988", "DELIVERY RECEIPT (PROOF OF DELIVERY)",
                        content="Document: bill-of-lading | Load: 77139 | BOL: HOU-221988 | Section: DELIVERY RECEIPT (PROOF OF DELIVERY)\n"
                                "Received by: Marcus Bell, Warehouse Supervisor")
INVOICE_CHARGES = source(1, "invoice-NFL-2026-1219", "CHARGES",
                         content="Document: invoice | Load: 48240 | Invoice: NFL-2026-1219 | Section: CHARGES\nTotal due: $2,245.00")
RATE_48240 = source(3, "rate-confirmation-48240", "RATE",
                    content="Document: rate-confirmation | Load: 48240 | Section: RATE\nTotal agreed rate: $2,110.00")
HANDBOOK = source(4, "driver-handbook-excerpt", "1. SAFETY EQUIPMENT",
                  content="Document: driver-handbook-excerpt | Section: 1. SAFETY EQUIPMENT\nDrivers must wear a vest.")


def test_q38_the_cited_chunk_belongs_to_a_different_load():
    # Load 77118 may well be in other retrieved chunks, but the answer cited load 77139's receipt.
    assert uncited_identifiers("Who received load 77118 at the Atlanta warehouse?", [LOAD_77139_POD]) == ["77118"]


def test_an_identifier_in_the_cited_chunk_is_fine():
    assert uncited_identifiers("Who received load 77139?", [LOAD_77139_POD]) == []


def test_the_identifier_only_in_the_body_of_a_cited_chunk_counts():
    chunk = source(1, content="Document: x | Section: NOTES\nSee load 77118 for details")
    assert uncited_identifiers("what about load 77118?", [chunk]) == []


def test_every_identifier_must_be_covered_by_some_cited_chunk():
    question = "Why is invoice NFL-2026-1219 higher than the agreed rate for load 48240?"
    assert uncited_identifiers(question, [INVOICE_CHARGES, RATE_48240]) == []
    assert uncited_identifiers(question, [RATE_48240]) == ["NFL-2026-1219"]          # the invoice id is nowhere in what was cited


def test_a_bare_number_matches_inside_a_code_and_whole_tokens_only():
    assert uncited_identifiers("BOL 778431 shipper?", [source(1, content="Document: bill-of-lading | BOL: SAV-778431 | Section: SHIPPER\nx")]) == []
    assert uncited_identifiers("detention for load 48213?", [source(1, content="Load: 148213 | Section: RATE\nx")]) == ["48213"]


def test_a_handbook_citation_cannot_satisfy_a_question_about_an_identifier():
    assert uncited_identifiers("What PPE for load 48213?", [HANDBOOK]) == ["48213"]
    assert uncited_identifiers("What PPE for load 48240?", [HANDBOOK, RATE_48240]) == []   # fine next to a matching chunk


def test_a_question_without_identifiers_never_triggers():
    assert uncited_identifiers("What protective gear is required?", [HANDBOOK]) == []
    assert uncited_identifiers("What protective gear is required?", []) == []
