"""WMAI relay: plain-HTTP chat front end for a Windows Mobile 5 phone.

The phone (Pocket IE, later a native app) talks plain HTTP to this relay over
the USB RNDIS link. The relay holds the API key and talks modern TLS to an
OpenAI-compatible provider (DeepSeek or OpenRouter).

Pages are server-rendered, tiny, and avoid JavaScript so Pocket IE on WM5 can
use them. While the model is answering, the page meta-refreshes to show the
reply growing (poor man's streaming).
"""
import html
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import tools

HERE = os.path.dirname(os.path.abspath(__file__))

PROVIDERS = {
    "deepseek": ("https://api.deepseek.com/chat/completions", "DEEPSEEK_API_KEY", "deepseek-flash"),
    "openrouter": ("https://openrouter.ai/api/v1/chat/completions", "OPENROUTER_API_KEY", "deepseek/deepseek-chat"),
}

MAX_STEPS = tools.MAX_STEPS
TOOL_TIMEOUT = 180  # seconds to wait for the phone (includes the user's Yes/No)


def load_env(path):
    if not os.path.exists(path):
        return
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                os.environ.setdefault(k.strip(), v.strip())


load_env(os.path.join(HERE, ".env"))

PROVIDER = os.environ.get("WMAI_PROVIDER", "deepseek")
API_URL, KEY_VAR, DEFAULT_MODEL = PROVIDERS[PROVIDER]
API_KEY = os.environ.get(KEY_VAR, "")
MODEL = os.environ.get("WMAI_MODEL", DEFAULT_MODEL)
BIND = os.environ.get("WMAI_BIND", "169.254.2.2")
PORT = int(os.environ.get("WMAI_PORT", "8080"))
CHAT_FILE = os.path.join(HERE, "chat.json")
APP_EXE = os.path.join(HERE, "..", "app", "WMAI.exe")


class Chat:
    def __init__(self):
        self.lock = threading.Lock()
        self.messages = []  # OpenAI-format history, incl. tool calls/results
        self.pending = None  # display text of the running turn (text + tool lines)
        self.last_display = ""  # display text of the finished turn
        self.error = None
        self.tool_req = None  # tool call waiting for the phone
        try:
            with open(CHAT_FILE, encoding="utf-8") as f:
                self.messages = json.load(f)
        except (OSError, ValueError):
            pass

    def reset(self):
        self.messages = []
        self.pending = None
        self.last_display = ""
        self.error = None
        self.save()

    def save(self):
        # Called with the lock held; the conversation survives relay restarts.
        tmp = CHAT_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(self.messages, f, ensure_ascii=False, indent=1)
        os.replace(tmp, CHAT_FILE)

    @property
    def busy(self):
        return self.pending is not None


chat = Chat()


def stream_completion(messages, on_delta):
    """Streams one model call. Returns (content, reasoning, tool_calls)."""
    body = json.dumps({
        "model": MODEL,
        "messages": [{"role": "system", "content": tools.SYSTEM_PROMPT}] + messages,
        "tools": tools.SCHEMAS,
        "stream": True,
    }).encode()
    req = urllib.request.Request(API_URL, data=body, headers={
        "Authorization": "Bearer " + API_KEY,
        "Content-Type": "application/json",
        "HTTP-Referer": "https://github.com/wmai",  # OpenRouter attribution, harmless elsewhere
        "X-Title": "WMAI",
    })
    content = []
    reasoning = []  # thinking models: must be sent back with tool calls within the turn
    calls = {}  # index -> {"id", "name", "arguments"}
    with urllib.request.urlopen(req, timeout=120) as resp:
        for raw in resp:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if data == "[DONE]":
                break
            try:
                delta = json.loads(data)["choices"][0]["delta"]
            except (ValueError, KeyError, IndexError, TypeError):
                continue
            if delta.get("content"):
                content.append(delta["content"])
                on_delta(delta["content"])
            if delta.get("reasoning_content"):
                reasoning.append(delta["reasoning_content"])
            for tc in delta.get("tool_calls") or []:
                c = calls.setdefault(tc.get("index", 0), {"id": "", "name": "", "arguments": ""})
                c["id"] = tc.get("id") or c["id"]
                fn = tc.get("function") or {}
                c["name"] += fn.get("name") or ""
                c["arguments"] += fn.get("arguments") or ""
    return "".join(content), "".join(reasoning), [calls[i] for i in sorted(calls)]


def describe_call(name, args):
    parts = []
    for k, v in args.items():
        v = v if isinstance(v, str) else json.dumps(v)
        v = v.replace("\n", " ")
        parts.append("%s=%s" % (k, v if len(v) <= 40 else v[:37] + "..."))
    return "[tool] %s(%s)" % (name, ", ".join(parts))


def phone_exec(call_id, name, args):
    """Hands a tool call to the phone and waits for its result."""
    req = {"id": call_id, "name": name, "args": args, "done": threading.Event(), "result": None}
    with chat.lock:
        chat.tool_req = req
    if not req["done"].wait(TOOL_TIMEOUT):
        result = "error: the phone did not answer (is the WMAI app open?)"
    else:
        result = req["result"]
    with chat.lock:
        chat.tool_req = None
    return result


