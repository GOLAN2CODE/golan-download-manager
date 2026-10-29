"""Local HTTP server for GDM's engine tests.

/<name>                 file with Range + ETag/If-Range support
/norange/<name>         ignores Range (200 only)
/limited/<name>         only 2 simultaneous ranged requests, the rest get 503
/nocookie/<name>        403 when a Cookie header is present
/redirect?to=<url>      302 to <url>
/stats, /reset          max simultaneous connections seen
/site/...               small website for the grabber test (files in served/site)
/expiring/<token>/<name> paced file that answers 403 (and drops open transfers) once the token is revoked
/revoke?token=<t>       expire a token; /tokbytes?token=<t> bytes served under it
"""
import hashlib, http.server, os, re, sys, threading, time, urllib.parse

ROOT = sys.argv[1]
PORT = int(sys.argv[2])
lock = threading.Lock()
state = {"active": 0, "max": 0, "limited": 0}
tags = {}  # ?t=<tag> -> [active, max]
revoked = set()
tok_bytes = {}


def etag_for(path):
    st = os.stat(path)
    return '"' + hashlib.md5(f"{st.st_size}-{st.st_mtime_ns}".encode()).hexdigest() + '"'


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def send_empty(self, code, headers=None):
        self.send_response(code)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):
        tag = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query).get("t", [""])[0]
        with lock:
            state["active"] += 1
            state["max"] = max(state["max"], state["active"])
            if tag:
                t = tags.setdefault(tag, [0, 0])
                t[0] += 1
                t[1] = max(t[1], t[0])
        try:
            self.handle_get()
        finally:
            with lock:
                state["active"] -= 1
                if tag:
                    tags[tag][0] -= 1

    def handle_get(self):
        url = urllib.parse.urlparse(self.path)
        path = url.path
        if path == "/stats":
            tag = urllib.parse.parse_qs(url.query).get("t", [""])[0]
            body = str(tags.get(tag, [0, 0])[1] if tag else state["max"]).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path == "/reset":
            state["max"] = 0
            return self.send_empty(200)
        if path in ("/revoke", "/tokbytes"):
            tok = urllib.parse.parse_qs(url.query).get("token", [""])[0]
            if path == "/revoke":
                with lock:
                    revoked.add(tok)
                return self.send_empty(200)
            body = str(tok_bytes.get(tok, 0)).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        m_exp = re.match(r"^/expiring/([^/]+)/(.+)$", path)
        if m_exp:
            tok = m_exp.group(1)
            if tok in revoked:
                return self.send_empty(403)
            return self.serve_file(os.path.join(ROOT, os.path.basename(m_exp.group(2))), "", token=tok)
        if path == "/redirect":
            to = urllib.parse.parse_qs(url.query)["to"][0]
            return self.send_empty(302, {"Location": to})
        if path == "/huge-range":
            self.send_response(206)
            self.send_header("Content-Range", "bytes 0-0/109951162777600")
            self.send_header("Content-Length", "1")
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Disposition", 'attachment; filename="huge.bin"')
            self.end_headers()
            self.wfile.write(b"x")
            return
        if path == "/overflow":
            self.send_response(200)
            self.send_header("Content-Range", "bytes 0-1023/1024")
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", 'attachment; filename="overflow.bin"')
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            try:
                self.wfile.write(b"A" * 65536)
            except Exception:
                pass
            return
        if path == "/unbounded":
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", 'attachment; filename="unbounded.bin"')
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            chunk = b"B" * 65536
            for _ in range(32):
                try:
                    self.wfile.write(chunk)
                except Exception:
                    return
            return

        mode = ""
        for m in ("norange", "limited", "nocookie", "requirecookie", "auth", "noauth"):
            if path.startswith(f"/{m}/"):
                mode = m
                path = path[len(m) + 1:]
        if path.startswith("/site/"):
            return self.serve_file(os.path.join(ROOT, "site", path[6:] or "index.html"), "", html=path.endswith(".html") or path == "/site/")

        if mode == "nocookie" and self.headers.get("Cookie"):
            return self.send_empty(403)
        if mode == "requirecookie" and self.headers.get("Cookie") != "session=secret":
            return self.send_empty(403)
        if mode == "auth" and self.headers.get("Authorization") != "Basic dXNlcjpwYXNz":
            return self.send_empty(401, {"WWW-Authenticate": 'Basic realm="test"'})
        if mode == "noauth" and self.headers.get("Authorization"):
            return self.send_empty(403)
        if mode == "limited" and self.headers.get("Range"):
            with lock:
                if state["limited"] >= 2:
                    return self.send_empty(503, {"Retry-After": "1"})
                state["limited"] += 1
            try:
                return self.serve_file(os.path.join(ROOT, os.path.basename(path)), mode)
            finally:
                with lock:
                    state["limited"] -= 1
        return self.serve_file(os.path.join(ROOT, os.path.basename(path)), mode)

    def serve_file(self, p, mode, html=False, token=None):
        if not os.path.isfile(p):
            return self.send_empty(404)
        size = os.path.getsize(p)
        etag = etag_for(p)
        start, end = 0, size - 1
        rng = self.headers.get("Range", "") if mode != "norange" else ""
        if_range = self.headers.get("If-Range")
        m = re.match(r"bytes=(\d+)-(\d*)", rng)
        if m and (not if_range or if_range == etag):
            start = int(m.group(1))
            end = int(m.group(2)) if m.group(2) else size - 1
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        else:
            self.send_response(200)
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Content-Type", "text/html" if html else "application/octet-stream")
        if not html:
            self.send_header("Content-Disposition", f'attachment; filename="{os.path.basename(p)}"')
        if "lm=2020" in self.path:
            self.send_header("Last-Modified", "Wed, 21 Oct 2020 07:28:00 GMT")
        if mode != "norange":
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("ETag", etag)
        self.end_headers()
        with open(p, "rb") as f:
            f.seek(start)
            left = end - start + 1
            while left > 0:
                chunk = f.read(min(65536, left))
                if not chunk:
                    break
                if token is not None and token in revoked:
                    self.close_connection = True
                    return
                try:
                    self.wfile.write(chunk)
                except Exception:
                    return
                left -= len(chunk)
                if token is not None:
                    with lock:
                        tok_bytes[token] = tok_bytes.get(token, 0) + len(chunk)
                time.sleep(0.05 if token is not None else 0.002)


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
