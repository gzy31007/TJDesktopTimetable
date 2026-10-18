"""THROWAWAY #19: serve only this prototype directory, loopback only; Ctrl+C stops it."""
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import argparse

parser = argparse.ArgumentParser(description="抛弃式整表纵向可见原型，不启动或修改 WinUI 应用")
parser.add_argument("--port", type=int, default=8191)
args = parser.parse_args()
root = Path(__file__).resolve().parent
server = ThreadingHTTPServer(("127.0.0.1", args.port), partial(SimpleHTTPRequestHandler, directory=str(root)))
print(f"Prototype only: http://127.0.0.1:{args.port}/?variant=A", flush=True)
server.serve_forever()
