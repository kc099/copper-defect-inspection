# Prompt: Mock Encoder API (for testing before the PCB exists)

**What this is for.** [Scan/HttpEncoderSource.cs](../Scan/HttpEncoderSource.cs)
(not built yet — see the note at the bottom) will poll a real PCB over HTTP.
Nothing about that code can be exercised for real until either the PCB or a
stand-in exists. This is that stand-in: a small HTTP server you run on your
own machine that answers exactly like the PCB is supposed to, so the app's
polling, staleness detection, capture-delay compensation and the trigger math
can all be tested against real HTTP and real timing — not the in-process
[SimulatedEncoderSource.cs](../Scan/SimulatedEncoderSource.cs), which skips
the network entirely and can't catch a bug that only shows up over the wire.

**How to use this file.** The fenced block in §3 is a complete, self-contained
prompt — copy it as-is into whatever tool you use to generate the program (a
fresh AI session, Claude Code, or write it by hand from it). It restates the
whole contract on its own, so it works even with no access to this repo.

---

## 1. The contract this program must implement

This is copied from [STRIP_SCAN_BUILD_PLAN.md §5](STRIP_SCAN_BUILD_PLAN.md#5-what-the-pcb-must-send--spec-for-your-pcb-programmer),
which is also what will eventually go to whoever builds the real PCB firmware.
The mock and the real PCB must satisfy the identical contract — that's the
whole point of building the mock from this spec rather than from the app's
code.

**One endpoint:** `GET /encoder` → `200 OK`, `application/json`.

| Field | Type | Meaning |
|---|---|---|
| `mm` | float | **Absolute** travel in millimetres since the program started. Never reset per request. |
| `seq` | integer | Increments on **every** internal sample, whether or not the strip is moving. |
| `zeroCount` | integer | Increments by 1 on every simulated button press. |
| `zeroMm` | float | The value of `mm` at the moment of the last press. |
| `button` | 0/1 | Current button state (momentary is fine — a 1 on the request that coincides with a press, 0 otherwise). |
| `uptimeMs` | integer | Milliseconds since the program started. |
| `dir` | −1/+1 | Travel direction. `1` unless a scenario deliberately reverses. |

Example response:
```json
{
  "seq":       4821,
  "uptimeMs":  128374,
  "mm":        52163.85,
  "zeroCount": 7,
  "zeroMm":    52080.10,
  "button":    0,
  "dir":       1
}
```

**Non-negotiables:**
- `mm` is **absolute**, counting from program start — never zeroed by a press.
  The app computes `position = mm − zeroMm` itself. If the mock ever resets
  `mm` to 0 on a press, it no longer matches what the real PCB is supposed to
  do, and every test built on it would be validating the wrong thing.
- `seq` must keep incrementing even while the strip is stationary. A frozen
  `seq` is how the app detects a dead feed — if the mock stops advancing it
  during a "line stopped" scenario, that scenario becomes untestable.
- Every response is well-formed JSON with all seven fields present, even
  during scripted odd behaviour (see §2). Malformed JSON isn't a test
  scenario the app needs to survive — the PCB is trusted to speak JSON
  correctly. What it must survive is *not answering at all*, which is
  different (§2).

## 2. What the app under test needs from this mock, beyond the contract

The app's `HttpEncoderSource` polls at a fixed rate (`Scan.Encoder.PollHz` —
default 50/sec) and needs to be provable to handle each of these:

1. **A steady line.** `mm` increasing at a constant rate.
2. **A variable line.** Speed changing over the course of a run — accelerate,
   cruise, decelerate — never a fixed rate.
3. **A stopped line.** `mm` frozen, `seq` still advancing. The app must fire
   zero captures during this, not a burst when it resumes.
4. **A slow response**, one-off or sustained — to test the app's
   `Scan.Encoder.TimeoutMs` handling (default 150 ms). Needs to be
   controllable per-request or for a duration, not just always-slow.
5. **No response at all** (dropped connection, or the process refusing new
   connections) for long enough to cross `Scan.Encoder.MaxStaleMs` (default
   300 ms) — to test that the app aborts the run rather than guessing.
