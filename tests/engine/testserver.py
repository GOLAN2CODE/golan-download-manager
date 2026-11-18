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
import gzip, hashlib, http.server, os, re, sys, threading, time, urllib.parse, zlib

ROOT = sys.argv[1]
PORT = int(sys.argv[2])
lock = threading.Lock()
state = {"active": 0, "max": 0, "limited": 0}
tags = {}  # ?t=<tag> -> [active, max]
revoked = set()
tok_bytes = {}
burst_barriers = {}
burst_done = set()
multi_exp_seq = {}
rf_hits = {}
rf_holds = {}
rf_drop_once = {}


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

    def send_bytes(self, code, body, content_type="application/octet-stream", headers=None):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_POST(self):
        clen = int(self.headers.get("Content-Length", "0") or "0")
        if clen > 0:
            self.rfile.read(clen)
        url = urllib.parse.urlparse(self.path)
        m_fh = re.match(r"^/fakehost/([^/]+)(/.*)?$", url.path)
        if m_fh:
            return self.handle_fakehost(m_fh.group(1).lower(), m_fh.group(2) or "/", url.query, "POST")
        return self.send_empty(405)

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
        m_mexp = re.match(r"^/multi-expiring/([^/]+)/(.+)$", path)
        if m_mexp:
            seq, fname = m_mexp.group(1), os.path.basename(m_mexp.group(2))
            with lock:
                n = multi_exp_seq.get(seq, 0) + 1
                multi_exp_seq[seq] = n
            return self.send_empty(302, {"Location": f"/expiring/{seq}_{n}/{fname}"})
        if path == "/html-page":
            body = b"<html><body><h1>Sharing Landing Page</h1></body></html>"
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path == "/download":
            qs = urllib.parse.parse_qs(url.query)
            dmode = qs.get("mode", ["error"])[0]
            if dmode == "confirm":
                body = (
                    b'<html><body><span class="uc-name-size"><a href="/x">confirmed_report.bin</a> (64K)</span>'
                    b'<form id="download-form" action="/download-confirmed" method="get">'
                    b'<input type="hidden" name="id" value="abc123">'
                    b'<input type="hidden" name="confirm" value="t">'
                    b'</form></body></html>'
                )
            elif dmode == "refresh":
                body = b'<html><head><meta http-equiv="refresh" content="0; url=/audit-payload.bin"></head><body>Redirecting...</body></html>'
            elif dmode == "cross":
                body = b'<html><head><meta http-equiv="refresh" content="0; url=http://evil.invalid/audit-payload.bin"></head></html>'
            else:
                body = b"<html><body><h1>Sign in required</h1><p>Please log in</p></body></html>"
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path == "/download-confirmed":
            qs = urllib.parse.parse_qs(url.query)
            if qs.get("id") != ["abc123"] or qs.get("confirm") != ["t"]:
                return self.send_empty(403)
            return self.serve_file(os.path.join(ROOT, "audit-payload.bin"), "", disp_name="confirmed_report.bin")
        m_def = re.match(r"^/deflate-(zlib|raw)/(.+)$", path)
        if m_def:
            kind, fname = m_def.group(1), os.path.basename(m_def.group(2))
            fp = os.path.join(ROOT, fname)
            if not os.path.isfile(fp):
                return self.send_empty(404)
            with open(fp, "rb") as f:
                raw = f.read()
            if kind == "zlib":
                comp = zlib.compress(raw)
            else:
                co = zlib.compressobj(6, zlib.DEFLATED, -zlib.MAX_WBITS)
                comp = co.compress(raw) + co.flush()
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Encoding", "deflate")
            self.send_header("Content-Disposition", f'attachment; filename="{fname}"')
            self.send_header("Content-Length", str(len(comp)))
            self.end_headers()
            self.wfile.write(comp)
            return
        m_gz = re.match(r"^/gzip-archive/(.+)$", path)
        if m_gz:
            fname = os.path.basename(m_gz.group(1))
            return self.serve_file(os.path.join(ROOT, fname), "", content_type="application/gzip", content_encoding="gzip")
        m_ref = re.match(r"^/require-referer/(.+)$", path)
        if m_ref:
            ref = self.headers.get("Referer", "")
            if ref != f"http://127.0.0.1:{PORT}/" or self.headers.get("Cookie") or self.headers.get("Authorization"):
                return self.send_empty(403)
            return self.serve_file(os.path.join(ROOT, os.path.basename(m_ref.group(1))), "")
        m_rr = re.match(r"^/reject-range/(\d+)/(.+)$", path)
        if m_rr:
            code, fname = int(m_rr.group(1)), os.path.basename(m_rr.group(2))
            if self.headers.get("Range"):
                return self.send_empty(code)
            return self.serve_file(os.path.join(ROOT, fname), "norange")
        m_b429 = re.match(r"^/burst429/([^/]+)/(.+)$", path)
        if m_b429:
            btag, fname = m_b429.group(1), os.path.basename(m_b429.group(2))
            rng = self.headers.get("Range", "")
            if rng and rng != "bytes=0-0":
                fire = False
                with lock:
                    if btag not in burst_done:
                        bar = burst_barriers.setdefault(btag, threading.Barrier(4, timeout=1.5))
                        fire = True
                if fire:
                    try:
                        bar.wait()
                    except threading.BrokenBarrierError:
                        pass
                    with lock:
                        burst_done.add(btag)
                    return self.send_empty(429, {"Retry-After": "1"})
            return self.serve_file(os.path.join(ROOT, fname), "")
        if path == "/gzip-html-page":
            html_bytes = b'<html><body><a href="/site/grabbed_doc.pdf">Grabbed PDF</a></body></html>'
            comp = gzip.compress(html_bytes)
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Encoding", "gzip")
            self.send_header("Content-Length", str(len(comp)))
            self.end_headers()
            self.wfile.write(comp)
            return
        if path == "/redirect":
            to = urllib.parse.parse_qs(url.query)["to"][0]
            return self.send_empty(302, {"Location": to})
        if path == "/short/stream":
            return self.send_empty(302, {"Location": "/hls/master.m3u8"})
        if path == "/interstitial/button":
            body = (
                b'<html><body><h1>Ready to download</h1>'
                b'<a class="btn download-btn" href="#" data-download-url="/audit-payload.bin">Start Download</a>'
                b'</body></html>'
            )
            return self.send_bytes(200, body, "text/html; charset=utf-8")
        if path.startswith("/hls/"):
            return self.handle_hls(path[5:])
        if path.startswith("/dash/"):
            return self.handle_dash(path[6:])
        if path == "/rf-ctl":
            return self.handle_rf_ctl(url.query)
        m_rf = re.match(r"^/rf/([^/]+)/(.+)$", path)
        if m_rf:
            return self.handle_rf_file(m_rf.group(1), m_rf.group(2), url.query)
        m_fh = re.match(r"^/fakehost/([^/]+)(/.*)?$", path)
        if m_fh:
            return self.handle_fakehost(m_fh.group(1).lower(), m_fh.group(2) or "/", url.query, "GET")
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

    def serve_file(self, p, mode, html=False, token=None, disp_name=None, content_type=None, content_encoding=None):
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
        self.send_header("Content-Type", content_type or ("text/html" if html else "application/octet-stream"))
        if content_encoding:
            self.send_header("Content-Encoding", content_encoding)
        if not html:
            self.send_header("Content-Disposition", f'attachment; filename="{disp_name or os.path.basename(p)}"')
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

    def handle_hls(self, name):
        m3u_type = "application/vnd.apple.mpegurl"
        if name == "master.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:4\n"
                "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n"
                "360p.m3u8\n"
                "#EXT-X-STREAM-INF:BANDWIDTH=4500000,RESOLUTION=1920x1080\n"
                "1080p.m3u8\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "360p.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:3\n"
                "#EXT-X-TARGETDURATION:4\n"
                "#EXTINF:4.0,\n"
                "seg360.ts\n"
                "#EXT-X-ENDLIST\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "1080p.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:4\n"
                "#EXT-X-TARGETDURATION:4\n"
                "#EXT-X-MEDIA-SEQUENCE:10\n"
                '#EXT-X-KEY:METHOD=AES-128,URI="/hls/enc.key",IV=0x000102030405060708090a0b0c0d0e0f\n'
                "#EXTINF:4.0,\n"
                "seg0.enc\n"
                '#EXT-X-KEY:METHOD=AES-128,URI="/hls/enc.key"\n'
                "#EXTINF:4.0,\n"
                "seg1.enc\n"
                "#EXT-X-ENDLIST\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "byterange.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:4\n"
                "#EXT-X-TARGETDURATION:4\n"
                "#EXT-X-BYTERANGE:1024@0\n"
                "#EXTINF:2.0,\n"
                "packed.bin\n"
                "#EXT-X-BYTERANGE:1024@2048\n"
                "#EXTINF:2.0,\n"
                "packed.bin\n"
                "#EXT-X-ENDLIST\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "fmp4.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:7\n"
                "#EXT-X-TARGETDURATION:2\n"
                '#EXT-X-MAP:URI="/dash/v_1080_init.mp4"\n'
                "#EXTINF:2.0,\n"
                "/dash/v_1080_seg1.m4s\n"
                "#EXT-X-ENDLIST\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "live.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:3\n"
                "#EXT-X-TARGETDURATION:4\n"
                "#EXT-X-MEDIA-SEQUENCE:991\n"
                "#EXTINF:4.0,\n"
                "seg360.ts\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        if name == "drm.m3u8":
            txt = (
                "#EXTM3U\n"
                "#EXT-X-VERSION:5\n"
                "#EXT-X-TARGETDURATION:4\n"
                '#EXT-X-KEY:METHOD=SAMPLE-AES,KEYFORMAT="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed",URI="data:text/plain;base64,AAA="\n'
                "#EXTINF:4.0,\n"
                "seg0.enc\n"
                "#EXT-X-ENDLIST\n"
            )
            return self.send_bytes(200, txt.encode("utf-8"), m3u_type)
        return self.serve_file(os.path.join(ROOT, "hls", os.path.basename(name)), "")

    def handle_dash(self, name):
        mpd_type = "application/dash+xml"
        if name == "manifest.mpd":
            xml = (
                '<?xml version="1.0" encoding="utf-8"?>\n'
                '<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT2S">\n'
                '  <Period duration="PT2S">\n'
                '    <AdaptationSet contentType="video" mimeType="video/mp4">\n'
                '      <SegmentTemplate initialization="/dash/v_$RepresentationID$_init.mp4" media="/dash/v_$RepresentationID$_seg$Number$.m4s" startNumber="1" duration="2" timescale="1"/>\n'
                '      <Representation id="720" bandwidth="1500000" width="1280" height="720"/>\n'
                '      <Representation id="1080" bandwidth="4200000" width="1920" height="1080"/>\n'
                '    </AdaptationSet>\n'
                '    <AdaptationSet contentType="audio" mimeType="audio/mp4">\n'
                '      <SegmentTemplate initialization="/dash/a_init.mp4" media="/dash/a_seg$Number$.m4s" startNumber="1" duration="2" timescale="1"/>\n'
                '      <Representation id="aud1" bandwidth="128000"/>\n'
                '    </AdaptationSet>\n'
                '  </Period>\n'
                '</MPD>\n'
            )
            return self.send_bytes(200, xml.encode("utf-8"), mpd_type)
        if name == "drm.mpd":
            xml = (
                '<?xml version="1.0" encoding="utf-8"?>\n'
                '<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10S">\n'
                '  <Period duration="PT10S">\n'
                '    <AdaptationSet contentType="video" mimeType="video/mp4">\n'
                '      <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>\n'
                '      <Representation id="1080" bandwidth="4000000" width="1920" height="1080">\n'
                '        <BaseURL>/dash/v_1080_init.mp4</BaseURL>\n'
                '      </Representation>\n'
                '    </AdaptationSet>\n'
                '  </Period>\n'
                '</MPD>\n'
            )
            return self.send_bytes(200, xml.encode("utf-8"), mpd_type)
        return self.serve_file(os.path.join(ROOT, "dash", os.path.basename(name)), "")

    def handle_fakehost(self, host, rest, query, method):
        qs = urllib.parse.parse_qs(query)
        if host == "docs.google.com":
            if rest == "/document/d/doc123/export" and qs.get("format") == ["pdf"]:
                return self.send_bytes(
                    200,
                    b"%PDF-1.4 fake google doc\n",
                    "application/pdf",
                    {"Content-Disposition": 'attachment; filename="Quarterly_Plan.pdf"'},
                )
            if rest == "/document/d/doc123/export" and qs.get("format") == ["docx"]:
                return self.send_bytes(
                    200,
                    b"PKfake-docx-payload",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    {"Content-Disposition": 'attachment; filename="Quarterly_Plan.docx"'},
                )
            if rest == "/spreadsheets/d/sheet456/export" and qs.get("format") == ["xlsx"]:
                return self.send_bytes(
                    200,
                    b"PK\x03\x04fake-xlsx-payload",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    {"Content-Disposition": 'attachment; filename="Budget_2026.xlsx"'},
                )
        if host in ("drive.google.com", "drive.usercontent.google.com") and rest in ("/uc", "/download"):
            if qs.get("id") == ["large999"]:
                if qs.get("confirm") == ["t"] and qs.get("at") == ["tok-at-77"]:
                    return self.send_bytes(
                        200,
                        b"MKV_FAKE_PAYLOAD" * 64,
                        "application/octet-stream",
                        {"Content-Disposition": 'attachment; filename="BigArchive.mkv"'},
                    )
                html = (
                    b'<html><body><span class="uc-name-size"><a href="/x">BigArchive.mkv</a> (3.2G)</span>'
                    b'<form id="download-form" action="https://drive.usercontent.google.com/download" method="get">'
                    b'<input type="hidden" name="id" value="large999">'
                    b'<input type="hidden" name="confirm" value="t">'
                    b'<input type="hidden" name="uuid" value="u-42">'
                    b'<input type="hidden" name="at" value="tok-at-77">'
                    b'</form></body></html>'
                )
                return self.send_bytes(200, html, "text/html; charset=utf-8")
        if host == "api.github.com":
            if rest == "/repos/acme/player/releases/latest":
                body = (
                    b'{"tag_name":"v2.4.0","assets":['
                    b'{"name":"SHA256SUMS.txt","size":128,"browser_download_url":"https://objects.githubusercontent.com/acme/SHA256SUMS.txt"},'
                    b'{"name":"acme-player-win-x64.zip","size":131072,"browser_download_url":"https://objects.githubusercontent.com/acme/acme-player-win-x64.zip"}'
                    b']}'
                )
                return self.send_bytes(200, body, "application/json")
            if rest == "/repos/acme/player/releases/tags/v2.3.0":
                body = (
                    b'{"tag_name":"v2.3.0","assets":['
                    b'{"name":"acme-player-v2.3.0.msi","size":262144,"browser_download_url":"https://objects.githubusercontent.com/acme/acme-player-v2.3.0.msi"}'
                    b']}'
                )
                return self.send_bytes(200, body, "application/json")
        if host == "pixeldrain.com":
            if rest == "/api/file/pxAbc123/info":
                body = b'{"id":"pxAbc123","name":"ubuntu-clip.mp4","size":5242880,"mime_type":"video/mp4"}'
                return self.send_bytes(200, body, "application/json")
        if host == "api.gofile.io":
            if method == "POST" and rest == "/accounts":
                return self.send_bytes(200, b'{"status":"ok","data":{"token":"gf_guest_tok_99"}}', "application/json")
            if method == "GET" and rest == "/contents/gfShare1":
                if self.headers.get("Authorization") != "Bearer gf_guest_tok_99":
                    return self.send_empty(401)
                body = (
                    b'{"status":"ok","data":{"type":"folder","childrenCount":1,"children":{'
                    b'"c1":{"type":"file","name":"dataset.7z","size":2097152,"link":"https://store1.gofile.io/download/web/c1/dataset.7z"}'
                    b'}}}'
                )
                return self.send_bytes(200, body, "application/json")
            if method == "GET" and rest == "/contents/gfMulti":
                body = (
                    b'{"status":"ok","data":{"type":"folder","childrenCount":2,"children":{'
                    b'"c1":{"type":"file","name":"part1.zip","size":1024,"link":"https://store1.gofile.io/download/web/c1/part1.zip"},'
                    b'"c2":{"type":"file","name":"part2.zip","size":1024,"link":"https://store1.gofile.io/download/web/c2/part2.zip"}'
                    b'}}}'
                )
                return self.send_bytes(200, body, "application/json")
        if host == "t.me":
            if rest == "/gdm_news/42" and qs.get("embed") == ["1"]:
                html = (
                    b'<div class="tgme_widget_message">'
                    b'<div class="tgme_widget_message_document_title">release_demo.mp4</div>'
                    b'<video src="https://cdn4.telesco.pe/file/tg_demo_42.mp4"></video>'
                    b'</div>'
                )
                return self.send_bytes(200, html, "text/html; charset=utf-8")
        if host == "www.mediafire.com" and rest == "/file/mf123/setup.exe/file":
            html = b'<html><body><a id="downloadButton" href="https://download1500.mediafire.com/xyz/mf123/setup.exe">Download</a></body></html>'
            return self.send_bytes(200, html, "text/html; charset=utf-8")
        if host == "api.onedrive.com" and rest.startswith("/v1.0/shares/"):
            return self.send_empty(401)
        if host == "1drv.ms" and rest == "/u/s!ABc" and qs.get("download") == ["1"]:
            return self.send_empty(302, {"Location": "https://onedrive.live.com/download?resid=ABC123"})
        if host == "onedrive.live.com" and rest == "/download" and qs.get("resid") == ["ABC123"]:
            return self.send_bytes(
                200,
                b"ONEDRIVE_DOC_BYTES" * 32,
                "application/octet-stream",
                {"Content-Disposition": 'attachment; filename="Notes.docx"'},
            )
        return self.send_empty(404)

    def handle_rf_ctl(self, query):
        qs = urllib.parse.parse_qs(query)
        op = qs.get("op", [""])[0]
        tag = qs.get("tag", [""])[0]
        sub = qs.get("sub", [""])[0]
        if op == "hits":
            with lock:
                lines = list(rf_hits.get(tag, []))
            return self.send_bytes(200, "\n".join(lines).encode("utf-8"), "text/plain; charset=utf-8")
        if op == "reset":
            with lock:
                rf_hits[tag] = []
            return self.send_empty(200)
        if op == "hold":
            with lock:
                rf_holds[(tag, sub)] = threading.Event()
            return self.send_empty(200)
        if op == "unhold":
            with lock:
                ev = rf_holds.pop((tag, sub), None)
            if ev is not None:
                ev.set()
            return self.send_empty(200)
        if op == "drop_once":
            at = int(qs.get("at", ["0"])[0])
            with lock:
                rf_drop_once[(tag, sub)] = at
            return self.send_empty(200)
        return self.send_empty(400)

    def handle_rf_file(self, tag, subpath, query):
        parts = [p for p in subpath.split("/") if p]
        if not parts or any(p in (".", "..") for p in parts):
            return self.send_empty(400)
        with lock:
            rf_hits.setdefault(tag, []).append(subpath)
            hold_ev = rf_holds.get((tag, subpath))
            drop_at = rf_drop_once.pop((tag, subpath), None)
        if hold_ev is not None:
            hold_ev.wait(timeout=5.0)

        fp = os.path.join(ROOT, "rf", tag, *parts)
        if not os.path.isfile(fp):
            return self.send_empty(404)

        ext = os.path.splitext(parts[-1])[1].lower()
        if ext == ".m3u8":
            mime = "application/vnd.apple.mpegurl"
        elif ext == ".mpd":
            mime = "application/dash+xml"
        elif ext in (".htm", ".html", ".xhtml", ".mht"):
            mime = "text/html; charset=utf-8"
        else:
            mime = "application/octet-stream"

        size = os.path.getsize(fp)
        qs = urllib.parse.parse_qs(query)

        if drop_at is not None:
            self.send_response(200)
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(size))
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            with open(fp, "rb") as f:
                left = min(drop_at, size)
                while left > 0:
                    chunk = f.read(min(262144, left))
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except Exception:
                        return
                    left -= len(chunk)
            try:
                self.wfile.flush()
            except Exception:
                pass
            return

        if qs.get("chunked") == ["1"]:
            self.send_response(200)
            self.send_header("Content-Type", mime)
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            with open(fp, "rb") as f:
                while True:
                    chunk = f.read(262144)
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except Exception:
                        return
            return

        start, end = 0, size - 1
        rng = self.headers.get("Range", "")
        m = re.match(r"bytes=(\d+)-(\d*)", rng)
        if m and size > 0:
            start = int(m.group(1))
            end = int(m.group(2)) if m.group(2) else size - 1
            if start >= size or end >= size or end < start:
                return self.send_empty(416)
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.send_header("Accept-Ranges", "bytes")
        else:
            self.send_response(200)
            if ext not in (".m3u8", ".mpd"):
                self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(max(0, end - start + 1) if size > 0 else 0))
        self.end_headers()
        if size > 0:
            with open(fp, "rb") as f:
                f.seek(start)
                left = end - start + 1
                while left > 0:
                    chunk = f.read(min(262144, left))
                    if not chunk:
                        break
                    try:
                        self.wfile.write(chunk)
                    except Exception:
                        return
                    left -= len(chunk)


http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
