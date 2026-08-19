"""Extract plain text from uploaded job description files (docx/pdf/xlsx)."""

from pathlib import Path

import openpyxl
from docx import Document
from PyPDF2 import PdfReader


def extract_text_from_docx(file_path_or_buffer):
    document = Document(file_path_or_buffer)
    parts = [paragraph.text for paragraph in document.paragraphs if paragraph.text.strip()]
    for table in document.tables:
        for row in table.rows:
            cells = [cell.text.strip() for cell in row.cells if cell.text.strip()]
            if cells:
                parts.append(" | ".join(cells))
    return "\n".join(parts)


def extract_text_from_pdf(file_path_or_buffer):
    reader = PdfReader(file_path_or_buffer)
    parts = [page.extract_text() or "" for page in reader.pages]
    return "\n".join(part for part in parts if part.strip())


def extract_text_from_xlsx(file_path_or_buffer):
    workbook = openpyxl.load_workbook(file_path_or_buffer, data_only=True)
    parts = []
    for sheet in workbook.worksheets:
        for row in sheet.iter_rows(values_only=True):
            cells = [str(cell).strip() for cell in row if cell is not None and str(cell).strip()]
            if cells:
                parts.append(" | ".join(cells))
    return "\n".join(parts)


EXTRACTORS = {
    ".docx": extract_text_from_docx,
    ".pdf": extract_text_from_pdf,
    ".xlsx": extract_text_from_xlsx,
}


def extract_text(file_path_or_buffer, filename=None):
    """Dispatch extraction based on file extension.

    `filename` is required when `file_path_or_buffer` is a file-like object
    (e.g. a Streamlit UploadedFile) that has no usable suffix on its own.
    """
    name = filename or getattr(file_path_or_buffer, "name", None) or str(file_path_or_buffer)
    suffix = Path(name).suffix.lower()
    extractor = EXTRACTORS.get(suffix)
    if extractor is None:
        raise ValueError(f"Unsupported file type: {suffix or 'unknown'}")
    return extractor(file_path_or_buffer)
