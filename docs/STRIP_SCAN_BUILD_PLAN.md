# Strip Scan — Build Plan (HTTP encoder, 10 cm FOV, producer/consumer)

Companion to [CONTINUOUS_STRIP_SCAN_PLAN.md](CONTINUOUS_STRIP_SCAN_PLAN.md),
which covers the geometry and the *why*. This one is the **build plan**: what
the new facts change, the flow in plain block diagrams, and **who does which
task — you or me**.

Nothing here is implemented yet. This is the agreed shape before code.

---

## 1. What you told me, and what it settles

| New fact | What it settles |
|---|---|
| Encoder distance arrives **over HTTP from a PCB** | We poll an IP address. Needs a request/response contract (§4) and an IP in config. Replaces the serial/MCU design in the older doc. |
| Button is pressed **when the strip has fully come into the FOV** | **This resolves the off-by-one-FOV question.** Your convention means `FirstTriggerOffsetMm = 0` — frame 1 fires immediately at the press. See below. |
| FOV is more like **10 cm**, not 1 m | Changes the throughput numbers by ~10×. This is the big one — §2. |
| Frames keep arriving → **producer/consumer loops** | Confirms the queue design. Producer captures and tags; consumer feeds the pipeline one frame at a time. §5. |
| FOV length and IP must be **editable in production** | Config file, re-read at the start of every run. §6. |

### The trigger formula is now simple

Because the button is pressed when the strip **already fills** the FOV, the
material under the camera at that instant is strip `0 … Fov`. So:

```
    capture n fires at    d = (n − 1) × Pitch
    where                 Pitch = Fov − Overlap
    frame n covers strip  [ (n−1)×Pitch , (n−1)×Pitch + Fov ]
```

