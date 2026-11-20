from freightrag_eval.headers import section_of


def test_reads_the_section_from_the_first_header_line():
    content = "Document: rate-confirmation | Load: 48213 | Section: RATE\nLine haul: $2,450.00"
    assert section_of(content) == "RATE"


def test_keeps_full_headings_with_punctuation():
    content = "Document: bill-of-lading | Load: 48213 | BOL: SAV-778431 | Section: DELIVERY RECEIPT (PROOF OF DELIVERY)\nDelivered"
    assert section_of(content) == "DELIVERY RECEIPT (PROOF OF DELIVERY)"


def test_numbered_handbook_sections():
    assert section_of("Document: driver-handbook | Section: 3. DETENTION REPORTING\nDetention is billable") == "3. DETENTION REPORTING"


def test_only_the_first_line_counts():
    # A body line that happens to contain "Section:" must not be mistaken for the header.
    assert section_of("Document: invoice | Section: CHARGES\nSee Section: RATE of the contract") == "CHARGES"


def test_returns_none_without_a_context_header():
    assert section_of("just some text\nSection: RATE") is None
    assert section_of("") is None
