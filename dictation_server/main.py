import argparse
import asyncio
import logging
import os

import uvicorn
from fastapi import FastAPI

from .auth import ApiKeyPolicy
from .config import AppConfig, load_config
from .engines import LLM, STT
from .pipeline import Pipeline
from .prompt import build_system_prompt
from .server import create_app

logger = logging.getLogger(__name__)

_APP_LOG_HANDLER_ATTR = "_dictation_server_handler"


def _build_stt(cfg: AppConfig) -> STT:
    if cfg.server.backend == "onnx":
        from .stt_onnx import OnnxParakeetSTT

        assert cfg.server.stt_onnx_dir is not None  # validated by ServerConfig
        return OnnxParakeetSTT(cfg.server.stt_onnx_dir)

    from .stt import ParakeetSTT

    return ParakeetSTT(cfg.server.stt_model)


def _build_llm(cfg: AppConfig) -> LLM:
    if cfg.server.llm_model is None:
        from .llm_null import NullLLM

        return NullLLM()

    from .llm import LLMCleaner

    return LLMCleaner(cfg.server.llm_model)


def configure_logging(level: int = logging.INFO) -> None:
    """Enable application logs for the shipped ``python -m`` entrypoint.

    Uvicorn only configures its own ``uvicorn*`` loggers, so without this the
    ``dictation_server`` namespace would inherit root's ``WARNING`` level and
    suppress every INFO readiness/scheduler state transition. The helper is
    idempotent: repeated calls never add a second handler.
    """
    app_logger = logging.getLogger("dictation_server")
    app_logger.setLevel(level)
    for handler in app_logger.handlers:
        if getattr(handler, _APP_LOG_HANDLER_ATTR, False):
            handler.setLevel(level)
            return
    handler = logging.StreamHandler()
    handler.setLevel(level)
    handler.setFormatter(
        logging.Formatter(
            "%(asctime)s %(levelname)s %(name)s: %(message)s"
        )
    )
    setattr(handler, _APP_LOG_HANDLER_ATTR, True)
    app_logger.addHandler(handler)


def build_app(
    cfg: AppConfig,
    stt: STT | None = None,
    llm: LLM | None = None,
) -> FastAPI:
    # Resolve auth before constructing or loading any model: a configured
    # but unavailable API key must fail startup without touching engines.
    auth = ApiKeyPolicy.from_environment(
        cfg.security.api_key_env, os.environ
    )

    if stt is None:
        stt = _build_stt(cfg)
    if llm is None:
        llm = _build_llm(cfg)

    stt.load()
    llm.load()
    llm.freeze(build_system_prompt(cfg.glossary))

    return create_app(cfg, Pipeline(stt, llm, cfg), auth=auth)


async def serve(
    cfg: AppConfig,
    app: FastAPI,
    pipeline: Pipeline,
    uvicorn_server: uvicorn.Server | None = None,
) -> None:
    state = getattr(app, "state", None)
    scheduler = getattr(state, "scheduler", None)
    try:
        if scheduler is not None:
            await scheduler.start()
        await pipeline.warm_up()
        if state is not None:
            state.ready = True
        logger.info("warm-up complete ready=true")
        if uvicorn_server is None:
            uvicorn_server = uvicorn.Server(
                uvicorn.Config(
                    app,
                    host=cfg.server.host,
                    port=cfg.server.port,
                    ws_max_size=cfg.limits.max_audio_bytes,
                )
            )
        await uvicorn_server.serve()
    finally:
        # Flip readiness before draining so probes see 503 during shutdown.
        if state is not None:
            state.ready = False
        logger.info("shutdown ready=false")
        if scheduler is not None:
            await scheduler.close()


def main(argv: list[str] | None = None) -> None:
    configure_logging()
    parser = argparse.ArgumentParser(
        description="On-device dictation server (MLX or sherpa-onnx)"
    )
    parser.add_argument("--config", default="./config.yaml")
    parser.add_argument(
        "--recording",
        action="store_true",
        help=(
            "store completed dictations as WAV + JSONL for local voice "
            "benchmarks (off by default)"
        ),
    )
    args = parser.parse_args(argv)

    cfg = load_config(args.config)
    if args.recording:
        cfg.recording.enabled = True
    app = build_app(cfg)
    asyncio.run(serve(cfg, app, app.state.pipeline))


if __name__ == "__main__":
    main()
