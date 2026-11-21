# FreightRAG retrieval evaluation (Python side). Pinned by tag and digest like the rest of the repo.
FROM ghcr.io/astral-sh/uv:0.13.9@sha256:d3b601f0c7b118bc1753febf7c41cc80981df678445e0254284eecf84db79a7d AS uv

# Python must match requires-python in pyproject.toml (==3.14.*); Dependabot ignores minor bumps (see dependabot.yml).
FROM python:3.14.10-slim-bookworm@sha256:3fe1a0b6c2d6b2ebb572906cdabeca75aeb558c0c17db5ffefbcf8094871c32b AS tools
COPY --from=uv /uv /uvx /bin/
# Use the image's Python only (never download another), keep the venv outside the source tree.
ENV UV_PYTHON_DOWNLOADS=never \
    UV_PROJECT_ENVIRONMENT=/opt/venv \
    UV_LINK_MODE=copy \
    UV_COMPILE_BYTECODE=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    # DeepEval (faithfulness judge): no telemetry, no automatic reading of .env files, no writes to disk.
    DEEPEVAL_TELEMETRY_OPT_OUT=1 \
    DEEPEVAL_DISABLE_DOTENV=1 \
    DEEPEVAL_FILE_SYSTEM=READ_ONLY
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
