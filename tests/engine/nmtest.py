"""Simulates Chrome talking to GDM's native-messaging host: length-prefixed JSON over stdin/stdout."""
import json, struct, subprocess, sys

exe = sys.argv[1]
msgs = [json.loads(m) for m in sys.argv[2:]] or [{"action": "ping"}]
for msg in msgs:
    p = subprocess.Popen([exe, "chrome-extension://fkjplbaglahdiphiodkgidkdfkbebgfh/", "--parent-window=0"],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    body = json.dumps(msg).encode("utf-8")
    p.stdin.write(struct.pack("<I", len(body)) + body)
    p.stdin.flush()
    head = p.stdout.read(4)
    if len(head) < 4:
        print("NO REPLY", msg)
        continue
    n = struct.unpack("<I", head)[0]
    reply = json.loads(p.stdout.read(n).decode("utf-8"))
    p.stdin.close()
    p.wait(timeout=10)
    print(json.dumps(msg)[:80], "=>", json.dumps(reply, ensure_ascii=False)[:300])
