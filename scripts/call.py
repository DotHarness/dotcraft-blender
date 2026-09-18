"""Send one command to the newest live bridge.

    python scripts/call.py status
    python scripts/call.py execute --code "dc.result(dc.selected())"
    python scripts/call.py view --maxSize 640 --save .out/view.png
"""

import argparse
import base64
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from smoke import Client, sessions  # noqa: E402


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command")
    parser.add_argument("--code")
    parser.add_argument("--name")
    parser.add_argument("--jobId")
    parser.add_argument("--maxSize", type=int)
    parser.add_argument("--save")
    parsed, _ = parser.parse_known_args()

    found = sessions()
    if not found:
        raise SystemExit("no live bridge")
    client = Client(found[0])

    params = {
        key: value
        for key, value in (
            ("code", parsed.code),
            ("name", parsed.name),
            ("jobId", parsed.jobId),
            ("maxSize", parsed.maxSize),
        )
        if value is not None
    }
    response = client.call(parsed.command, **params)

    if parsed.save and response["ok"] and "base64" in response.get("result", {}):
        os.makedirs(os.path.dirname(parsed.save) or ".", exist_ok=True)
        with open(parsed.save, "wb") as handle:
            handle.write(base64.b64decode(response["result"]["base64"]))
        response["result"] = {
            key: value for key, value in response["result"].items() if key != "base64"
        }
        response["result"]["savedTo"] = parsed.save

    print(json.dumps(response, indent=2, default=str))
    return 0 if response["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
