"""Collection guards for platforms without the macOS-only test targets.

``scripts/benchmark_mac.py`` imports MLX and ``resource`` at module scope, so
its test cannot even be imported off macOS. Everything else is guarded at
module level with ``pytest.importorskip``.
"""

import sys

collect_ignore = []

if sys.platform != "darwin":
    collect_ignore += [
        "test_benchmark.py",
        "test_stt_eval_cleaners.py",
    ]
