import argparse
import asyncio
import logging
import os

import uvicorn
from fastapi import FastAPI

from .auth import ApiKeyPolicy
from .config import AppConfig, load_config
from .llm import LLMCleaner
from .pipeline import Pipeline
from .prompt import build_system_prompt
from .server import create_app
from .stt import ParakeetSTT

logger = logging.getLogger(__name__)


def build_app(
    cfg: AppConfig,
    stt: ParakeetSTT | None = None,
    llm: LLMCleaner | None = None,
) -> FastAPI:
    # Resolve auth before constructing or loading any model: a configured
    # but unavailable API key must fail startup without touching engines.
    auth = ApiKeyPolicy.from_environment(
        cfg.security.api_key_env, os.environ
    )

    if stt is None:
        stt = ParakeetSTT(cfg.server.stt_model)
    if llm is None:
        llm = LLMCleaner(cfg.server.llm_model)

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
    parser = argparse.ArgumentParser(
        description="On-device MLX dictation server"
    )
    parser.add_argument("--config", default="./config.yaml")
    args = parser.parse_args(argv)

    cfg = load_config(args.config)
    app = build_app(cfg)
    asyncio.run(serve(cfg, app, app.state.pipeline))


if __name__ == "__main__":
    main()
