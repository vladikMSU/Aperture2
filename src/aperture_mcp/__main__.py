import argparse

from .binding import Target
from .server import create_server


def main():
    parser = argparse.ArgumentParser(description="Aperture live Excel MCP (captured target only)")
    parser.add_argument("--context", required=True, help="Immutable VSTO target JSON")
    args = parser.parse_args()
    create_server(Target.load(args.context)).run(transport="stdio", show_banner=False)


if __name__ == "__main__":
    main()
