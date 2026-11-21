from freightrag_answer.headers import section_of


def test_the_section_is_read_from_the_context_header():
    assert section_of("Document: rate-confirmation | Load: 48213 | Section: RATE\nbody") == "RATE"
    assert section_of("Document: invoice | Section: 3. DETENTION REPORTING\nx") == "3. DETENTION REPORTING"


def test_a_missing_or_foreign_header_gives_none():
    assert section_of("just text") is None
    assert section_of("Document: x | no section here") is None
    assert section_of("Document: x | Section:  \nbody") is None
