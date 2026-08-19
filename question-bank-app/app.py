"""Streamlit entry point and page routing for the question bank app."""

import streamlit as st

import ai_engine
import database as db
import file_parser
import gift_export

st.set_page_config(page_title="Question Bank Builder", layout="wide")
db.init_db()

PAGES = [
    "Job Description",
    "Domains",
    "Generate Questions",
    "Question Bank",
    "Export GIFT",
]


def _selected_job():
    jobs = db.get_jobs()
    if not jobs:
        st.info("Add a job description first (see the 'Job Description' page).")
        return None
    options = {f"{job['title']} (#{job['id']})": job["id"] for job in jobs}
    label = st.selectbox("Job", list(options.keys()))
    return options[label]


def page_job_description():
    st.header("1. Job Description")
    st.write("Upload a job description (.docx, .pdf, .xlsx) or paste text, then extract its domains.")

    uploaded = st.file_uploader("Upload file", type=["docx", "pdf", "xlsx"])
    pasted_text = st.text_area("...or paste job description text", height=200)
    title = st.text_input("Job title", value="")

    raw_text = None
    if uploaded is not None:
        try:
            raw_text = file_parser.extract_text(uploaded, filename=uploaded.name)
        except Exception as exc:
            st.error(f"Could not parse file: {exc}")
    elif pasted_text.strip():
        raw_text = pasted_text.strip()

    if raw_text:
        with st.expander("Extracted text preview"):
            st.text(raw_text[:3000] + ("..." if len(raw_text) > 3000 else ""))

    if st.button("Save job & analyze domains", disabled=not (raw_text and title.strip())):
        job_id = db.create_job(title.strip(), raw_text)
        with st.spinner("Analyzing job description with Claude..."):
            try:
                domains = ai_engine.analyze_job_description(raw_text)
            except Exception as exc:
                st.error(f"Analysis failed: {exc}")
                domains = []
        for domain in domains:
            db.create_domain(
                job_id,
                name=domain.get("name", "Untitled domain"),
                description=domain.get("description", ""),
                weight=domain.get("weight", 0),
            )
        st.success(f"Saved job #{job_id} with {len(domains)} domain(s). Go to 'Domains' to review.")


def page_domains():
    st.header("2. Domains")
    job_id = _selected_job()
    if job_id is None:
        return

    domains = db.get_domains(job_id)
    if not domains:
        st.warning("No domains yet for this job. Analyze it on the 'Job Description' page.")
        return

    total_weight = sum(d["weight"] for d in domains)
    st.caption(f"Total weight: {total_weight:g}% (should sum to 100%)")

    for domain in domains:
        with st.expander(f"{domain['name']} — {domain['weight']:g}%"):
            st.write(domain["description"] or "_No description_")
            if st.button("Delete domain", key=f"del_domain_{domain['id']}"):
                db.delete_domain(domain["id"])
                st.rerun()


def page_generate_questions():
    st.header("3. Generate Questions")
    job_id = _selected_job()
    if job_id is None:
        return

    domains = db.get_domains(job_id)
    if not domains:
        st.warning("No domains yet for this job. Analyze it on the 'Job Description' page.")
        return

    options = {f"{d['name']} ({d['weight']:g}%)": d["id"] for d in domains}
    domain_label = st.selectbox("Domain", list(options.keys()))
    domain_id = options[domain_label]
    domain = db.get_domain(domain_id)

    col1, col2, col3 = st.columns(3)
    count = col1.number_input("Number of questions", min_value=1, max_value=30, value=5)
    difficulty = col2.selectbox("Difficulty", ["easy", "medium", "hard"], index=1)
    bloom_level = col3.selectbox(
        "Bloom's level",
        ["recall", "comprehension", "application", "analysis", "evaluation", "synthesis"],
        index=2,
    )

    if st.button("Generate"):
        with st.spinner("Generating questions with Claude..."):
            try:
                questions = ai_engine.generate_questions(
                    domain["name"], domain["description"], count, difficulty, bloom_level
                )
            except Exception as exc:
                st.error(f"Generation failed: {exc}")
                questions = []
        st.session_state["draft_questions"] = questions
        st.session_state["draft_domain_id"] = domain_id

    draft = st.session_state.get("draft_questions")
    if draft and st.session_state.get("draft_domain_id") == domain_id:
        st.subheader("Review generated questions")
        keep = []
        for i, question in enumerate(draft):
            with st.expander(f"Q{i + 1}: {question.get('question_text', '')[:80]}"):
                st.json(question)
                if st.checkbox("Keep this question", value=True, key=f"keep_{i}"):
                    keep.append(question)

        if st.button("Save kept questions to bank"):
            saved = 0
            for question in keep:
                opts = question.get("options", {})
                correct = question.get("correct_answer", "").strip().upper()
                if not opts.get("A") or not opts.get("B") or correct not in opts:
                    continue
                db.create_question(
                    domain_id,
                    question_text=question.get("question_text", ""),
                    option_a=opts.get("A"),
                    option_b=opts.get("B"),
                    option_c=opts.get("C"),
                    option_d=opts.get("D"),
                    correct_answer=correct,
                    difficulty=question.get("difficulty", difficulty),
                    bloom_level=question.get("bloom_level", bloom_level),
                )
                saved += 1
            st.success(f"Saved {saved} question(s) to the bank.")
            del st.session_state["draft_questions"]
            st.rerun()


