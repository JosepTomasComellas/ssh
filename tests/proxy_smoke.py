"""Real Nginx HTTP/TLS/WebSocket smoke with an isolated dummy backend (not the app)."""
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
BACKEND = r'''
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import base64, hashlib, json
class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, *args): pass
    def do_GET(self):
        if self.headers.get("Upgrade", "").lower() == "websocket":
            key = self.headers["Sec-WebSocket-Key"]
            accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
            self.send_response(101)
            self.send_header("Upgrade", "websocket")
            self.send_header("Connection", "Upgrade")
            self.send_header("Sec-WebSocket-Accept", accept)
            self.end_headers()
            self.wfile.write(b"\x81\x02ok")
            self.wfile.flush()
            self.close_connection = True
        else:
            data = json.dumps({"host": self.headers.get("Host"), "proto": self.headers.get("X-Forwarded-Proto"), "client": self.headers.get("X-Forwarded-For")}).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
ThreadingHTTPServer(("0.0.0.0", 18080), Handler).serve_forever()
'''
CLIENT = r'''
import http.client, json, socket, ssl
ctx = ssl.create_default_context(cafile="/test/fullchain.pem")
connection = http.client.HTTPConnection("127.0.0.1", 80, timeout=5)
connection.request("GET", "/terminal", headers={"Host": "ssh.example.com"})
response = connection.getresponse()
assert response.status == 308 and response.getheader("Location") == "https://ssh.example.com/terminal"
connection.close()
with ctx.wrap_socket(socket.create_connection(("127.0.0.1", 443), timeout=5), server_hostname="ssh.example.com") as sock:
    sock.sendall(b"GET / HTTP/1.1\r\nHost: ssh.example.com\r\nX-Forwarded-For: 203.0.113.99\r\nX-Forwarded-Proto: http\r\nConnection: close\r\n\r\n")
    data = b""
    while chunk := sock.recv(8192): data += chunk
    headers, body = data.split(b"\r\n\r\n", 1)
    assert b"200 OK" in headers and b"Strict-Transport-Security" in headers
    result = json.loads(body)
    assert result == {"host": "ssh.example.com", "proto": "https", "client": "127.0.0.1"}, result
with ctx.wrap_socket(socket.create_connection(("127.0.0.1", 443), timeout=5), server_hostname="ssh.example.com") as sock:
    sock.sendall(b"GET /terminal HTTP/1.1\r\nHost: ssh.example.com\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n")
    data = b""
    while b"\x81\x02ok" not in data:
        chunk = sock.recv(8192)
        assert chunk, "WebSocket closed before the expected frame"
        data += chunk
    assert b"101 Switching Protocols" in data
    assert b"s3pPLMBiTxaQ9kYGzzhZRbK+xOo=" in data
print("Nginx: HTTPS, HTTP redirect, sanitized forwarding headers and WebSocket 101/frame passed")
'''


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], check=True, **kwargs)


with tempfile.TemporaryDirectory(prefix="webssh-proxy-") as directory:
    folder = Path(directory)
    (folder / "backend.py").write_text(BACKEND)
    (folder / "client.py").write_text(CLIENT)
    config = (ROOT / "deploy/nginx/webssh.conf.template").read_text().replace("@@DOMAIN@@", "ssh.example.com").replace("@@PORT@@", "18080")
    (folder / "nginx.conf").write_text(config)
    mount = f"type=bind,source={folder},target=/test"
    docker("run", "--rm", "--mount", mount, "alpine/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1", "-subj", "/CN=ssh.example.com", "-addext", "subjectAltName=DNS:ssh.example.com", "-keyout", "/test/privkey.pem", "-out", "/test/fullchain.pem", stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    suffix = uuid.uuid4().hex[:8]
    backend, nginx = "webssh-test-backend-" + suffix, "webssh-test-nginx-" + suffix
    try:
        docker("run", "-d", "--name", backend, "--mount", mount, "python:3.12-slim", "python", "/test/backend.py", stdout=subprocess.DEVNULL)
        docker("run", "-d", "--name", nginx, "--network", "container:" + backend,
               "--mount", f"type=bind,source={folder / 'nginx.conf'},target=/etc/nginx/conf.d/default.conf,readonly",
               "--mount", f"type=bind,source={folder},target=/etc/letsencrypt/live/ssh.example.com,readonly",
               "nginx:stable-alpine", stdout=subprocess.DEVNULL)
        docker("exec", nginx, "nginx", "-t")
        for attempt in range(10):
            result = subprocess.run(["docker", "exec", backend, "python", "/test/client.py"], capture_output=True, text=True, timeout=30)
            if result.returncode == 0:
                print(result.stdout.strip())
                break
            if attempt == 9:
                raise RuntimeError(result.stderr)
            time.sleep(1)
    finally:
        for container in (nginx, backend):
            subprocess.run(["docker", "rm", "-f", container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
