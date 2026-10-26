from typing import Literal

from pydantic import BaseModel, Field

from .pipeline import Metrics
from .prompt import Profile

MAX_AUDIO_BYTES: int = 64 * 1024 * 1024

__all__ = [
    "AbortMsg",
    "ErrorMsg",
    "MAX_AUDIO_BYTES",
    "Profile",
    "ResultEnvelope",
    "StartMsg",
    "StopMsg",
]


class StartMsg(BaseModel):
    type: Literal["start"]
    app_name: str
    window_title: str
    profile: Profile = "default"
    selected_text: str = ""
    bypass_llm: bool = False


class StopMsg(BaseModel):
    type: Literal["stop"]


class AbortMsg(BaseModel):
    type: Literal["abort"]


class ResultEnvelope(BaseModel):
    type: Literal["result"] = "result"
    raw_text: str
    final_text: str
    fast_path: bool
    metrics: Metrics
    warnings: list[str] = Field(default_factory=list)


class ErrorMsg(BaseModel):
    type: Literal["error"] = "error"
    code: str
    message: str
    retryable: bool = False
