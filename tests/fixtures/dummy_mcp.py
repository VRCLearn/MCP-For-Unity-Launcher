"""Local process fixture; no Unity or uv download needed."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int)
parser.add_argument("--mode", default="healthy")
parser.add_argument("--pid-file")
parser.add_argument("--child-file")
parser.add_argument("--starts-file")
parser.add_argument("--echo-file")
parser.add_argument("--value")
args = parser.parse_args()
if args.mode == "sleep":
    if args.pid_file:
        Path(args.pid_file).write_text(str(os.getpid()))
    time.sleep(120)
    sys.exit(0)
if args.starts_file:
    with open(args.starts_file, "a", encoding="utf-8") as stream:
        stream.write(str(os.getpid()) + "\n")
if args.pid_file:
    Path(args.pid_file).write_text(str(os.getpid()))
if args.echo_file:
    Path(args.echo_file).write_text(args.value, encoding="utf-8")
if args.child_file:
    subprocess.Popen([sys.executable, __file__, "--mode", "sleep", "--pid-file", args.child_file])
if args.mode == "crash":
    sys.exit(3)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if args.mode == "hang":
            time.sleep(120)
            return
        payload = {"status": "healthy", "message": "MCP for Unity server is running"}
        if args.mode == "unrelated":
            payload["message"] = "some other server"
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
