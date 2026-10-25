import argparse
import asyncio

import uvicorn
from fastapi import FastAPI

from .config import AppConfig, load_config
from .llm import LLMCleaner
from .pipeline import Pipeline
from .prompt import build_system_prompt
from .protocol import MAX_AUDIO_BYTES
from .server import create_app
from .stt import ParakeetSTT


def build_app(
    cfg: AppConfig,
    stt: ParakeetSTT | None = None,
    llm: LLMCleaner | None = None,
) -> FastAPI:
    if stt is None:
        stt = ParakeetSTT(cfg.server.stt_model)
    if llm is None:
        llm = LLMCleaner(cfg.server.llm_model)

    stt.load()
    llm.load()
    llm.freeze(build_system_prompt(cfg.glossary))

    return create_app(cfg, Pipeline(stt, llm, cfg))


async def serve(
    cfg: AppConfig,
    app: FastAPI,
    pipeline: Pipeline,
    uvicorn_server: uvicorn.Server | None = None,
) -> None:
    await pipeline.warm_up()
    server = uvicorn_server or uvicorn.Server(
        uvicorn.Config(
            app,
            host=cfg.server.host,
            port=cfg.server.port,
            ws_max_size=MAX_AUDIO_BYTES,
        )
    )
    await server.serve()


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(
        description="On-device MLX dictation server"
    )
    parser.add_argument("--config", default="./config.yaml")
    args = parser.parse_args(argv)

    cfg = load_config(args.config)
    app = build_app(cfg)
    asyncio.run(serve(cfg, app, app.state.pipeline))
