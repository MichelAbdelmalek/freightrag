from freightrag_extract.ocr import parse_tsv

HEADER = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"


def row(level, block, par, line, word, conf, text):
    return f"{level}\t1\t{block}\t{par}\t{line}\t{word}\t0\t0\t10\t10\t{conf}\t{text}\n"


def test_words_are_grouped_into_lines_and_paragraphs_in_order():
    tsv = (HEADER + row(1, 0, 0, 0, 0, -1, "") + row(5, 1, 1, 1, 1, 96, "Load") + row(5, 1, 1, 1, 2, 94, "number:")
           + row(5, 1, 1, 1, 3, 90, "48213") + row(5, 1, 1, 2, 1, 80, "Date") + row(5, 2, 1, 1, 1, 99, "RATE"))
    parsed = parse_tsv(tsv)
    assert [l.text for l in parsed.lines] == ["Load number: 48213", "Date", "RATE"]
    assert [l.paragraph for l in parsed.lines] == [0, 0, 1]          # a new block starts a new paragraph


def test_rows_without_a_confidence_are_not_words_and_do_not_count_toward_the_mean():
    tsv = HEADER + row(4, 1, 1, 1, 0, -1, "") + row(5, 1, 1, 1, 1, 90, "RATE") + row(5, 1, 1, 1, 2, 70, "x") + row(5, 1, 1, 1, 3, -1, "ghost")
    parsed = parse_tsv(tsv)
    assert parsed.lines[0].text == "RATE x"
    assert parsed.mean_confidence == 80.0


def test_digit_words_with_low_confidence_are_counted():
    tsv = HEADER + row(5, 1, 1, 1, 1, 60, "$5O.OO") + row(5, 1, 1, 1, 2, 95, "$50.00") + row(5, 1, 1, 1, 3, 40, "hello")
    assert parse_tsv(tsv).low_confidence_numeric_tokens == 1


def test_empty_and_garbled_output_gives_no_lines():
    assert parse_tsv("").lines == [] and parse_tsv("garbage").lines == []
    assert parse_tsv("").mean_confidence is None
