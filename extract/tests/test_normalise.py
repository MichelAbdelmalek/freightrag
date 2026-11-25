from freightrag_extract.normalise import clean_chars, dehyphenate, drop_page_markers, fix_numeric_tokens, join_pages, normalise_page


def test_dehyphenates_lowercase_words_only():
    assert dehyphenate(["The shipment was deliv-", "ered on time."]) == ["The shipment was delivered on time."]
    assert dehyphenate(["Load SAV-", "778431 is here"]) == ["Load SAV-", "778431 is here"]      # capitals and digits stay
    assert dehyphenate(["ends with a dash -", "next"]) == ["ends with a dash -", "next"]


def test_fixes_letters_misread_as_digits_inside_numeric_tokens():
    assert fix_numeric_tokens("Detention: $5O.OO per hour") == ("Detention: $50.00 per hour", 1)
    assert fix_numeric_tokens("Load number: 482l3") == ("Load number: 48213", 1)
    assert fix_numeric_tokens("Total: $2,63O.OO") == ("Total: $2,630.00", 1)


def test_does_not_touch_words_or_identifiers():
    for line in ["SOLO LOAD", "Illinois", "SAV-778431", "NFL-2026-1187", "lol Oslo", "MC# 902114", "Dock 4"]:
        assert fix_numeric_tokens(line) == (line, 0)


def test_strips_page_x_of_y_lines_but_not_prose():
    assert drop_page_markers(["RATE", "Page 2 of 3", "PAGE 7", "See page 2 of the contract."]) == ["RATE", "See page 2 of the contract."]


def test_pages_are_joined_with_a_blank_line_and_no_marker():
    out = join_pages(["RATE\nDetention: $50", "PAYMENT TERMS\nNet 30"])
    assert out == "RATE\nDetention: $50\n\nPAYMENT TERMS\nNet 30"
    assert "PAGE" not in out.upper()


def test_removes_nul_replacement_and_control_characters_but_keeps_newlines():
    assert clean_chars("RATE\x00�\x07\nTotal\tdue") == "RATE\nTotal\tdue"


def test_nfkc_and_ascii_quotes_and_dashes():
    assert clean_chars("‘Dock’ “9” – — １２") == "'Dock' \"9\" - - 12"


def test_normalise_page_runs_the_steps_in_order_and_counts_corrections():
    text, corrections = normalise_page("RATE\nDetention: $5O.OO per hour after 2 free hours\nPage 1 of 2")
    assert text == "RATE\nDetention: $50.00 per hour after 2 free hours" and corrections == 1
