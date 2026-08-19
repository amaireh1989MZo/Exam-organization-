"""SQLite schema and CRUD functions for the question bank app."""

import sqlite3
from contextlib import contextmanager
from pathlib import Path

DB_PATH = Path(__file__).parent / "job_banks.db"

SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    raw_text TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS domains (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    description TEXT,
    weight REAL NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS questions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    domain_id INTEGER NOT NULL REFERENCES domains(id) ON DELETE CASCADE,
    question_text TEXT NOT NULL,
    option_a TEXT NOT NULL,
    option_b TEXT NOT NULL,
    option_c TEXT,
    option_d TEXT,
    correct_answer TEXT NOT NULL,
    difficulty TEXT NOT NULL DEFAULT 'medium',
    bloom_level TEXT NOT NULL DEFAULT 'recall',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
"""


@contextmanager
def get_connection():
    conn = sqlite3.connect(DB_PATH)
    conn.execute("PRAGMA foreign_keys = ON")
    conn.row_factory = sqlite3.Row
    try:
        yield conn
        conn.commit()
    finally:
        conn.close()


def init_db():
    with get_connection() as conn:
        conn.executescript(SCHEMA)


# --- Jobs -------------------------------------------------------------

def create_job(title, raw_text):
    with get_connection() as conn:
        cur = conn.execute(
            "INSERT INTO jobs (title, raw_text) VALUES (?, ?)",
            (title, raw_text),
        )
        return cur.lastrowid


def get_jobs():
    with get_connection() as conn:
        return conn.execute("SELECT * FROM jobs ORDER BY created_at DESC").fetchall()


def get_job(job_id):
    with get_connection() as conn:
        return conn.execute("SELECT * FROM jobs WHERE id = ?", (job_id,)).fetchone()


def delete_job(job_id):
    with get_connection() as conn:
        conn.execute("DELETE FROM jobs WHERE id = ?", (job_id,))


# --- Domains ------------------------------------------------------------

def create_domain(job_id, name, description="", weight=0):
    with get_connection() as conn:
        cur = conn.execute(
            "INSERT INTO domains (job_id, name, description, weight) VALUES (?, ?, ?, ?)",
            (job_id, name, description, weight),
        )
        return cur.lastrowid


def get_domains(job_id):
    with get_connection() as conn:
        return conn.execute(
            "SELECT * FROM domains WHERE job_id = ? ORDER BY id", (job_id,)
        ).fetchall()


def get_domain(domain_id):
    with get_connection() as conn:
        return conn.execute(
            "SELECT * FROM domains WHERE id = ?", (domain_id,)
        ).fetchone()


def delete_domain(domain_id):
    with get_connection() as conn:
        conn.execute("DELETE FROM domains WHERE id = ?", (domain_id,))


# --- Questions ------------------------------------------------------------

def create_question(
    domain_id,
    question_text,
    option_a,
    option_b,
    correct_answer,
    option_c=None,
    option_d=None,
    difficulty="medium",
    bloom_level="recall",
):
    with get_connection() as conn:
        cur = conn.execute(
            """INSERT INTO questions
               (domain_id, question_text, option_a, option_b, option_c, option_d,
                correct_answer, difficulty, bloom_level)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (
                domain_id,
                question_text,
                option_a,
                option_b,
                option_c,
                option_d,
                correct_answer,
                difficulty,
                bloom_level,
            ),
        )
        return cur.lastrowid


def get_questions(domain_id=None, job_id=None):
    with get_connection() as conn:
        if domain_id is not None:
            return conn.execute(
                "SELECT * FROM questions WHERE domain_id = ? ORDER BY id",
                (domain_id,),
            ).fetchall()
        if job_id is not None:
            return conn.execute(
                """SELECT q.* FROM questions q
                   JOIN domains d ON q.domain_id = d.id
                   WHERE d.job_id = ? ORDER BY q.domain_id, q.id""",
                (job_id,),
            ).fetchall()
        return conn.execute("SELECT * FROM questions ORDER BY id").fetchall()


def update_question(question_id, **fields):
    if not fields:
        return
    columns = ", ".join(f"{key} = ?" for key in fields)
    values = list(fields.values()) + [question_id]
    with get_connection() as conn:
        conn.execute(f"UPDATE questions SET {columns} WHERE id = ?", values)


def delete_question(question_id):
    with get_connection() as conn:
        conn.execute("DELETE FROM questions WHERE id = ?", (question_id,))
