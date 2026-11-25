import pytest

from freightrag_extract.report import count_headings, looks_like_heading, warnings_for


@pytest.mark.parametrize(("line", "expected"), [
    ("RATE", True), ("RATE CONFIRMATION", True), ("PAYMENT TERMS", True), ("DELIVERY RECEIPT (PROOF OF DELIVERY)", True),
    ("Load number: 48213", False), ("LOADS MUST BE SEALED.", False), ("A", False), ("12345", False), ("x" * 90, False),
    ("RATe", False)])
def test_looks_like_heading_matches_the_dotnet_chunker_rule(line, expected):
    assert looks_like_heading(line) is expected


def test_only_the_first_line_of_a_run_of_heading_shaped_lines_counts():
    assert count_headings("RATE CONFIRMATION\nINVOICE\nbody\nRATE\nDetention: $50\n\nPAYMENT TERMS") == 3


def test_warnings():
    assert warnings_for(mean_confidence=75.0, headings=3, low_numeric=0, dpi_reduced=False, low_confidence=80) == ["low_confidence"]
    assert warnings_for(mean_confidence=None, headings=0, low_numeric=0, dpi_reduced=False, low_confidence=80) == ["no_headings"]
    assert warnings_for(mean_confidence=95.0, headings=2, low_numeric=2, dpi_reduced=True, low_confidence=80) == [
        "numeric_tokens_low_confidence", "dpi_reduced"]
    assert warnings_for(mean_confidence=95.0, headings=2, low_numeric=0, dpi_reduced=False, low_confidence=80) == []
