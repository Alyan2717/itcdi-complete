"""
main.py — FastAPI application entry point

Run with:
    uvicorn schema_intelligence.main:app --reload --port 5001
"""
import logging
from contextlib import asynccontextmanager
from fastapi import FastAPI
# from .routers.matching import router
from .routers.matching_up import router
from .config import settings
from .services.scoping import get_model

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s  %(levelname)-8s  %(name)s  %(message)s"
)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Warm up sentence-transformer at startup
    # so the first request does not pay model load cost
    logger.info("Warming up sentence-transformer model...")
    get_model()
    logger.info(
        "Schema intelligence service ready. "
        "LLM_PROVIDER=%s", settings.llm_provider)
    yield
    logger.info("Shutting down schema intelligence service.")


app = FastAPI(
    title="ITCDI Schema Intelligence",
    version="0.1.0",
    lifespan=lifespan
)

app.include_router(router)


@app.get("/health")
async def health():
    return {
        "status":       "ok",
        "service":      "ITCDI Schema Intelligence",
        "llm_provider": settings.llm_provider
    }