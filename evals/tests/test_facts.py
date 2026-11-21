import pytest

from freightrag_eval.facts import check_facts, fact_present, normalize


@pytest.mark.parametrize(("fact", "answer"), [
    ("$50", "Detention is $50.00 per hour [1]."),                # $50 == $50.00
    ("$50.00", "Detention is $50 per hour."),
    ("$2,450", "The line haul is $2450.00."),                    # thousands separator and decimals
    ("$3,110", "Total due: $3,110.00"),
    ("0.5", "a rate of 0.50 per mile"),
    ("lena morris", "Received by LENA   MORRIS, Receiving Supervisor"),   # case and whitespace
    ("-10", "Keep it at −10 degrees F"),                    # unicode minus sign
    ("-10", "Keep it at -10°F."),
    ("912-555-0199", "Call 912‑555‑0199"),             # non-breaking hyphens
    ("hard hat", "Hard hats are required."),                     # a word fact may continue (plural)
    ("2 free hours", "after 2 free hours at pickup"),
    ("2026-11-15", "due 2026-11-15 (Net 30)"),
    ("pallet 7", "Pallet 7 had torn shrink-wrap"),
    ("10:00", "deliver by 10:00."),
])
def test_a_fact_is_found_despite_formatting_differences(fact, answer):
    assert fact_present(fact, answer)


@pytest.mark.parametrize(("fact", "answer"), [
    ("50", "The fee is $500 per day."),                          # not inside a longer number
    ("$50", "Detention is $50.50 per hour."),                    # not a prefix of a decimal
    ("48213", "Load 148213 and load 482139."),
    ("34", "It was delivered at 134 degrees"),
    ("$60", "Detention is $50.00 per hour."),                    # a wrong number is not found
    ("pallet 7", "Pallet 71 was damaged."),
    ("-10", "Keep it at 10 degrees F."),                         # the sign matters
    ("lena morris", "Received by Lena Smith"),
    ("", "anything"),
])
def test_a_fact_is_not_found_when_the_answer_differs(fact, answer):
    assert not fact_present(fact, answer)


def test_numbers_are_canonical():
    assert normalize("$2,450.00 and 4,11 and 0.50 and 10:00") == "$2450 and 4,11 and 0.5 and 10:00"


def test_check_facts_counts_found_and_lists_missing():
    result = check_facts(["$50", "2 free hours", "$99"], "Detention is $50.00 per hour after 2 free hours.")
    assert (result.found, result.total, result.missing) == (2, 3, ("$99",))
    assert result.recall == pytest.approx(2 / 3)


def test_an_abstention_finds_no_facts():
    result = check_facts(["$50"], None)
    assert (result.found, result.total, result.recall) == (0, 1, 0.0)