def drop_unanswered_tool_calls(messages):
    """The API rejects tool_calls without matching results; a failed turn can
    leave such a tail, so cut history back to before it."""
    for i in range(len(messages) - 1, -1, -1):
        m = messages[i]
        if m["role"] == "assistant" and m.get("tool_calls"):
            answered = {x.get("tool_call_id") for x in messages[i + 1:] if x["role"] == "tool"}
            if any(c["id"] not in answered for c in m["tool_calls"]):
                del messages[i:]
            return


def run_turn():
    def show(text):
        with chat.lock:
            chat.pending += text

    err = None
    try:
        for _ in range(MAX_STEPS):
            with chat.lock:
                history = list(chat.messages)
            content, reasoning, calls = stream_completion(history, show)
            msg = {"role": "assistant", "content": content}
            if reasoning:
                msg["reasoning_content"] = reasoning
            if calls:
                msg["tool_calls"] = [{"id": c["id"], "type": "function",
                                      "function": {"name": c["name"], "arguments": c["arguments"]}} for c in calls]
            with chat.lock:
                chat.messages.append(msg)
            if not calls:
                break
            for c in calls:
                try:
                    args = json.loads(c["arguments"] or "{}")
                except ValueError:
                    args = None
                if not isinstance(args, dict):
                    args = None
                with chat.lock:
                    lead = "\n" if chat.pending and not chat.pending.endswith("\n") else ""
                show(lead + describe_call(c["name"], args or {}))
                if args is None:
                    result = "error: arguments were not valid JSON"
                else:
                    result = phone_exec(c["id"], c["name"], args)
                outcome = "denied" if result.startswith("denied") else "error" if result.startswith("error") else "ok"
                show(" - %s\n" % outcome)
                with chat.lock:
                    chat.messages.append({"role": "tool", "tool_call_id": c["id"], "content": result})
        else:
            show("\n[stopped after %d steps]" % MAX_STEPS)
    except urllib.error.HTTPError as e:
        err = "HTTP %d: %s" % (e.code, e.read().decode("utf-8", "replace")[:300])
    except Exception as e:  # network errors, timeouts
        err = "%s: %s" % (type(e).__name__, e)
    with chat.lock:
        drop_unanswered_tool_calls(chat.messages)
        for m in chat.messages:  # reasoning is only needed while the turn runs
            m.pop("reasoning_content", None)
        chat.last_display = chat.pending
        chat.pending = None
        chat.error = err
        chat.save()


def start_turn(q):
    with chat.lock:
        if not q or chat.busy:
            return False
        chat.messages.append({"role": "user", "content": q})
        chat.pending = ""
        chat.error = None
    threading.Thread(target=run_turn, daemon=True).start()
    return True


def transcript(messages):
    """[(who, text)] for display: user/assistant text plus tool-call lines."""
    out = []
    for m in messages:
        if m["role"] == "user":
            out.append(("You", m["content"]))
        elif m["role"] == "assistant":
            lines = [m["content"]] if m.get("content") else []
            for c in m.get("tool_calls") or []:
                try:
                    args = json.loads(c["function"]["arguments"] or "{}")
                except ValueError:
                    args = {}
                lines.append(describe_call(c["function"]["name"], args if isinstance(args, dict) else {}))
            if lines:
                text = "\n".join(lines)
                if out and out[-1][0] == "AI":  # one turn = one AI block
                    out[-1] = ("AI", out[-1][1] + "\n" + text)
                else:
                    out.append(("AI", text))
    return out


def fmt(text):
    return html.escape(text).replace("\n", "<br>")