Frame 1 fires at `d = 0` — on the button press itself, exactly as you
described. No offset needed. (The older doc's §2.1 kept both conventions open;
yours is the `Offset = 0` one, and I'll hard-default it to that.)

---

## 2. ⚠ Reality check: 10 cm FOV is ~10× harder than 1 m

I ran the numbers before writing this, because a 100 mm FOV changes what is
and isn't feasible. At **10 m/min = 166.7 mm/s**:

### 2.1 Throughput

| FOV | Overlap | Pitch | Frames/sec | Time between frames | Segments per 10 m |
|---:|---:|---:|---:|---:|---:|
| 1000 mm | 80 | 920 mm | 0.18 | 5520 ms | 11 |
| 100 mm | 10 | 90 mm | 1.85 | 540 ms | 111 |
| **100 mm** | **20** | **80 mm** | **2.08** | **480 ms** | **125** |
| 100 mm | 25 | 75 mm | 2.22 | 450 ms | 133 |

**The consumer now has ~480 ms to inspect one frame.** With a 1 m FOV it had
5.5 seconds. If PatchCore inference takes longer than 480 ms, the queue backs
up and segments get dropped — i.e. strip goes uninspected.

> **This is the first thing to measure** (task Y1 below). Time one detection
> run in the app you already have. If it is over ~400 ms, we need a plan —
> options in §5.3, none of them hard, but the choice changes the design.

### 2.2 The position error budget no longer fits in a 10 mm overlap

| Error source | At 166.7 mm/s |
|---|---:|
| HTTP poll interval @ 20 Hz | 8.33 mm |
| HTTP poll interval @ 50 Hz | 3.33 mm |
| Capture latency 50 ms | 8.33 mm |
| Capture latency 150 ms | 25.00 mm |

A 10 % overlap on a 100 mm FOV is only 10 mm — **smaller than the plausible
error**, which means gaps: strip that was never imaged but that the report
counts as inspected. Two fixes, use both:

1. **Overlap 20–25 mm** (20–25 %), not 10 %. Costs ~12 % more frames.
2. **Interpolate between HTTP polls** (§4.3) instead of using the last polled
   value raw. This removes the poll-interval error almost entirely and is
   about five lines of code.

### 2.3 Motion blur is the hard physical constraint

`blur_px = speed × exposure × px_per_mm`, and a 100 mm FOV means a *high*
px/mm — which is great for seeing small defects and brutal for blur:

| Sensor width across 100 mm FOV | px/mm | 5 ms | 2 ms | 1 ms | 0.5 ms | 0.2 ms |
|---|---:|---:|---:|---:|---:|---:|
| 2048 px | 20.5 | 17.1 px | 6.8 px | 3.4 px | 1.7 px | 0.7 px |
| 4096 px | 41.0 | 34.1 px | 13.7 px | 6.8 px | 3.4 px | 1.4 px |

So exposure needs to be roughly **0.2–1 ms**, which is a *lighting*
requirement, not a software one. The good news: at 20–41 px/mm a 0.5 mm defect
is still 10–20 px wide, so you can probably tolerate 3–4 px of blur — but you
have to decide that deliberately, by looking at real images of a **moving**
strip, not a stationary one (task Y2).

### 2.4 Image storage

125 segments per 10 m strip. At ~500 KB per PNG that is **60 MB per 10 m of
strip**, every strip, into MongoDB. A shift's worth will be tens of GB.

Recommendation: store the full image **only for segments with a defect**; for
good segments store the metadata and a small thumbnail. Decision needed
(question Q6).

---

## 3. The flow

### A. One strip, start to finish

```
   ┌──────────────────────────────────────────────────────────────┐
   │ 1  OPERATOR ARMS THE RUN                                     │
   │    types Batch ID, presses ARM                               │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 2  RELOAD CONFIG FROM DISK  +  PRE-FLIGHT CHECKS             │
   │      - config.json re-read here, so edits take effect         │
   │      - camera connected and streaming?                        │
   │      - PCB answering at http://<ip>/encoder ?                 │
   │      - Fov - Overlap > 0 ?                                    │
   │      - Overlap >= worst-case position error ?                 │
   │                                                               │
   │    any check fails ───▶ REFUSE TO START, show which one       │
   └───────────────────────────┬──────────────────────────────────┘
                               │ all pass
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 3  STATE = ARMED                                             │
   │    poll the PCB, watch for zeroCount to change               │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 4  OPERATOR PRESSES THE BUTTON                               │
   │    (strip has just filled the FOV)                           │
   │    PCB records zeroCounts and bumps zeroCount                │
   │    app sees the change  ->  d = 0,  n = 1,  STATE = SCANNING │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 5  POLL LOOP   (~50 Hz, its own thread)                      │ <──┐
   │      GET http://<ip>/encoder                                 │    │
   │      d = (counts - zeroCounts) x MmPerCount                  │    │
   │      speed = change in d over recent samples                 │    │
   │      d_now = d + speed x (age of this reading)               │    │
   └───────────────────────────┬──────────────────────────────────┘    │
                               │                                       │
                               v                                       │
                    ╱──────────────────────╲                           │
                   ╱  d_now + lead >=        ╲        no                │
                   ╲  (n-1) x Pitch ?        ╱ ──────────────────────── ┤
                    ╲──────────────────────╱                           │
                               │ yes                                   │
                               v                                       │
   ┌──────────────────────────────────────────────────────────────┐    │
   │ 6  PRODUCER: capture segment n                               │    │
   │      grab full frame from camera                             │    │
   │      tag it with n, d at capture, speed, timestamp           │    │
   │      push onto the queue  (never waits for inspection)       │    │
   │      n = n + 1     <-- latch, this n can never fire again    │ ───┤
   └───────────────────────────┬──────────────────────────────────┘    │
                               │                                       │
                               v                                       │
                    ╱──────────────────────╲                           │
                   ╱   strip finished?      ╲        no                │
                   ╲   (End button /         ╱ ───────────────────────┘
                    ╲   length / timeout)   ╱
                     ╲────────────────────╱
                               │ yes
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 7  FINALISE THE RUN                                          │
   │      wait for the queue to drain                             │
   │      write StripRun: segment count, defects,                 │
   │      CoverageComplete = no drops / no failures / no gaps      │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 8  SHOW RESULT: strip map + pass/fail + defect positions      │
   └──────────────────────────────────────────────────────────────┘
```

The loop `5 → 6 → 5` is the whole answer to "how do I capture the next part".
**Nothing in it knows how fast the strip is moving** — that is the entire
point of triggering on distance.

### B. Producer / consumer

```
   PCB (HTTP)              SCAN CONTROLLER
   ┌──────────┐            ┌───────────────────┐
   │ encoder  │  poll      │ trigger math      │
   │ + button │ ─────────▶ │ "capture n now"   │
   └──────────┘  ~50 Hz    └─────────┬─────────┘
                                     │
                                     v
   CAMERA                  PRODUCER  (thread 1)          QUEUE
   ┌──────────┐            ┌──────────────────────┐   ┌─────────────┐
   │ Baumer   │  frame     │ CaptureFullFrame()   │   │ [#][#][ ]   │
   │ via      │ ─────────▶ │ tag with n, d, speed │──▶│ bounded, 6  │
   │ Bridge   │            │ push and go back     │   │ FULL = drop │
   │ .exe     │            │ (never blocks)       │   │  + LOG LOUD │
   └──────────┘            └──────────────────────┘   └──────┬──────┘
                                                             │
                                                             v
                           CONSUMER (thread 2)         ┌─────────────┐
                           ┌──────────────────────┐    │ one frame   │
                           │ Inspect engine       │ ◀──│ at a time   │
                           │ ColorDiff / PatchCore│    └─────────────┘
                           └──────────┬───────────┘
                                      │
                        ┌─────────────┴─────────────┐
                        v                           v
                 ┌─────────────┐            ┌────────────────┐
                 │  MongoDB    │            │ Reports window │
                 │ reports +   │            │ + strip map    │
                 │ strip_runs  │            └────────────────┘
                 └─────────────┘
```

**Why the queue must exist:** the producer has ~480 ms between triggers. If it
had to wait for inspection to finish, one slow PatchCore run would silently
eat a trigger — and 80 mm of strip would go uninspected with nothing in the
log to say so. The queue makes that failure *visible* (a logged drop) instead
of *invisible*.

### C. What the frames actually cover

`Fov = 100 mm`, `Overlap = 20 mm`, so `Pitch = 80 mm`:

```
            0         100       200       300       400  mm
            ├─────────┼─────────┼─────────┼─────────┼──▶
  frame 1   [========]                                     0 – 100
  frame 2           [========]                            80 – 180
  frame 3                   [========]                   160 – 260
  frame 4                           [========]           240 – 340
                    ├┤
                    20 mm overlap at every seam, so a defect
                    landing on a boundary is whole in one of
                    the two frames, and no timing error can
                    open an un-imaged gap
```

### D. Why polls get interpolated

```
  real position ────────────────────────────────────────▶ (continuous)

  HTTP polls        x           x           x           x
                    │           │           │           │
                  20ms        20ms        20ms        20ms
                    │◀── 3.3 mm of travel between polls ──▶│

  naive:   use the last polled value      -> up to 3.3 mm late, always late
  better:  d_now = d_polled + speed x (time since that poll arrived)
                                          -> error drops to well under 1 mm
```

Same trick, applied again, covers the capture latency: fire the trigger early
by `speed × CaptureLatencyMs`, so the shutter opens at the right *place*
rather than the right *moment*.

---

## 4. The PCB HTTP contract

This is the part **you** (or whoever builds the PCB firmware) has to provide.
Here is the contract I would like to code against.

### 4.1 Endpoint

```
GET http://<pcb-ip>/encoder        ->  200 OK, application/json
```

```json
{
  "seq":        4821,
  "uptimeMs":   128374,
  "counts":     1043277,
  "zeroCount":  7,
  "zeroCounts": 1041200,
  "button":     0
}
```

| Field | Meaning | Why it must be there |
|---|---|---|
| `seq` | increments every internal sample | Lets the PC detect a **frozen feed**. Without it, a stuck value is indistinguishable from a stopped line — a silent, dangerous failure. |
| `uptimeMs` | PCB's own clock | Second staleness check, and lets the PC measure round-trip age. |
| `counts` | **absolute** quadrature counts since power-up | Never a pre-zeroed distance — see §4.2. |
| `zeroCount` | +1 on every button press | The PC watches this to detect "new strip started". |
| `zeroCounts` | value of `counts` at the last press | The PC computes `d` itself; a missed poll can't lose the zero. |
| `button` | current button state | Diagnostics / UI indicator. |

### 4.2 Why absolute counts, and why the PCB remembers the zero

The obvious design is "button press zeroes the counter, PC reads distance".
**Don't do that with HTTP polling.** At 50 Hz you will regularly not be
polling at the exact moment of the press, and if a response is lost you can
never tell whether a small value means "just zeroed" or "reading is stale".

With absolute counts plus a remembered `zeroCounts`, the PC computes
`d = (counts − zeroCounts) × MmPerCount` and gets the **exactly correct**
answer even if it missed the press entirely, reconnected mid-strip, or dropped
twenty responses in a row. This one decision removes a whole category of
"the run started 40 mm off and nobody noticed" bugs.

### 4.3 Polling behaviour on the PC side

- One dedicated poll thread, one reused `HttpClient` with keep-alive.
- `PollHz` = 50 (configurable). Short timeout (~150 ms); a slow response must
  never stall the loop.
- Interpolate between polls (diagram D).
- **Staleness guard:** if `seq` stops advancing, or no successful response for
  `MaxStaleMs`, treat it as an encoder fault → **abort the run and alarm**.
- **Never fall back to a timer.** A timer assumes constant speed, which is
  exactly the assumption the encoder exists to remove; a silent fallback would
  produce a clean-looking report for strip nobody actually measured.

> **Better still, if the PCB team is willing:** let the PCB do the triggering.
> Tell it the pitch, and have it fire a hardware trigger line into the camera
> when it crosses each threshold. That removes HTTP latency, poll jitter and
> lead compensation in one move. Worth asking (question Q4), but the polling
> design above works without it.

---

## 5. Producer / consumer detail

### 5.1 Producer (one thread)

Does exactly three things per trigger: capture, tag, enqueue. Nothing else —
no inspection, no database, no UI. If it can't enqueue because the queue is
full, it logs a dropped segment and marks the run incomplete, then carries on.

Tagged with each frame: segment index, encoder `d` at capture, estimated
speed, monotonic timestamp, and `TriggerErrorMm` (actual `d` minus the nominal
threshold — a free quality signal; if it starts drifting, something is wrong).

### 5.2 Consumer (one thread)

Takes one frame at a time, runs the inspection engine, writes the report,
raises an event for the UI. **One** consumer, not several: the PatchCore
detector is shared state and I'd rather serialise access than assume the ONNX
session is safe to call concurrently.

### 5.3 If the consumer can't keep up with 480 ms

Options, cheapest first — we pick once you've measured (task Y1):

1. **Widen the pitch** — a smaller overlap or a larger FOV. Fewer frames/sec.
2. **Two-stage inspection** — run cheap ColorDiff on every frame, and only run
   PatchCore on frames that look suspicious. Usually the biggest win, since
   most strip is good.
3. **Parallel consumers** — needs one ONNX session per consumer (memory) and
   careful ordering when writing results.
4. **Smaller model / GPU** — PatchCore-R18 instead of R50, or CUDA execution.

### 5.4 Memory

A 4096×3000 BGR frame is ~36 MB. Queue of 6 ≈ 220 MB of `Mat` buffers held at
once. Keep the bound small and dispose promptly. If frames are larger, lower
the capacity.

---

## 6. Config file

Everything that can change on the line goes in `config.json`, alongside the
existing settings ([PipelineConfig.cs](../PipelineConfig.cs)):

```json
"Scan": {
  "Encoder": {
    "BaseUrl":     "http://192.168.1.50",
    "Path":        "/encoder",
    "PollHz":      50,
    "TimeoutMs":   150,
    "MaxStaleMs":  300,
    "MmPerCount":  0.05
  },
  "Geometry": {
    "FovAlongTravelMm":     100.0,
    "OverlapMm":             20.0,
    "FirstTriggerOffsetMm":   0.0,
    "TravelAxis":            "X",
    "TravelReversed":        false,
    "PxPerMmAlong":          41.0,
    "PxPerMmAcross":         41.0
  },
  "Timing": {
    "CaptureLatencyMs":    80.0,
    "LeadCompensation":    true,
    "MaxLineSpeedMmPerSec": 170.0
  },
  "Run": {
    "QueueCapacity":       6,
    "IdleTimeoutSec":     20.0,
    "StripLengthMm":         0,
    "StoreImagesFor":  "defects-only"
  },
  "Simulate": {
    "Enabled":              false,
    "SpeedMmPerSec":        166.7
  }
}
```

Two deliberate choices:

- **`Pitch` is not in the file.** It's derived as `Fov − Overlap`. Storing both
  a pitch and the values it comes from invites them to disagree, and a
  disagreement here means silent gaps.
- **"Read it every time" = re-read at ARM**, not on every encoder sample. A
  file read at 50 Hz would be terrible, and worse, a config change *mid-strip*
  would mean segment 60 used a different pitch from segment 1 — the coverage
  map would be quietly wrong. Re-reading at the start of each run gives you
  exactly what you want ("edit the file, next strip uses it") with no restart
  and no mid-run corruption. The UI will show the values in force for the
  current run, plus a **Reload Config** button.

---

## 7. Who does what

### 7.1 Your tasks — physical, hardware, and decisions I can't make

| # | Task | Why it's yours | Blocks |
|---|---|---|---|
| **Y1** | **Time one detection run** in the current app (ColorDiff and PatchCore-R50/R18). Just note the wall-clock seconds. | Decides whether one consumer can keep up with 480 ms, and therefore the whole consumer design (§5.3). | §5.3 choice |
| **Y2** | Capture a few frames of a **moving** strip at production speed and look at the blur. | Blur is physical; only real images settle it (§2.3). | Lens/lighting/exposure |
| **Y3** | Measure the FOV along travel with a tape; mark both FOV edges on the guide rail. | Physical measurement. The rail marks make the button press repeatable instead of a judgement call. | `FovAlongTravelMm` |
| **Y4** | Calibrate `MmPerCount`: mark the strip, run **5 m or more**, record counts, divide. Repeat 3×. | Needs the line. Long run because error divides by distance. | `MmPerCount` |
| **Y5** | Get the PCB's **IP address**, and have it made static / DHCP-reserved. | Network admin. A changing IP breaks every run. | `BaseUrl` |
| **Y6** | Give the PCB team the §4.1 contract and confirm they can serve it (especially `seq`, `zeroCount`, `zeroCounts`). | Their firmware. I can't write to your PCB. | Everything encoder-side |
| **Y7** | Confirm camera **model, resolution, global vs rolling shutter, max fps, hardware trigger input?** | Spec sheet / physical. Rolling shutter would skew a moving image and break the geometry. | §2.3, Q4 |
| **Y8** | Decide the **smallest defect that must be caught** (mm). | Product decision. Drives px/mm, blur budget and overlap. | Overlap sizing |
| **Y9** | Answer the open questions in §9. | Design decisions. | Phase 1 |
| **Y10** | Bench test with the simulator, then the first real strip. | Needs the line. | Go-live |

### 7.2 My tasks — all the code

| # | Task | Depends on | Testable without hardware? |
|---|---|---|---|
| **C1** | `ScanConfig` schema + loader + validation, wired into `config.json`, re-read at ARM | — | Yes |
| **C2** | Trigger math + `ScanController` state machine (ARMED → SCANNING → FINALISING), latch and no-burst guards | C1 | Yes — unit tests |
| **C3** | `IEncoderSource` interface + **`SimulatedEncoderSource`** (software ramp + on-screen zero button) | C1 | Yes |
| **C4** | `HttpEncoderSource`: poll loop, interpolation, staleness guard, fault handling | C3, Y5, Y6 | Partly — I can test against a fake local endpoint |
| **C5** | Producer/consumer: bounded queue, capture thread, consumer thread, drop accounting | C2 | Yes |
| **C6** | Extract the UI-free `Inspect` engine out of `RunDetectionPipelineAsync` ([MainWindow.xaml.cs:664](../MainWindow.xaml.cs#L664)) so manual and scanned runs share one code path | — | Yes |
| **C7** | `StripRun` model + extended `DefectReport` (segment index, start/end mm, trigger error, speed) + Mongo writes | C6 | Yes |
| **C8** | Strip Scan UI panel: encoder status, live position/speed, ARM / End Strip, segment counter, Reload Config | C2, C3 | Yes |
| **C9** | Strip map: one tick per segment, green/red/grey, click a tick to open that image | C7, C8 | Yes |
| **C10** | Defect pixel → strip-coordinate mapping, and overlap dedupe across adjacent segments | C7, Y3 | Yes |
| **C11** | Diagnostics: trigger error log, dropped-segment counter, coverage-complete flag | C5, C7 | Yes |
| **C12** | Reference PCB firmware sketch implementing §4.1, if useful to your PCB team | Y6 | N/A |

**C1–C3, C5–C9 need no encoder and no line** — the simulator stands in, so
most of the build can happen at a desk before the hardware is ready.

### 7.3 Suggested order

```
   Y1 ──▶ decides §5.3          (do this first, it's 10 minutes)
   Y3, Y5, Y7 ──▶ C1
                   │
                   ├──▶ C2 ──▶ C3 ──▶ C5 ──▶ C8      <- all desk work,
                   │                    │              simulator only
                   └──▶ C6 ──────────▶ C7 ──▶ C9
                                        │
   Y6 ──▶ C4 ──────────────────────────┤
   Y4 ────────────────────────────────▶ C10, C11 ──▶ Y10 first strip
```

---

## 8. Build phases

| Phase | Contains | Done when |
|---|---|---|
| **0** | C1 + C2 | Trigger math passes unit tests: variable speed, stop, reverse, jump-past-threshold |
| **1** | C3 + C5 | Simulator drives a full fake run, queue fills and drains, drops are logged |
| **2** | C6 | Manual Run Detection produces byte-identical results to today |
| **3** | C7 + C8 | A simulated run writes a complete `StripRun` with per-segment reports |
| **4** | C9 + C10 | Strip map shows defect positions in millimetres, clickable |
| **5** | C4 | Real PCB drives a real run |
| **6** | C11 + Y4 calibration | Trigger error measured and stable; first production strip |

---

## 9. Open questions

| # | Question | Why it matters |
|---|---|---|
| **Q1** | **"Max 10/min" — 10 metres per minute, or 10 strips per minute?** All of §2 assumes 10 m/min. | If it's strips, and a strip is 20 m, that's 200 m/min = 3333 mm/s — **20× faster**. At 100 mm FOV that's 42 frames/sec and every number in §2 fails. This changes the answer from "area camera" to "line-scan camera". **Most important question here.** |
| **Q2** | Is the FOV really ~10 cm, or was that just an example? | 100 mm vs 1000 mm is a 10× difference in frame rate, storage and blur. |
| **Q3** | How does the operator signal **end of strip**? Button, known length, sensor, or just stop? | Determines when a run finalises and coverage is judged. |
| **Q4** | Can the PCB drive a **hardware trigger** into the camera, and does the camera have a trigger input? | Would eliminate HTTP latency and poll jitter entirely (§4.3). |
| **Q5** | Does one frame cover the **full strip width**? | If not, this becomes 2-D tiling and needs a second axis or second camera. |
| **Q6** | Store images for **every** segment, or only defective ones? | 125 segments per 10 m ≈ 60 MB/strip if you keep them all (§2.4). |
| **Q7** | Is the strip **laterally guided**, or does it wander side to side? | Wander breaks pixel-exact reference diff; PatchCore tolerates it better. |

**Q1 and Q2 gate everything else** — if the speed or the FOV is very different
from what I've assumed, the throughput section needs redoing before any code
gets written.

---

## 10. Status

Nothing implemented. Say the word and I'll start at **C1** (config schema and
trigger math), which is self-contained, needs no hardware, and gives you
something you can unit-test the same day.
