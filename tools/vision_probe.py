"""Does DeepSeek's API accept images (OpenAI-style image_url data URLs), and in
which message positions? Sends a generated PNG (left half red, right half blue)."""
import base64
import json
import struct
import sys
import time
import urllib.error
import urllib.request
import zlib

KEY = [l.split("=", 1)[1].strip() for l in open("relay/.env", encoding="utf-8") if l.startswith("DEEPSEEK_API_KEY=")][0]
MODEL = sys.argv[1] if len(sys.argv) > 1 else "deepseek-flash"


def png(w, h, pixel):
    raw = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(w)) for y in range(h))
    def chunk(t, data):
        return struct.pack(">I", len(data)) + t + data + struct.pack(">I", zlib.crc32(t + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


img = "data:image/png;base64," + base64.b64encode(png(64, 32, lambda x, y: (220, 0, 0) if x < 32 else (0, 0, 220))).decode()
part = {"type": "image_url", "image_url": {"url": img}}
question = "What colors are in the left and right halves of this image? Answer in one short sentence."


def ask(label, messages):
    body = json.dumps({"model": MODEL, "messages": messages, "stream": False}).encode()
    req = urllib.request.Request("https://api.deepseek.com/chat/completions", data=body,
                                 headers={"Authorization": "Bearer " + KEY, "Content-Type": "application/json"})
    t = time.time()
    try:
        r = json.load(urllib.request.urlopen(req, timeout=120))
        msg = r["choices"][0]["message"]
        print("%-34s %5.1fs  %s" % (label, time.time() - t, (msg.get("content") or "").strip()[:160]))
        print("%-34s        usage %s" % ("", r.get("usage")))
    except urllib.error.HTTPError as e:
        print("%-34s HTTP %d %s" % (label, e.code, e.read().decode()[:200]))


ask("user message with image", [{"role": "user", "content": [{"type": "text", "text": question}, part]}])
call = {"id": "call_1", "type": "function", "function": {"name": "screenshot", "arguments": "{}"}}
ask("image inside a tool result", [
    {"role": "user", "content": question + " Take a screenshot first."},
    {"role": "assistant", "content": "", "tool_calls": [call]},
    {"role": "tool", "tool_call_id": "call_1", "content": [{"type": "text", "text": "screenshot:"}, part]}])
ask("tool text + follow-up user image", [
    {"role": "user", "content": question + " Take a screenshot first."},
    {"role": "assistant", "content": "", "tool_calls": [call]},
    {"role": "tool", "tool_call_id": "call_1", "content": "screenshot taken; the image follows in the next message"},
    {"role": "user", "content": [{"type": "text", "text": "[image returned by screenshot]"}, part]}])