6. **A frozen `seq`** while `mm` keeps changing, or vice versa — an
   inconsistent feed, distinct from scenario 5, to make sure the app is
   actually checking `seq` and not just checking "did a response arrive".
7. **A button press**, on command, at the current `mm` — `zeroCount++`,
   `zeroMm = mm` at that instant, nothing else resets.
8. **A second press mid-run** — the app is expected to treat this as "this
   strip is over", not silently re-base onto a new strip.
9. **A long run** — `mm` past several kilometres, to sanity-check that
   nothing silently loses precision (this is exactly the failure mode flagged
   for the real PCB in §5.5.2 of the build plan: a 32-bit float starts losing
   sub-millimetre resolution above roughly 30 m).

## 3. The prompt

Copy everything inside the fence below.

````
Build a small mock HTTP server that stands in for a physical encoder PCB
during development, so client code can be tested against real HTTP and real
timing before the actual hardware exists.

CONTRACT — implement this exactly, it is not negotiable:

One endpoint: GET /encoder, returns 200 OK, Content-Type: application/json,
with exactly these seven fields every time:

  seq        integer  increments on every internal sample, ALWAYS advances,
                       even while the simulated strip is stationary
  uptimeMs   integer  milliseconds since this program started
  mm         float    absolute travel in millimetres since program start;
                       NEVER reset by a button press
  zeroCount  integer  increments by 1 on every simulated button press
  zeroMm     float    the value of `mm` at the moment of the last press
                       (0 before any press has happened)
  button     integer  0 or 1, momentary: 1 only on a response that coincides
                       with a press, 0 otherwise
  dir        integer  -1 or 1, travel direction (1 unless a scenario reverses)

Example response:
{
  "seq": 4821, "uptimeMs": 128374, "mm": 52163.85,
  "zeroCount": 7, "zeroMm": 52080.10, "button": 0, "dir": 1
}

SIMULATION BEHAVIOUR:

- `mm` advances continuously based on wall-clock time and a current speed
  setting (mm/s), sampled fresh on every request rather than pre-computed on
  a timer, so the position is always accurate to when the request actually
  arrived.
- Starts at speed 0 and mm 0. Nothing moves until a scenario or control call
  sets a speed.
- `seq` increments on every internal sample tick, on an interval independent
  of HTTP requests (e.g. an internal ~50 Hz clock) — NOT only when polled —
  so a client polling slower than that still sees seq jump by more than 1
  between requests, and a stalled seq genuinely means the simulated PCB
  itself has stopped, not just that nobody asked recently.

CONTROL INTERFACE — a second set of endpoints (or a simple stdin command
loop if you prefer, but HTTP control endpoints are strongly preferred so a
test harness can drive scenarios programmatically), to manipulate the mock
while GET /encoder keeps serving normally:

  POST /control/speed        body: {"mmPerSec": <float>}
      Change the current speed. Negative values are allowed (reverse).

  POST /control/press
      Simulate a button press RIGHT NOW: zeroCount += 1, zeroMm = current mm.
      Does not touch mm itself.

  POST /control/jump         body: {"mm": <float>}
      Teleport the position (for exercising specific scenarios quickly
      without waiting for real elapsed time).

  POST /control/fault        body: {"mode": "none" | "hang" | "refuse" | "freeze-seq" | "freeze-mm", "durationMs": <int or omitted for indefinite>}
      "none"        — normal operation (also used to clear a prior fault)
      "hang"        — GET /encoder accepts the connection but never responds
                      until durationMs elapses (or indefinitely if omitted)
      "refuse"      — GET /encoder refuses/resets the connection immediately
      "freeze-seq"  — seq stops incrementing while mm keeps advancing normally
      "freeze-mm"   — mm stops advancing while seq keeps incrementing normally
                      (this is the "stopped line" scenario — the difference
                      from a fault is that the feed is still healthy, so this
                      one should probably NOT be under /control/fault at all;
                      expose it plainly as its own endpoint, e.g.
                      POST /control/pause and POST /control/resume, so it
                      reads as "the strip stopped", not "the encoder broke")

  POST /control/latency      body: {"delayMs": <int>}
      Add an artificial fixed delay before every response, without refusing
      or hanging outright, to test timeout-adjacent behaviour distinct from
      the two extremes above.

  GET  /control/status
      Returns the current simulated state as JSON: speed, mm, zeroCount,
      zeroMm, active fault mode if any, added latency — for a human or a
      test script to confirm what the mock is currently doing.

