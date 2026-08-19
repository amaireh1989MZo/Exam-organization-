"""Convert question-bank records to Moodle GIFT format, with validation."""

import re

GIFT_SPECIAL_CHARS = re.compile(r"([~=#{}:\\])")


def escape_gift_text(text):
    if not text:
        return ""
    return GIFT_SPECIAL_CHARS.sub(r"\\\1", text)


def question_to_gift(question, title=None):
    """Convert a single question record (dict or sqlite3.Row) to a GIFT block.

    `question` is expected to have: question_text, option_a, option_b,
    option_c, option_d (optional), correct_answer ("A"/"B"/"C"/"D").
    """
    q = dict(question)
    options = {
        "A": q.get("option_a"),
        "B": q.get("option_b"),
        "C": q.get("option_c"),
        "D": q.get("option_d"),
    }
    options = {key: value for key, value in options.items() if value}
    correct = (q.get("correct_answer") or "").strip().upper()
    if correct not in options:
        raise ValueError(f"correct_answer {correct!r} is not among the available options")

    lines = []
    if title:
        lines.append(f"::{escape_gift_text(title)}::")
    lines.append(escape_gift_text(q["question_text"]) + " {")
    for letter, text in options.items():
        prefix = "=" if letter == correct else "~"
        lines.append(f"{prefix}{escape_gift_text(text)}")
    lines.append("}")
    return "\n".join(lines)


def questions_to_gift(questions, title_prefix=None):
    """Convert a list of question records into a full GIFT file body."""
    blocks = []
    for index, question in enumerate(questions, start=1):
        title = f"{title_prefix} Q{index}" if title_prefix else f"Question {index}"
        blocks.append(question_to_gift(question, title=title))
    return "\n\n".join(blocks)


def validate_gift(gift_text):
    """Basic structural validation of GIFT-formatted text.

    Returns a list of error strings; an empty list means the text passed
    the checks performed here (not a full GIFT grammar validation).
    """
    errors = []

    # Escaped \{ and \} are literal text, not block delimiters, so they must
    # be excluded from both the balance check and the block scan below.
    open_braces = len(re.findall(r"(?<!\\)\{", gift_text))
    close_braces = len(re.findall(r"(?<!\\)\}", gift_text))
    if open_braces != close_braces:
        errors.append(f"Unbalanced braces: {open_braces} '{{' vs {close_braces} '}}'")

    blocks = re.findall(r"(?<!\\)\{((?:\\.|[^{}])*)(?<!\\)\}", gift_text, re.DOTALL)
    if not blocks:
        errors.append("No question blocks ({...}) found")

    for index, block in enumerate(blocks, start=1):
        answer_lines = [line.strip() for line in block.strip().splitlines() if line.strip()]
        correct_count = sum(1 for line in answer_lines if line.startswith("="))
        if correct_count == 0:
            errors.append(f"Block {index}: no correct answer marked with '='")
        elif correct_count > 1:
            errors.append(f"Block {index}: multiple correct answers marked ({correct_count})")
        if not answer_lines:
            errors.append(f"Block {index}: no answer options found")

    return errors