def render_page():
    with chat.lock:
        msgs = list(chat.messages)
        pending, error = chat.pending, chat.error
    out = ['<html><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8">']
    if pending is not None:
        out.append('<meta http-equiv="refresh" content="2;url=/#end">')
    out.append("<title>WMAI</title></head><body>")
    out.append('<b>WMAI</b> <small>%s | <a href="/new">new chat</a></small><hr>' % html.escape(MODEL))
    if pending is not None:  # the running turn is shown from `pending` below
        while msgs and msgs[-1]["role"] != "user":
            msgs.pop()
    for who, text in transcript(msgs):
        color = "#000080" if who == "You" else "#006000"
        out.append('<p><font color="%s"><b>%s:</b></font> %s</p>' % (color, who, fmt(text)))
    if pending is not None:
        out.append('<p><font color="#006000"><b>AI:</b></font> %s <i>...</i></p>' % fmt(pending))
    if error:
        out.append('<p><font color="#c00000"><b>Error:</b> %s</font></p>' % html.escape(error))
    out.append('<a name="end"></a>')
    if pending is None:
        out.append('<form method="post" action="/send" accept-charset="utf-8">'
                   '<textarea name="q" rows="3" cols="24"></textarea><br>'
                   '<input type="submit" value="Send"></form>')
    out.append("</body></html>")
    return "".join(out).encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"  # keep it simple for Pocket IE

    def send_html(self, body):
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Pragma", "no-cache")
        self.end_headers()
        self.wfile.write(body)

    def redirect(self, where="/#end"):
        self.send_response(303)
        self.send_header("Location", where)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def send_text(self, text):
        body = text.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    # --- Plain-text API for the native phone app -------------------------
    # GET  /api/reply?from=N -> first line: "B" busy, "T" busy and a tool call is
    #                            waiting, "D" done, "E <msg>" error;
    #                            rest: current reply text from char offset N
    # POST /api/send          -> body: UTF-8 message. Answers "OK" or "BUSY"
    # GET  /api/tool          -> waiting tool call: "id\nname\nkey=value..." lines,
    #                            values escaped (\\ \n \r); empty if none
    # POST /api/tool?id=ID    -> body: the tool's result text
    # GET  /api/new           -> start a new conversation
    # GET  /api/history       -> whole conversation as "You: ..."/"AI: ..." text
    # GET  /api/app           -> the latest WMAI.exe build (self-update)
    def api_history(self):
        with chat.lock:
            msgs = list(chat.messages)
        self.send_text("\n\n".join("%s: %s" % item for item in transcript(msgs)))

    def api_tool_get(self):
        with chat.lock:
            req = chat.tool_req
        if req is None or req["done"].is_set():
            return self.send_text("")
        lines = [req["id"], req["name"]]
        for k, v in req["args"].items():
            if isinstance(v, bool):
                v = "true" if v else "false"
            elif not isinstance(v, str):
                v = json.dumps(v)
            lines.append(k + "=" + v.replace("\\", "\\\\").replace("\n", "\\n").replace("\r", "\\r"))
        self.send_text("\n".join(lines))

    def api_tool_post(self, query, body):
        call_id = urllib.parse.parse_qs(query).get("id", [""])[0]
        with chat.lock:
            req = chat.tool_req
            if req is not None and req["id"] == call_id and not req["done"].is_set():
                req["result"] = body.decode("utf-8", "replace")
                req["done"].set()
        self.send_text("OK")

    def api_app(self):
        try:
            with open(APP_EXE, "rb") as f:
                body = f.read()
        except OSError:
            return self.send_error(404)
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def api_reply(self, query):
        try:
            start = int(urllib.parse.parse_qs(query).get("from", ["0"])[0])
        except ValueError:
            start = 0
        with chat.lock:
            if chat.pending is not None:
                waiting = chat.tool_req is not None and not chat.tool_req["done"].is_set()
                status, text = ("T" if waiting else "B"), chat.pending
            else:
                status = "E " + chat.error.replace("\n", " ") if chat.error else "D"
                text = chat.last_display
        # Offsets are UTF-16 code units, matching .NET string lengths on the phone.
        tail = text.encode("utf-16-le")[start * 2:].decode("utf-16-le", "replace")
        self.send_text(status + "\n" + tail)

    def do_GET(self):
        path, _, query = self.path.split("#")[0].partition("?")
        if path == "/api/reply":
            self.api_reply(query)
        elif path == "/api/history":
            self.api_history()
        elif path == "/api/tool":
            self.api_tool_get()
        elif path == "/api/app":
            self.api_app()
        elif path == "/api/new":
            with chat.lock:
                if not chat.busy:
                    chat.reset()
            self.send_text("OK")
        elif path == "/":
            self.send_html(render_page())
        elif path == "/new":
            with chat.lock:
                if not chat.busy:
                    chat.reset()
            self.redirect("/")
        else:
            self.send_error(404)

    def do_POST(self):
        path, _, query = self.path.partition("?")
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length)
        if path == "/api/tool":
            return self.api_tool_post(query, raw)
        if self.path not in ("/send", "/api/send"):
            return self.send_error(404)
        if self.path == "/api/send":
            q = raw.decode("utf-8", "replace").strip()
        else:
            q = urllib.parse.parse_qs(raw.decode("ascii", "replace"), encoding="utf-8").get("q", [""])[0].strip()
        started = start_turn(q)
        if self.path == "/api/send":
            return self.send_text("OK" if started else "BUSY")
        if started:
            time.sleep(1.5)  # often the first tokens arrive before the redirect lands
        self.redirect()

    def log_message(self, fmt_, *args):
        sys.stderr.write("%s %s %s\n" % (time.strftime("%H:%M:%S"), self.client_address[0], fmt_ % args))


class Server(ThreadingHTTPServer):
    def server_bind(self):
        # HTTPServer.server_bind does a reverse DNS lookup (getfqdn) that stalls
        # ~10s on link-local addresses; we never use the name.
        self.socket.bind(self.server_address)
        self.server_address = self.socket.getsockname()
        self.server_name, self.server_port = self.server_address[:2]


def main():
    if not API_KEY:
        sys.exit("Missing %s - put it in relay/.env (see .env.example)" % KEY_VAR)
    srv = Server((BIND, PORT), Handler)
    print("WMAI relay: %s via %s on http://%s:%d/" % (MODEL, PROVIDER, BIND, PORT), flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
