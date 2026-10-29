"""Hostile, slow, broken and tricky local HTTP/1.1 server for GDM's engine tests (TASK E).

Stdlib only, ThreadingHTTPServer bound to 127.0.0.1.
Usage:
    python -I hardserver.py <port>
    python -I hardserver.py <ignored_dir> <port>
"""
import email.utils
import gzip
import hashlib
import http.server
import json
import socket
import struct
import sys
import threading
import time
import urllib.parse
import zlib

PORT = int(sys.argv[-1])

# Precompute 256 x 64 KB deterministic blocks in RAM for O(1) random access at any 64-bit offset
BLOCK_SIZE = 65536
_base = bytearray(BLOCK_SIZE)
for _i in range(BLOCK_SIZE):
    _base[_i] = ((_i * 73) ^ ((_i >> 8) * 199)) & 0xFF
BLOCKS = [bytes(b ^ k for b in _base) for k in range(256)]


def block_key(block_idx: int, seed: int) -> int:
    return (block_idx ^ (block_idx >> 8) ^ (block_idx >> 16) ^ seed) & 0xFF


def generate_slice(start: int, length: int, seed: int = 0) -> bytes:
    """Generates `length` deterministic bytes starting at 64-bit `start` offset."""
    if length <= 0:
        return b""
    out = bytearray()
    pos = start
    rem = length
    while rem > 0:
        b_idx = pos >> 16
        off = pos & 0xFFFF
        take = min(rem, BLOCK_SIZE - off)
        blk = BLOCKS[block_key(b_idx, seed)]
        out.extend(blk[off : off + take])
        pos += take
        rem -= take
    return bytes(out)


def sha256_for(size: int, seed: int = 0) -> str:
    h = hashlib.sha256()
    pos = 0
    rem = size
    while rem > 0:
        b_idx = pos >> 16
        off = pos & 0xFFFF
        take = min(rem, BLOCK_SIZE - off)
        h.update(BLOCKS[block_key(b_idx, seed)][off : off + take])
        pos += take
        rem -= take
    return h.hexdigest().upper()


lock = threading.Lock()
tags = {}


def get_tag_state(tag: str) -> dict:
    with lock:
        st = tags.get(tag)
        if st is None:
            st = {
                "active": 0,
                "max": 0,
                "requests": 0,
                "range_requests": 0,
                "body_requests": 0,
                "resets_done": 0,
                "stalls_done": 0,
                "drops_done": 0,
                "shorts_done": 0,
                "overrides": {},
                "log": [],
            }
            tags[tag] = st
        return st


