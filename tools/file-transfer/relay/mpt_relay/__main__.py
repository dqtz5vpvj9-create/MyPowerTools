"""``python3 -m mpt_relay`` entry point."""

from __future__ import annotations

import sys

from . import __version__
from .config import Config, ConfigError
from .service import serve_forever


def main(argv=None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    if "--version" in args:
        print(__version__)
        return 0
    try:
        config = Config.load(args)
    except ConfigError as error:
        print(f"mpt-relay configuration error: {error}", file=sys.stderr)
        return 2
    if "--print-config" in args:
        print(config.to_json())
        return 0
    return serve_forever(config)


if __name__ == "__main__":
    raise SystemExit(main())
