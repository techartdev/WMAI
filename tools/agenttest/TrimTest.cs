// TrimTest - unit test of Agent.TrimHistory (PC, no network).
// Regression: a long coding turn (one user message + dozens of tool steps)
// used to make the old count-based trim delete the whole history.
using System;
using System.Collections;
using WMAI;

class TrimTest
{
    static int failures;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? " - " + detail : ""));
    }

    static Hashtable Msg(string role, string content)
    {
        Hashtable m = new Hashtable();
        m["role"] = role;
        m["content"] = content;
        return m;
    }

    static void AddToolStep(ArrayList h, int i, int resultChars)
    {
        Hashtable fn = new Hashtable();
        fn["name"] = "build";
        fn["arguments"] = "{\"project\":\"shot\"}";
        Hashtable call = new Hashtable();
        call["id"] = "call_" + i;
        call["type"] = "function";
        call["function"] = fn;
        ArrayList calls = new ArrayList();
        calls.Add(call);
        Hashtable am = Msg("assistant", "");
        am["tool_calls"] = calls;
        h.Add(am);
        Hashtable tm = Msg("tool", "step " + i + " " + new string('x', resultChars));
        tm["tool_call_id"] = "call_" + i;
        h.Add(tm);
    }

    static int Size(ArrayList h)
    {
        int n = 0;
        foreach (Hashtable m in h) { string c = m["content"] as string; if (c != null) n += c.Length; }
        return n;
    }

    static int Main()
    {
        // 1. The reported bug: one turn, 70 tool steps with 3 KB results each.
        ArrayList h = new ArrayList();
        h.Add(Msg("user", "Make a screenshot app called shot"));
        for (int i = 0; i < 70; i++) AddToolStep(h, i, 3000);
        int before = h.Count;
        Agent.TrimHistory(h, 150000, 8);
        string first = (string)((Hashtable)h[0])["content"];
        string last = (string)((Hashtable)h[h.Count - 1])["content"];
        Check("long single turn is kept (no messages dropped)", h.Count == before && first.StartsWith("Make a screenshot"),
              h.Count + " of " + before + " messages, first: " + first);
        Check("old tool results shortened, newest intact", last.Length > 3000 && Size(h) <= 150000,
              "size " + Size(h) + " chars, newest result " + last.Length + " chars");

        // 2. Several big turns: the oldest whole turns go, the last stays.
        h = new ArrayList();
        for (int t = 0; t < 4; t++)
        {
            h.Add(Msg("user", "turn " + t));
            h.Add(Msg("assistant", new string('a', 60000)));
        }
        Agent.TrimHistory(h, 150000, 8);
        Check("oldest whole turns dropped, history starts with a user message",
              (string)((Hashtable)h[0])["role"] == "user" && (string)((Hashtable)h[h.Count - 2])["content"] == "turn 3" && Size(h) <= 150000,
              h.Count + " messages left, first: " + ((Hashtable)h[0])["content"]);

        // 3. Within budget: untouched.
        h = new ArrayList();
        h.Add(Msg("user", "hi"));
        h.Add(Msg("assistant", "hello"));
        Agent.TrimHistory(h, 150000, 8);
        Check("small history untouched", h.Count == 2, null);

        // 4. A single turn larger than the budget even after shortening: kept anyway.
        h = new ArrayList();
        h.Add(Msg("user", "huge"));
        h.Add(Msg("assistant", new string('a', 400000)));
        Agent.TrimHistory(h, 150000, 8);
        Check("oversized current turn is never deleted", h.Count == 2, null);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