class HardServer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def send_json(self, code: int, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_plain(self, code: int, text: str, headers: dict | None = None):
        body = text.encode("utf-8")
        self.send_response(code)
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def abort_tcp(self):
        try:
            self.wfile.flush()
        except Exception:
            pass
        try:
            self.connection.setsockopt(
                socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0)
            )
        except Exception:
            pass
        try:
            self.connection.close()
        except Exception:
            pass
        self.close_connection = True

    def do_HEAD(self):
        parsed = urllib.parse.urlparse(self.path)
        qs = urllib.parse.parse_qs(parsed.query)
        tag = qs.get("tag", qs.get("t", [parsed.path]))[0]
        st = get_tag_state(tag)
        with lock:
            st["requests"] += 1
            st["log"].append({"method": "HEAD", "path": self.path})
        if qs.get("head405", ["0"])[0] == "1":
            self.send_response(405)
            self.send_header("Allow", "GET")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.handle_request_inner(head_only=True)

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        qs = urllib.parse.parse_qs(parsed.query)

        if path == "/ping":
            return self.send_plain(200, "pong")

        if path == "/reset":
            tag = qs.get("tag", qs.get("t", [""]))[0]
            with lock:
                if tag:
                    tags.pop(tag, None)
                else:
                    tags.clear()
            return self.send_plain(200, "ok")

        if path == "/mutate":
            tag = qs.get("tag", qs.get("t", [""]))[0]
            st = get_tag_state(tag)
            with lock:
                for k, v in qs.items():
                    if k not in ("tag", "t"):
                        st["overrides"][k] = v[0]
            return self.send_plain(200, "ok")

        if path == "/stats":
            tag = qs.get("tag", qs.get("t", [""]))[0]
            st = get_tag_state(tag)
            with lock:
                snap = {
                    "active": st["active"],
                    "max": st["max"],
                    "requests": st["requests"],
                    "range_requests": st["range_requests"],
                    "body_requests": st["body_requests"],
                    "resets_done": st["resets_done"],
                    "stalls_done": st["stalls_done"],
                    "drops_done": st["drops_done"],
                    "busy_answers": st.get("busy_answers", 0),
                }
            if qs.get("plain", ["0"])[0] == "1":
                return self.send_plain(200, str(snap["max"]))
            return self.send_json(200, snap)

        if path == "/log":
            tag = qs.get("tag", qs.get("t", [""]))[0]
            st = get_tag_state(tag)
            with lock:
                entries = list(st["log"])
            return self.send_json(200, entries)

        if path == "/hash":
            size = int(qs.get("size", ["0"])[0])
            seed = int(qs.get("seed", ["0"])[0])
            return self.send_plain(200, sha256_for(size, seed))

        self.handle_request_inner(head_only=False)

    def handle_request_inner(self, head_only: bool):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        raw_qs = urllib.parse.parse_qs(parsed.query, keep_blank_values=True)
        tag = raw_qs.get("tag", raw_qs.get("t", [path]))[0]
        st = get_tag_state(tag)

        with lock:
            st["active"] += 1
            if st["active"] > st["max"]:
                st["max"] = st["active"]
            req_idx = st["requests"]
            st["requests"] += 1
            overrides = dict(st["overrides"])

        def opt(key: str, default: str = "") -> str:
            if key in overrides:
                return overrides[key]
            vals = raw_qs.get(key)
            return vals[0] if vals else default

        rng_hdr = self.headers.get("Range", "")
        if_range_hdr = self.headers.get("If-Range", "")
        cookie_hdr = self.headers.get("Cookie", "")
        auth_hdr = self.headers.get("Authorization", "")
        ref_hdr = self.headers.get("Referer", "")
        ae_hdr = self.headers.get("Accept-Encoding", "")

        log_entry = {
            "idx": req_idx,
            "method": "HEAD" if head_only else "GET",
            "path": self.path,
            "range": rng_hdr,
            "if_range": if_range_hdr,
            "has_cookie": bool(cookie_hdr),
            "cookie": cookie_hdr,
            "has_auth": bool(auth_hdr),
            "auth": auth_hdr,
            "has_referer": bool(ref_hdr),
            "accept_encoding": ae_hdr,
            "status": 0,
        }
        with lock:
            if rng_hdr:
                st["range_requests"] += 1
            st["log"].append(log_entry)

        try:
            # 0. busy_over=N: more than N requests at once answer 429 (like a rate limiter);
            #    busy_ban=S then refuses every request of this tag for S seconds; busy_retry_after=S is sent along.
            busy_over = int(opt("busy_over", "0"))
            if busy_over > 0:
                now = time.time()
                with lock:
                    over = st["active"] > busy_over
                    if over and int(opt("busy_ban", "0")) > 0 and st.get("ban_until", 0) <= now:
                        st["ban_until"] = now + int(opt("busy_ban", "0"))
                    banned = st.get("ban_until", 0) > now
                    if over or banned:
                        st["busy_answers"] = st.get("busy_answers", 0) + 1
                if over or banned:
                    log_entry["status"] = 429
                    busy_headers = {"Retry-After": opt("busy_retry_after", "1")}
                    if opt("busy_challenge") == "1":
                        busy_headers["cf-mitigated"] = "challenge"  # Cloudflare managed challenge
                    return self.send_plain(429, "busy", busy_headers)

            # 0b. page_for_browsers=1: browsers get an HTML mirror page, download tools get the file (VideoLAN style)
            if opt("page_for_browsers") == "1" and "Mozilla" in self.headers.get("User-Agent", ""):
                log_entry["status"] = 200
                body = b"<!DOCTYPE html><html><body>Choose a mirror</body></html>"
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

            # 1. drop_headers: close before sending any response headers
            if opt("drop_headers") == "1":
                drop_n = int(opt("drop_n", "999999"))
                with lock:
                    do_drop = st["drops_done"] < drop_n
                    if do_drop:
                        st["drops_done"] += 1
                if do_drop:
                    log_entry["status"] = -1
                    self.abort_tcp()
                    return

            # 2. Header security checks (for redirect / downgrade tests)
            if opt("require_no_cookie") == "1" and cookie_hdr:
                log_entry["status"] = 403
                return self.send_plain(403, "Cookie header leaked")
            if opt("require_no_auth") == "1" and auth_hdr:
                log_entry["status"] = 403
                return self.send_plain(403, "Authorization header leaked")
            if opt("require_no_referer") == "1" and ref_hdr:
                log_entry["status"] = 403
                return self.send_plain(403, "Referer header leaked")
            if opt("require_cookie") and cookie_hdr != opt("require_cookie"):
                log_entry["status"] = 403
                return self.send_plain(403, "Missing expected Cookie")
            if opt("require_auth") and auth_hdr != opt("require_auth"):
                log_entry["status"] = 401
                return self.send_plain(
                    401, "Unauthorized", {"WWW-Authenticate": 'Basic realm="hard"'}
                )

            # 3. Redirects
            if opt("redirect_loop") == "1":
                log_entry["status"] = 302
                return self.send_plain(302, "", {"Location": self.path})

            if opt("redirect_missing_loc") == "1":
                log_entry["status"] = 302
                self.send_response(302)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return

            if opt("redirect_to"):
                code = int(opt("code", "302"))
                target = opt("redirect_to")
                log_entry["status"] = code
                return self.send_plain(code, "", {"Location": target})

            redirects_left = int(opt("redirects", "0"))
            if redirects_left > 0:
                codes_str = opt("redirect_codes", "301,302,303,307,308")
                codes = [int(c) for c in codes_str.split(",") if c.strip()]
                hop_idx = int(opt("hop", "0"))
                code = codes[hop_idx % len(codes)]
                next_qs = dict(raw_qs)
                next_qs["redirects"] = [str(redirects_left - 1)]
                next_qs["hop"] = [str(hop_idx + 1)]
                query_encoded = urllib.parse.urlencode(next_qs, doseq=True)
                if opt("redirect_rel") == "1":
                    # Relative path without scheme/host
                    leaf = path.lstrip("/")
                    loc = f"/{leaf}?{query_encoded}" if hop_idx % 2 == 0 else f"{leaf.split('/')[-1]}?{query_encoded}"
                elif opt("redirect_unicode") == "pct":
                    size_seg = path.split("/")[2] if len(path.split("/")) > 2 else "4096"
                    ar_name = urllib.parse.quote("ملف_عربي.bin")
                    loc = f"/f/{size_seg}/{ar_name}?{query_encoded}"
                elif opt("redirect_unicode") == "raw":
                    size_seg = path.split("/")[2] if len(path.split("/")) > 2 else "4096"
                    loc_raw = f"/f/{size_seg}/ملف_مباشر.bin?{query_encoded}"
                    log_entry["status"] = code
                    raw_resp = (
                        f"HTTP/1.1 {code} Redirect\r\n"
                        f"Location: {loc_raw}\r\n"
                        f"Content-Length: 0\r\n\r\n"
                    ).encode("utf-8")
                    self.wfile.write(raw_resp)
                    self.wfile.flush()
                    return
                else:
                    loc = f"{path}?{query_encoded}"
                log_entry["status"] = code
                return self.send_plain(code, "", {"Location": loc})

            # 4. Status scripting (e.g., status=503,503,200 or status=429)
            status_script = opt("status")
            if status_script:
                seq = [int(x.strip()) for x in status_script.split(",") if x.strip()]
                chosen_code = seq[min(req_idx, len(seq) - 1)]
                if chosen_code != 200:
                    hdrs = {}
                    ra = opt("retry_after")
                    if ra:
                        if ra.startswith("date+"):
                            sec = float(ra[5:])
                            hdrs["Retry-After"] = email.utils.formatdate(
                                time.time() + sec, usegmt=True
                            )
                        elif ra.startswith("date-"):
                            sec = float(ra[5:])
                            hdrs["Retry-After"] = email.utils.formatdate(
                                time.time() - sec, usegmt=True
                            )
                        else:
                            hdrs["Retry-After"] = ra
                    log_entry["status"] = chosen_code
                    return self.send_plain(chosen_code, f"Status {chosen_code}", hdrs)

            # Parse size from /f/<size>[/<name>]
            parts = [p for p in path.split("/") if p]
            size = int(parts[1]) if len(parts) >= 2 and parts[0] == "f" else int(opt("size", "4096"))
            if opt("zero") == "1":
                size = 0
            seed = int(opt("seed", "0"))

            # 5. Validators (ETag / Last-Modified / weak ETag / etag_change_after)
            etag_change_after = int(opt("etag_change_after", "0"))
            lastmod_change_after = int(opt("lastmod_change_after", "0"))
            version_bumped = False
            if etag_change_after > 0 and req_idx >= etag_change_after:
                version_bumped = True
                seed += 1
            if lastmod_change_after > 0 and req_idx >= lastmod_change_after:
                version_bumped = True
                seed += 1

            raw_etag = opt("etag")
            if opt("weak_etag") == "1":
                etag = f'W/"weak-{size}-{seed}"'
            elif raw_etag:
                if version_bumped:
                    etag = f'"{raw_etag.strip(chr(34))}-v2"'
                else:
                    etag = raw_etag if raw_etag.startswith('"') or raw_etag.startswith("W/") else f'"{raw_etag}"'
            elif opt("no_etag") == "1":
                etag = ""
            else:
                etag = f'"{size}-{seed}"'

            lastmod = opt("lastmod")
            if lastmod == "1":
                lastmod = (
                    "Wed, 21 Oct 2020 08:00:00 GMT"
                    if version_bumped
                    else "Wed, 21 Oct 2020 07:28:00 GMT"
                )
            elif version_bumped and lastmod:
                lastmod = "Wed, 21 Oct 2020 08:00:00 GMT"

            # 6. Range quirks & If-Range evaluation
            norange = opt("norange") == "1"
            range_once = opt("range_once") == "1"
            badrange = opt("badrange")
            range416 = opt("range416")
            ifrange_mode = opt("ifrange", "on")

            if range_once:
                with lock:
                    if st["range_requests"] > 1:
                        norange = True

            # Handle 416 modes
            if range416 == "always" or (range416 == "1" and rng_hdr):
                log_entry["status"] = 416
                return self.send_plain(
                    416, "Range Not Satisfiable", {"Content-Range": f"bytes */{size}"}
                )
            if range416 == "seg" and rng_hdr and rng_hdr != "bytes=0-0":
                log_entry["status"] = 416
                return self.send_plain(
                    416, "Range Not Satisfiable", {"Content-Range": f"bytes */{size}"}
                )

            # 0-byte file with Range: bytes=0-0 -> standard HTTP 416 unless norange
            if size == 0 and rng_hdr and opt("zero_200") != "1":
                log_entry["status"] = 416
                return self.send_plain(
                    416, "Range Not Satisfiable", {"Content-Range": "bytes */0"}
                )

            # Check If-Range validator match when ifrange_mode == "on"
            if_range_ok = True
            if rng_hdr and if_range_hdr and ifrange_mode != "off":
                if if_range_hdr.startswith('"') or if_range_hdr.startswith("W/"):
                    if if_range_hdr != etag or etag.startswith("W/"):
                        if_range_ok = False
                elif lastmod and if_range_hdr != lastmod:
                    if_range_ok = False

            # Framing modes that force 200 single-stream
            chunked = opt("chunked") == "1"
            nolength = opt("nolength") == "1"
            gzip_mode = opt("gzip") == "1"
            if gzip_mode and opt("gzip_respect_identity") == "1" and "identity" in ae_hdr.lower():
                gzip_mode = False

            use_range = (
                bool(rng_hdr)
                and not norange
                and if_range_ok
                and not chunked
                and not nolength
                and not gzip_mode
                and size > 0
            )

            start, end = 0, max(0, size - 1)
            status_code = 200
            content_range_hdr = None

            if use_range and rng_hdr.startswith("bytes="):
                spec = rng_hdr[6:].split(",")[0].strip()
                s_part, e_part = spec.split("-", 1)
                start = int(s_part) if s_part else 0
                end = int(e_part) if e_part else size - 1
                if start >= size or start > end:
                    log_entry["status"] = 416
                    return self.send_plain(
                        416, "Range Not Satisfiable", {"Content-Range": f"bytes */{size}"}
                    )
                end = min(end, size - 1)
                status_code = 206
                content_range_hdr = f"bytes {start}-{end}/{size}"

                if badrange == "1" or (badrange == "seg" and not (start == 0 and end == 0)):
                    # Send a mismatched Content-Range window
                    wrong_start = start + 16 if start + 32 < size else max(0, start - 16)
                    wrong_end = min(size - 1, end)
                    if wrong_start == start:
                        wrong_end = max(start, end - 1)
                    content_range_hdr = f"bytes {wrong_start}-{wrong_end}/{size}"

            # Build response headers
            length = 0 if size == 0 else (end - start + 1)
            payload_to_send = None

            if gzip_mode:
                raw_data = generate_slice(0, size, seed)
                payload_to_send = gzip.compress(raw_data, compresslevel=1)
                length = len(payload_to_send)

            log_entry["status"] = status_code
            self.send_response(status_code)

            long_nonrange = opt("long") == "1" and not use_range and not chunked and not nolength and size > 0
            if content_range_hdr:
                self.send_header("Content-Range", content_range_hdr)
            elif long_nonrange:
                self.send_header("Content-Range", f"bytes 0-{size - 1}/{size}")
            if opt("noaccept") != "1" and not norange and not chunked and not nolength:
                self.send_header("Accept-Ranges", "bytes")
            if etag:
                self.send_header("ETag", etag)
            if lastmod:
                self.send_header("Last-Modified", lastmod)
            if gzip_mode:
                self.send_header("Content-Encoding", "gzip")

            self.send_header("Content-Type", opt("mime", "application/octet-stream"))

            # Content-Disposition options
            cd_header = self.build_content_disposition(opt, parts)
            if cd_header:
                self.send_header("Content-Disposition", cd_header)

            if chunked:
                self.send_header("Transfer-Encoding", "chunked")
            elif nolength or long_nonrange:
                self.send_header("Connection", "close")
                self.close_connection = True
            else:
                self.send_header("Content-Length", str(length))

            self.end_headers()
            if head_only:
                return

            # Track non-probe body transfers
            is_probe = rng_hdr == "bytes=0-0" and length == 1
            if not is_probe:
                with lock:
                    st["body_requests"] += 1
                    body_req_num = st["body_requests"]
            else:
                body_req_num = 0

            # Determine throttling, stall, reset, short, long
            rate = float(opt("rate", "0"))
            slow_first = float(opt("slow_first", "0"))
            if slow_first > 0 and body_req_num == 1:
                rate = slow_first

            stall_spec = opt("stall")
            stall_offset, stall_sec = -1, 0.0
            if stall_spec and ":" in stall_spec:
                so, ss = stall_spec.split(":", 1)
                stall_offset, stall_sec = int(so), float(ss)
            stall_n = int(opt("stall_n", "999999"))

            reset_offset = int(opt("reset", "-1"))
            reset_n = int(opt("reset_n", "999999"))

            short_mode = opt("short")
            short_n = int(opt("short_n", "999999"))
            actual_to_send = length
            if short_mode and not is_probe and length > 1:
                with lock:
                    do_short = st["shorts_done"] < short_n
                    if do_short:
                        st["shorts_done"] += 1
                if do_short:
                    actual_to_send = max(1, length // 2) if short_mode == "1" else min(length - 1, int(short_mode))

            extra_long = 0
            if opt("long") == "1" and not is_probe:
                extra_long = 65536
            elif opt("long") and opt("long") != "0" and not is_probe:
                extra_long = int(opt("long"))

            # Stream the payload
            sent = 0
            cur_offset = start
            chunk_step = 16384 if rate <= 0 else max(512, min(16384, int(rate / 20)))

            while sent < actual_to_send:
                # Check stall trigger
                if stall_offset >= 0 and sent >= stall_offset and not is_probe:
                    with lock:
                        do_stall = st["stalls_done"] < stall_n
                        if do_stall:
                            st["stalls_done"] += 1
                    stall_offset = -1
                    if do_stall and stall_sec > 0:
                        time.sleep(stall_sec)

                # Check reset trigger
                if reset_offset >= 0 and sent >= reset_offset and not is_probe:
                    with lock:
                        do_reset = st["resets_done"] < reset_n
                        if do_reset:
                            st["resets_done"] += 1
                    reset_offset = -1
                    if do_reset:
                        self.abort_tcp()
                        return

                take = min(chunk_step, actual_to_send - sent)
                if payload_to_send is not None:
                    piece = payload_to_send[sent : sent + take]
                else:
                    piece = generate_slice(cur_offset, take, seed)

                try:
                    if chunked:
                        self.wfile.write(f"{len(piece):X}\r\n".encode("ascii"))
                        self.wfile.write(piece)
                        self.wfile.write(b"\r\n")
                    else:
                        self.wfile.write(piece)
                    self.wfile.flush()
                except Exception:
                    return

                sent += len(piece)
                cur_offset += len(piece)

                if rate > 0:
                    time.sleep(len(piece) / rate)

            if actual_to_send < length:
                # Short response: close connection abruptly so client sees premature EOF
                self.close_connection = True
                try:
                    self.wfile.flush()
                    self.connection.close()
                except Exception:
                    pass
                return

            if extra_long > 0:
                try:
                    self.wfile.write(b"X" * extra_long)
                    self.wfile.flush()
                except Exception:
                    return

            if chunked:
                try:
                    self.wfile.write(b"0\r\n\r\n")
                    self.wfile.flush()
                except Exception:
                    return

        finally:
            with lock:
                st["active"] -= 1

    def build_content_disposition(self, opt, parts) -> str:
        cd_raw = opt("cd_raw")
        if cd_raw:
            return cd_raw
        mode = opt("cd_mode")
        if not mode:
            if len(parts) >= 3 and parts[0] == "f":
                return ""
            name = opt("name")
            if name:
                return f'attachment; filename="{name}"'
            return ""
        if mode == "simple":
            return 'attachment; filename="simple_report.bin"'
        if mode == "no_disp":
            return 'filename="bare_disposition.bin"'
        if mode == "semi":
            return 'attachment; filename="part1;part2;final.bin"'
        if mode == "rfc5987":
            pct = urllib.parse.quote("تقرير_جولان_2026.bin")
            return f"attachment; filename*=UTF-8''{pct}"
        if mode == "both":
            pct = urllib.parse.quote("الاسم_المفضل.bin")
            return f'attachment; filename="fallback_ascii.bin"; filename*=UTF-8\'\'{pct}'
        if mode == "evil_traversal":
            return 'attachment; filename="../../evil.exe"'
        if mode == "evil_drive":
            return 'attachment; filename="C:\\x"'
        if mode == "evil_con":
            return 'attachment; filename="CON"'
        if mode == "evil_dots":
            return 'attachment; filename="trailing_dots.bin..."'
        if mode == "evil_long":
            long_stem = "L" * 300
            return f'attachment; filename="{long_stem}.bin"'
        return ""


if __name__ == "__main__":
    HardServer(("127.0.0.1", PORT), Handler).serve_forever()
