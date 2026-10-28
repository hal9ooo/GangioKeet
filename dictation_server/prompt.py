import hashlib
from functools import lru_cache
from pathlib import Path
from typing import Literal

from .config import GlossaryConfig

Profile = Literal["code", "terminal", "chat", "email", "document", "default"]
ALLOWED_PROFILES: tuple[Profile, ...] = (
    "code", "terminal", "chat", "email", "document", "default",
)

_PROMPT_PATH = (
    Path(__file__).resolve().parents[1]
    / "shared"
    / "prompts"
    / "conservative-local-v3.txt"
)


@lru_cache(maxsize=1)
def load_canonical_prompt() -> str:
    """Return the exact system prompt bytes, preserving CRLF line endings.

    Windows renders the C# raw string literal with CRLF; the canonical
    artifact uses the same bytes so the prompt hash and the model input are
    identical on both platforms.
    """
    return _PROMPT_PATH.read_bytes().decode("utf-8")


def prompt_sha256() -> str:
    return hashlib.sha256(load_canonical_prompt().encode("utf-8")).hexdigest()


def build_system_prompt() -> str:
    """Return the canonical system prompt without injecting the glossary.

    The glossary belongs in the user message, exactly like the Windows
    cleaner.  Injecting it here would change the prompt and its hash.
    """
    return load_canonical_prompt()


def _escape(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def _glossary_entries(glossary: GlossaryConfig) -> dict[str, str]:
    entries: dict[str, str] = {}
    for term in glossary.canonical_terms:
        entries[term.strip()] = term.strip()
    for spoken, canonical in glossary.phonetic_overrides.items():
        entries[spoken] = canonical
    return {key: value for key, value in entries.items() if key}


def build_user_prompt(
    raw_text: str, profile: Profile, glossary: GlossaryConfig
) -> str:
    if profile not in ALLOWED_PROFILES:
        raise ValueError(f"unsupported profile: {profile!r}")
    terms = "\n".join(
        f"{_escape(key)} -> {_escape(value)}"
        for key, value in _glossary_entries(glossary).items()
    )
    return (
        f"<glossary>\n{terms}\n</glossary>\n"
        f"<profile>{profile}</profile>\n"
        f"<raw_dictation>{_escape(raw_text)}</raw_dictation>"
    )