def page_question_bank():
    st.header("4. Question Bank")
    job_id = _selected_job()
    if job_id is None:
        return

    questions = db.get_questions(job_id=job_id)
    if not questions:
        st.info("No questions saved yet for this job.")
        return

    domains_by_id = {d["id"]: d["name"] for d in db.get_domains(job_id)}

    for question in questions:
        domain_name = domains_by_id.get(question["domain_id"], "Unknown domain")
        with st.expander(f"[{domain_name}] {question['question_text'][:80]}"):
            text = st.text_area("Question text", value=question["question_text"], key=f"qt_{question['id']}")
            a = st.text_input("Option A", value=question["option_a"] or "", key=f"a_{question['id']}")
            b = st.text_input("Option B", value=question["option_b"] or "", key=f"b_{question['id']}")
            c = st.text_input("Option C", value=question["option_c"] or "", key=f"c_{question['id']}")
            d = st.text_input("Option D", value=question["option_d"] or "", key=f"d_{question['id']}")
            correct = st.selectbox(
                "Correct answer",
                ["A", "B", "C", "D"],
                index=["A", "B", "C", "D"].index(question["correct_answer"])
                if question["correct_answer"] in ["A", "B", "C", "D"]
                else 0,
                key=f"correct_{question['id']}",
            )
            col1, col2 = st.columns(2)
            if col1.button("Save changes", key=f"save_{question['id']}"):
                db.update_question(
                    question["id"],
                    question_text=text,
                    option_a=a,
                    option_b=b,
                    option_c=c or None,
                    option_d=d or None,
                    correct_answer=correct,
                )
                st.success("Updated.")
                st.rerun()
            if col2.button("Delete", key=f"delete_{question['id']}"):
                db.delete_question(question["id"])
                st.rerun()


def page_export():
    st.header("5. Export GIFT")
    job_id = _selected_job()
    if job_id is None:
        return

    job = db.get_job(job_id)
    questions = db.get_questions(job_id=job_id)
    if not questions:
        st.info("No questions saved yet for this job.")
        return

    gift_text = gift_export.questions_to_gift(questions, title_prefix=job["title"])
    errors = gift_export.validate_gift(gift_text)

    if errors:
        st.error("Validation issues found:")
        for error in errors:
            st.write(f"- {error}")
    else:
        st.success(f"Valid GIFT export: {len(questions)} question(s).")

    st.text_area("GIFT preview", value=gift_text, height=400)
    st.download_button(
        "Download .gift file",
        data=gift_text,
        file_name=f"{job['title'].replace(' ', '_')}.gift",
        mime="text/plain",
        disabled=bool(errors),
    )


def main():
    st.sidebar.title("Question Bank Builder")
    page = st.sidebar.radio("Navigate", PAGES)

    if page == "Job Description":
        page_job_description()
    elif page == "Domains":
        page_domains()
    elif page == "Generate Questions":
        page_generate_questions()
    elif page == "Question Bank":
        page_question_bank()
    elif page == "Export GIFT":
        page_export()


if __name__ == "__main__":
    main()
