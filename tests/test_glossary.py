from dictation_server.glossary import normalize_canonical_terms

CANONICAL_TERMS = [
    "Proxmox VE",
    "FastAPI",
    "CodeIgniter 4",
    "Docker",
    "MLX",
    "llama.cpp",
    "Nginx Proxy Manager",
    "GitHub",
]


def test_canonical_casing_is_applied_case_insensitively():
    assert (
        normalize_canonical_terms(
            "uso docker e github ogni giorno", CANONICAL_TERMS
        )
        == "uso Docker e GitHub ogni giorno"
    )


def test_acronyms_use_canonical_casing():
    assert normalize_canonical_terms("mlx e' veloce", CANONICAL_TERMS) == \
        "MLX e' veloce"


def test_no_replacement_inside_a_larger_word():
    assert (
        normalize_canonical_terms("dockerman e docker", CANONICAL_TERMS)
        == "dockerman e Docker"
    )
    assert (
        normalize_canonical_terms("fastapi_client", CANONICAL_TERMS)
        == "fastapi_client"
    )


def test_punctuation_adjacent_to_terms_is_preserved():
    assert (
        normalize_canonical_terms("(docker), poi llama.cpp!", CANONICAL_TERMS)
        == "(Docker), poi llama.cpp!"
    )
    assert normalize_canonical_terms("MLX.", CANONICAL_TERMS) == "MLX."


def test_multi_word_terms_and_spacing_outside_matches_are_preserved():
    assert (
        normalize_canonical_terms("  proxmox ve,  codeigniter 4.  ",
                                  CANONICAL_TERMS)
        == "  Proxmox VE,  CodeIgniter 4.  "
    )


def test_phrase_whitespace_inside_a_match_is_normalized():
    assert (
        normalize_canonical_terms("nginx   proxy manager", CANONICAL_TERMS)
        == "Nginx Proxy Manager"
    )


def test_longest_term_wins_before_shorter_overlapping_term():
    terms = ["Proxy Manager", "Nginx Proxy Manager", "Docker",
             "Docker Compose"]
    assert (
        normalize_canonical_terms(
            "nginx proxy manager su docker compose", terms
        )
        == "Nginx Proxy Manager su Docker Compose"
    )


def test_dotted_term_matches_as_a_whole_word():
    assert normalize_canonical_terms("LLAMA.CPP", CANONICAL_TERMS) == \
        "llama.cpp"
    assert normalize_canonical_terms("(llama.cpp)", CANONICAL_TERMS) == \
        "(llama.cpp)"


def test_phonetic_spellings_are_not_substituted():
    assert normalize_canonical_terms("fast a pi", CANONICAL_TERMS) == \
        "fast a pi"
    assert normalize_canonical_terms("prossimo ve", CANONICAL_TERMS) == \
        "prossimo ve"


def test_empty_inputs_are_unchanged():
    assert normalize_canonical_terms("", CANONICAL_TERMS) == ""
    assert normalize_canonical_terms("uso docker", []) == "uso docker"


def test_already_canonical_text_is_stable():
    text = "Docker, GitHub e Proxmox VE"
    assert normalize_canonical_terms(text, CANONICAL_TERMS) == text


def test_code_and_terminal_profiles_do_not_recase_commands():
    assert (
        normalize_canonical_terms("docker compose up", CANONICAL_TERMS, "code")
        == "docker compose up"
    )
    assert (
        normalize_canonical_terms(
            "docker compose up", CANONICAL_TERMS, "terminal"
        )
        == "docker compose up"
    )
    assert (
        normalize_canonical_terms("docker", CANONICAL_TERMS, "chat")
        == "Docker"
    )
