"""Claude API calls for job-description analysis and question generation."""

import json
import os
import re
from pathlib import Path

import anthropic
from dotenv import load_dotenv

load_dotenv()

MODEL = os.environ.get("ANTHROPIC_MODEL", "claude-sonnet-4-5")
PROMPTS_DIR = Path(__file__).parent / "prompts"

_client = None


def get_client():
    global _client
    if _client is None:
        api_key = os.environ.get("ANTHROPIC_API_KEY")
        if not api_key:
            raise RuntimeError(
                "ANTHROPIC_API_KEY is not set. Add it to your .env file."
            )
        _client = anthropic.Anthropic(api_key=api_key)
    return _client


def load_prompt(name):
    return (PROMPTS_DIR / name).read_text(encoding="utf-8")


def _extract_json(text):
    """Pull a JSON array/object out of a model response, tolerating code fences."""
    cleaned = text.strip()
    fence_match = re.search(r"```(?:json)?\s*(.*?)```", cleaned, re.DOTALL)
    if fence_match:
        cleaned = fence_match.group(1).strip()
    start = min(
        (i for i in (cleaned.find("["), cleaned.find("{")) if i != -1),
        default=-1,
    )
    end = max(cleaned.rfind("]"), cleaned.rfind("}"))
    if start == -1 or end == -1:
        raise ValueError(f"No JSON found in model response: {text[:200]}")
    return json.loads(cleaned[start : end + 1])


def analyze_job_description(raw_text):
    """Return a list of {name, description, weight} domain dicts."""
    system_prompt = load_prompt("job_analyzer.txt")
    response = get_client().messages.create(
        model=MODEL,
        max_tokens=4096,
        system=system_prompt,
        messages=[{"role": "user", "content": raw_text}],
    )
    text = "".join(block.text for block in response.content if block.type == "text")
    domains = _extract_json(text)
    if not isinstance(domains, list):
        raise ValueError("Expected a JSON array of domains")
    return domains


def generate_questions(domain_name, domain_description, count=5, difficulty="medium", bloom_level="application"):
    """Return a list of question dicts for the given domain."""
    system_prompt = load_prompt("question_gen.txt")
    user_message = (
        f"Domain: {domain_name}\n"
        f"Description: {domain_description}\n"
        f"Number of questions: {count}\n"
        f"Difficulty: {difficulty}\n"
        f"Bloom's level: {bloom_level}\n"
    )
    response = get_client().messages.create(
        model=MODEL,
        max_tokens=4096,
        system=system_prompt,
        messages=[{"role": "user", "content": user_message}],
    )
    text = "".join(block.text for block in response.content if block.type == "text")
    questions = _extract_json(text)
    if not isinstance(questions, list):
        raise ValueError("Expected a JSON array of questions")
    return questions
