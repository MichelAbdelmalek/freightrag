# FreightRAG retrieval evaluation (Python side). Pinned by tag and digest like the rest of the repo.
FROM ghcr.io/astral-sh/uv:0.13.7@sha256:96f40d3904aad8d0bd010539001d36c8e2188b981e1334471a90c4a1d93bb490 AS uv

FROM python:3.14.9-slim-bookworm@sha256:945c2676dc2b18e0c1f16879244122c57c0ffede60006bf3df159c507a31bcae AS tools
COPY --from=uv /uv /uvx /bin/
# Use the image's Python only (never download another), keep the venv outside the source tree.
ENV UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv \
    UV_LINK_MODE=copy \
    UV_COMPILE_BYTECODE=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /evals

FROM tools AS app
# Dependencies first (cached layer); --locked fails if uv.lock is out of date with pyproject.toml.
COPY pyproject.toml uv.lock .python-version ./
RUN uv sync --locked --no-install-project
COPY . .
RUN useradd --uid 1654 --no-create-home --shell /usr/sbin/nologin app \
    && mkdir -p /evals/results && chown app /evals/results
# Run the locked environment's Python directly: no uv (and no network or cache) needed at runtime.
ENV PATH="/opt/venv/bin:$PATH"
USER app
CMD ["python", "run_retrieval_eval.py"]
