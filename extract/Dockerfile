# FreightRAG PDF extractor (Python): reads the text layer of a PDF and runs OCR (Tesseract) on scanned pages.
# Same pinned uv image and Python base as answer/, so the layers are shared on disk.
FROM ghcr.io/astral-sh/uv:0.13.9@sha256:d3b601f0c7b118bc1753febf7c41cc80981df678445e0254284eecf84db79a7d AS uv

FROM python:3.14.10-slim-bookworm@sha256:3fe1a0b6c2d6b2ebb572906cdabeca75aeb558c0c17db5ffefbcf8094871c32b AS tools
COPY --from=uv /uv /uvx /bin/
ENV UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv \
    UV_LINK_MODE=copy \
    UV_COMPILE_BYTECODE=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /extract

FROM tools AS app
# Tesseract from Debian bookworm, pinned to the exact packaged versions (released in 2023, so well past the two-week
# rule). A security update that changes a version breaks this build on purpose: bump the pin deliberately.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr=5.3.0-2 tesseract-ocr-eng=1:4.1.0-2 \
 && rm -rf /var/lib/apt/lists/*
COPY pyproject.toml uv.lock .python-version ./
RUN uv sync --locked --no-dev --no-install-project
COPY . .
RUN useradd --uid 1654 --no-create-home --shell /usr/sbin/nologin app
ENV PATH="/opt/venv/bin:$PATH" \
    OMP_THREAD_LIMIT=1 \
    TMPDIR=/tmp
USER app
EXPOSE 8091
CMD ["uvicorn", "--factory", "freightrag_extract.app:create_app", "--host", "0.0.0.0", "--port", "8091", "--no-access-log", "--workers", "1"]

# Tests: adds the dev and fixture packages and a font to draw synthetic scans. Not part of the runtime image.
FROM app AS test
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
RUN uv sync --locked --group dev --group fixtures --no-install-project
COPY --from=data . /data/
USER app
CMD ["pytest", "-q", "-p", "no:cacheprovider"]