NON-FUNCTIONAL:

- HTTP/1.1 with keep-alive on GET /encoder (don't force a new TCP connection
  per request — the real PCB is expected to support this and the client is
  written expecting it).
- Runs on localhost on a configurable port (default suggestion: 5055),
  logs each GET /encoder to the console with mm, seq and elapsed time, and
  each control call with what changed — this is a development tool, verbose
  and inspectable is more valuable here than quiet.
- Single process, no external dependencies beyond what a minimal HTTP
  server needs. Prefer the language's own standard library HTTP server if it
  has one that's adequate (e.g. Python's http.server, Node's http module,
  C#'s HttpListener/minimal API) over pulling in a framework for something
  this small — but readability wins over minimalism if the standard library
  option is awkward.
- Print, on startup, the exact URL to GET (e.g. "Serving GET
  http://localhost:5055/encoder") plus a one-line summary of the control
  endpoints, so it's immediately usable without reading the source.

DELIVERABLE:

A single runnable program (one file if the language allows it comfortably)
plus a short README or top-of-file comment block covering: how to run it,
every endpoint with a curl example, and how to script the nine test
scenarios below using the control endpoints — spell out each one as a
concrete curl sequence, don't just point back at the endpoint list.

  1. Steady line: POST /control/speed 166.7, let it run.
  2. Variable line: several POST /control/speed calls at different values
     over the course of one run.
  3. Stopped line: POST /control/pause mid-run, confirm seq keeps advancing
     and mm does not, then /control/resume.
  4. One-off slow response: /control/latency for a short duration, confirm
     GET /encoder still eventually answers.
  5. Encoder fault (no response): /control/fault {"mode":"refuse"} or
     {"mode":"hang"} for longer than a client's expected stale-timeout.
  6. Inconsistent feed: /control/fault {"mode":"freeze-seq"} while mm still
     advances, and separately {"mode":"freeze-mm"} while seq still advances.
  7. Button press: /control/press, confirm zeroCount increments and zeroMm
     equals mm at that instant.
  8. Press again mid-run: two /control/press calls with movement between
     them.
  9. Long run: /control/jump to a large mm (several kilometres) and confirm
     the reported value stays precise to sub-millimetre.
````

---

## 4. Once you have it running

Point the app at it by changing two values in [config.json](../config.json):

```json
"Encoder": {
  "BaseUrl": "http://localhost:5055",
  "Path": "/encoder",
  ...
}
```

Leave `Scan.Simulate.Enabled` as `false` — that flag selects the in-process
`SimulatedEncoderSource` and skips HTTP entirely, which is *not* what you want
here. Talking to the mock over `http://localhost` is the whole point: it's
what actually exercises `HttpEncoderSource`'s HTTP client, timeouts and
staleness detection.

## 5. What's still needed on the app side

`Scan/HttpEncoderSource.cs` (task **C4** in
[STRIP_SCAN_BUILD_PLAN.md §8](STRIP_SCAN_BUILD_PLAN.md#8-my-tasks)) doesn't
exist yet — it's the piece that actually polls whatever this mock (or later,
the real PCB) serves, turns each response into an `EncoderSample`, and raises
`Faulted` when `seq` stalls or requests fail past `MaxStaleMs`. It implements
the same `IEncoderSource` interface `SimulatedEncoderSource` already does, so
none of the code built so far (`ScanController`, `ScanPipeline`,
`StripRunRecorder`) needs to change — it's a drop-in. Say the word once the
mock is running and I'll build it against these exact nine scenarios.
