# Strip Scan — Build Plan (HTTP encoder, producer/consumer)

Companion to [CONTINUOUS_STRIP_SCAN_PLAN.md](CONTINUOUS_STRIP_SCAN_PLAN.md),
which covers the geometry and the *why*. This one is the **build plan**: the
flow in plain block diagrams, the settings, what the PCB must send, and **who
does which task — you or me**.

Nothing is implemented yet.

---

## 1. Decisions locked in so far

| Fact | What it settles |
|---|---|
| Encoder distance arrives **over HTTP from a PCB** | We poll an IP address. Contract in §5. IP lives in config. |
| Button pressed **when the strip has fully entered the FOV** | Frame 1 fires at `d = 0` — on the press itself. `FirstTriggerOffsetMm = 0`. |
| **PatchCore-R18 only** | R50 is out of the scan path. Lower inference time, one model to keep loaded. |
| Two-stage inspection is a **config toggle** | Off by default; flip it on if R18 can't hold the frame budget. §4.3 |
| Capture delay must be **settable and switchable** | `CaptureDelay.Enabled` + `CaptureDelay.Ms`, same file as the IP. §3.3 |
| PCB sends **millimetres directly**, already correct | No counts, no `MmPerCount`, no unit conversion anywhere in the app. §5.1 |
| Defect **position on the strip** must be stored | Every defect gets `startMm`/`endMm` in the database. §7 |
| FOV is **not yet known** — 10 cm was an example | Everything below is parametric. Measuring it is task **Y3**. |

---

## 2. What "pitch" means

**Pitch is how far the strip travels between one capture and the next.**

```
                  Pitch  =  Fov  −  Overlap

   Fov      = how much strip the camera sees in one frame (measure with a tape)
   Overlap  = deliberate re-imaging at each seam, so nothing falls in a crack
   Pitch    = the trigger interval, in millimetres of encoder travel
```

The whole scan loop is then one line:

```
   capture n fires when   d  =  (n − 1) × Pitch
   frame n covers strip   [ (n−1)×Pitch  ,  (n−1)×Pitch + Fov ]
```

`d` is encoder distance since the button press. Frame 1 at `d = 0`, frame 2 at
`d = Pitch`, frame 3 at `d = 2×Pitch`, and so on. **No timers anywhere** —
the line can speed up, slow down, or stop, and the frames still land on the
same places on the strip.

Pitch is **never stored in the config file** — it is computed from `Fov` and
`Overlap`. Storing all three invites them to disagree, and a disagreement here
means silent gaps in coverage.

### 2.1 Coverage picture

Example only, with `Fov = 100`, `Overlap = 20`, so `Pitch = 80`:

```
            0         100       200       300       400  mm
            ├─────────┼─────────┼─────────┼─────────┼──▶
  frame 1   [========]                                     0 – 100
  frame 2           [========]                            80 – 180
  frame 3                   [========]                   160 – 260
  frame 4                           [========]           240 – 340
                    ├┤
                    20 mm overlap at every seam, so a defect landing
                    on a boundary is whole in one of the two frames,
                    and no timing error can open an un-imaged gap
```

---

## 3. Sizing: plug in your real numbers

Earlier I ran the throughput numbers as if the FOV really were 10 cm. It was
your example figure, so treat this section as the **formulas plus a lookup
table**, and fill in the real FOV once you've measured it (Y3).

### 3.1 How to size the overlap

Overlap is **an absolute number of millimetres, not a percentage of the FOV.**
The things it has to absorb — position error and defect size — don't shrink
when the FOV shrinks.

```
   Overlap_min  =  v_max × (poll_interval + latency_residual)
                 + largest_defect_length
                 + safety_margin
```

At 166.7 mm/s (10 m/min), the first term alone:

| Capture delay compensation | Poll rate | Position error |
|---|---:|---:|
| **On** (residual ≈ 20 % of an 80 ms delay) | 50 Hz | **6.0 mm** |
| On | 20 Hz | 11.0 mm |
| **Off** (full 80 ms uncompensated) | 50 Hz | **16.7 mm** |
| Off | 20 Hz | 21.7 mm |

So: add your largest expected defect length and a margin on top. A starting
value of **25 mm** is reasonable for most cases and is what the table below
assumes. Note the payoff of the `CaptureDelay` toggle — leaving it **off costs
you ~10 mm of overlap**, which matters a lot at a small FOV and not at all at
a large one.

### 3.2 Throughput vs FOV

Overlap fixed at 25 mm, speed 166.7 mm/s:

| FOV | Pitch | Frames/sec | **Budget per frame** | Segments per 10 m | Overlap as % of FOV |
|---:|---:|---:|---:|---:|---:|
| 50 mm | 25 mm | 6.67 | **150 ms** | 400 | 50 % |
| 100 mm | 75 mm | 2.22 | **450 ms** | 133 | 25 % |
| 200 mm | 175 mm | 0.95 | **1050 ms** | 57 | 12 % |
| 300 mm | 275 mm | 0.61 | **1650 ms** | 36 | 8 % |
| 500 mm | 475 mm | 0.35 | **2850 ms** | 21 | 5 % |
| 1000 mm | 975 mm | 0.17 | **5850 ms** | 10 | 2 % |

**"Budget per frame" is the number that matters.** It is how long the consumer
has to inspect one frame before the queue starts backing up. Compare it
against your measured PatchCore-R18 time (task **Y1**) — that single
comparison decides whether we need two-stage inspection.

A small FOV is not free: it buys resolution and costs frame rate, storage, and
overlap efficiency, all roughly linearly.

### 3.3 Capture delay — what the setting does

Between "the encoder says we're at the trigger distance" and "the shutter
actually opens" there is real time: the HTTP poll, the pipe round trip to
`CameraBridge.exe`, and the camera's own latency. The strip does not wait.

```
   trigger decision                       shutter opens
        │                                       │
        │◀────────── CaptureDelay.Ms ──────────▶│
        │                                       │
   strip here                            strip has moved
                                         v × delay further on
```

`CaptureDelay.Enabled = true` makes the app fire the trigger **early** by that
distance, so the shutter opens at the right *place* rather than the right
*moment*. `false` disables the compensation entirely (fire exactly on the
threshold) — useful for A/B testing whether it's helping, and for a first
bring-up where you'd rather have one less moving part.

The value is **signed**, so it covers both directions:

- **positive** (normal) — there is latency, fire early to compensate;
- **negative** — a deliberate wait after the threshold, if it turns out the
  operator presses the button slightly early or the fixture sits upstream.

Measuring it is task **Y11**; I'll add the timing log that produces the number.

### 3.4 Motion blur

Not affected by any of the above corrections, and still worth checking on real
moving images (Y2):

```
   blur_px  =  speed_mm_per_s  ×  exposure_s  ×  px_per_mm

   where    px_per_mm = image_width_px / Fov_along_travel_mm
```

A smaller FOV means more px/mm, which means more blur for the same exposure.
Once you have the real FOV and sensor width, this is one multiplication — and
it's a lighting requirement, not a software one.

---

## 4. The flow

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
   │      - Overlap >= minimum safe overlap (3.1) ?                │
   │                                                               │
   │    any check fails ───▶ REFUSE TO START, name the failure     │
   └───────────────────────────┬──────────────────────────────────┘
                               │ all pass
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 3  STATE = ARMED                                             │
   │    poll the PCB, watch for the press marker to change        │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 4  OPERATOR PRESSES THE BUTTON                               │
   │    (strip has just fully filled the FOV)                     │
   │    app sees the marker change                                │
   │       ->  d = 0,  n = 1,  STATE = SCANNING                   │
   └───────────────────────────┬──────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ 5  POLL LOOP   (~50 Hz, its own thread)                      │ <──┐
   │      GET http://<ip>/encoder                                 │    │
   │      d      = distance since the press                       │    │
   │      speed  = change in d over recent samples                │    │
   │      d_now  = d + speed x (age of this reading)              │    │
   └───────────────────────────┬──────────────────────────────────┘    │
                               │                                       │
                               v                                       │
                    ╱──────────────────────╲                           │
                   ╱  d_now + lead >=        ╲        no                │
                   ╲  (n-1) x Pitch ?        ╱ ──────────────────────── ┤
                    ╲──────────────────────╱                           │
                               │ yes                                   │
                               │  (lead = speed x CaptureDelay.Ms,     │
                               │   or 0 when CaptureDelay.Enabled=false)│
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
                           │ PatchCore-R18        │    └─────────────┘
                           │ (+ optional 2-stage) │
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

**Why the queue must exist:** if the producer had to wait for inspection to
finish, one slow run would silently eat a trigger — and a whole pitch of strip
would go uninspected with nothing in the log to say so. The queue turns that
failure from *invisible* into a *logged drop*.

### C. Two-stage inspection (config toggle, off by default)

```
                        ┌──────────────────────┐
   frame from queue ───▶│ TwoStage.Enabled ?   │
                        └───┬──────────────┬───┘
                        no  │              │  yes
                            v              v
                 ┌────────────────┐   ┌──────────────────────┐
                 │ PatchCore-R18  │   │ cheap screen first   │
                 │ on every frame │   │ (ColorDiff)          │
                 └────────┬───────┘   └──────┬───────────────┘
                          │                  │
                          │            ┌─────┴──────┐
                          │       clean│            │suspicious
                          │            v            v
                          │      ┌──────────┐  ┌──────────────┐
                          │      │ pass,    │  │ PatchCore-R18│
                          │      │ no model │  │ confirms     │
                          │      │ run      │  └──────┬───────┘
                          │      └────┬─────┘         │
                          └───────────┴───────────────┘
                                      │
                                      v
                                 write report
```

Worth turning on only if R18 can't hold the frame budget (§3.2). Most strip is
good, so the cheap screen usually skips the model on the large majority of
frames. The trade-off is that a defect the screen misses never reaches the
model — so the screen must be tuned to over-trigger, not under-trigger.

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

---

## 5. What the PCB must send — spec for your PCB programmer

You said the JSON body might only carry distance. Here is what we want and
what each field buys, in priority order. **Everything is one endpoint,
`GET /encoder`, returning JSON.** Copy this section to them.

### 5.1 Tier 1 — required, we cannot work without these

| Field | Type | Meaning |
|---|---|---|
| `mm` | float | **Absolute travel in millimetres** since power-up. Already scaled and correct — the app does no conversion. Never reset per read, never "distance since last request". |
| `seq` | integer | Increments on every internal sample the PCB takes. |

> **The PCB sends true millimetres.** Counts-to-millimetres scaling lives on
> the PCB, so the app has no `MmPerCount` and does no arithmetic on the value.
> The one consequence: the encoder scale is now *their* correctness problem,
> and a wrong scale silently misplaces every defect position in the database
> without anything looking broken. So it still gets verified once on the line
> — task **Y4**, now a five-minute check rather than a calibration.

**Why `seq` is not optional:** without it, a frozen feed and a stopped line
look *identical* over HTTP — same number, poll after poll. One is fine, the
other means we're flying blind and must abort the run. `seq` is the only thing
that tells them apart. A monotonically increasing `uptimeMs` would do the same
job if that's easier for them.

### 5.2 Tier 2 — strongly wanted, saves real trouble

| Field | Type | Meaning |
|---|---|---|
| `zeroCount` | integer | Increments by 1 on **every button press**. This is how we detect "a new strip just started". |
| `zeroMm` | float | The value of `mm` **at the moment of that press**. |

**Why not just have the button zero the distance?** Because we're polling at
~50 Hz, we will routinely not be looking at the exact instant of the press. If
the PCB silently resets to zero, a lost or late response is indistinguishable
from a genuine zero, and the run starts some unknown distance off with nothing
to flag it.

With `zeroCount` + `zeroMm` we compute `d = mm − zeroMm` ourselves and get the
**exactly right** answer even if we missed the press entirely, dropped twenty
responses, or reconnected mid-strip. It costs the PCB two variables and
removes a whole class of "the run started 40 mm off and nobody noticed" bugs.
It is also the only subtraction the app does — there is no unit conversion
anywhere in the path.

### 5.3 Tier 3 — nice to have

| Field | Type | Meaning |
|---|---|---|
| `button` | 0/1 | Current button state, for a UI indicator. |
| `uptimeMs` | integer | PCB clock; lets us measure reading age and round-trip. |
| `dir` | −1/+1 | Travel direction, if the encoder is quadrature. |

### 5.4 Example response

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

### 5.5 Also please confirm with them

1. **Confirm `mm` is true millimetres**, already scaled from counts-per-rev
   and wheel circumference on their side. Tell them we do no conversion, so
   the scale is theirs to get right — and that we will verify it once against
   a 5 m tape run (Y4).
2. **Range and wrap behaviour** — how large can `mm` grow before it wraps or
   loses float precision, and what happens then. A `double` is fine for any
   realistic run length; a 32-bit float starts losing sub-millimetre
   resolution above ~30 m, which would quietly degrade defect positions.
3. **Max sustainable poll rate** — can it serve 50 requests/sec? 20?
4. **HTTP/1.1 with keep-alive**, so we're not opening a TCP connection per
   poll.
5. **Typical and worst-case response time.**
6. **Static IP or DHCP reservation** — a changing IP breaks every run.
7. Does the PCB **latch** the button press, or is it momentary? (Tier 2 makes
   this a non-issue, which is the point.)

### 5.6 If they can only send distance and nothing else

Then we need, at absolute minimum, `seq` added (§5.1). Without any way to
detect a stale feed we'd have to treat the encoder as untrustworthy, and the
honest response to an untrustworthy encoder is to refuse to run — a silent
fallback to a timer would produce clean-looking reports for strip nobody
actually measured. It's a small firmware change; worth pushing for.

---

## 6. Config file

All of this goes in `config.json` next to the existing settings
([PipelineConfig.cs](../PipelineConfig.cs)), re-read at the start of each run.

```json
"Scan": {
  "Encoder": {
    "BaseUrl":    "http://192.168.1.50",
    "Path":       "/encoder",
    "PollHz":     50,
    "TimeoutMs":  150,
    "MaxStaleMs": 300
  },

  "CaptureDelay": {
    "Enabled": true,
    "Ms":      80.0
  },

  "Geometry": {
    "FovAlongTravelMm":     0.0,
    "OverlapMm":           25.0,
    "FirstTriggerOffsetMm": 0.0,
    "TravelAxis":           "X",
    "TravelReversed":       false,
    "PxPerMmAlong":         0.0,
    "PxPerMmAcross":        0.0
  },

  "Inspection": {
    "Method": "PatchCore-R18",
    "TwoStage": {
      "Enabled":      false,
      "ScreenMethod": "ColorDiff"
    }
  },

  "Run": {
    "QueueCapacity":        6,
    "IdleTimeoutSec":      20.0,
    "StripLengthMm":          0,
    "MaxLineSpeedMmPerSec": 170.0,
    "StoreImagesFor":  "defects-only"
  },

  "Simulate": {
    "Enabled":       false,
    "SpeedMmPerSec": 166.7
  }
}
```

Notes on three of these:

- **`FovAlongTravelMm: 0.0`** is a deliberate "not measured yet". Pre-flight
  refuses to start at 0 rather than guessing — task Y3 fills it in.
- **`CaptureDelay.Enabled: false`** turns off lead compensation entirely.
  Remember from §3.1 that this costs about 10 mm of required overlap; the
  pre-flight check accounts for the setting automatically and will tell you if
  your overlap is now too small.
- **"Read every time" = re-read at ARM**, not per encoder sample. A file read
  at 50 Hz would be wasteful, and worse, a config change *mid-strip* would
  mean segment 60 used a different pitch from segment 1 — the coverage map
  would be quietly wrong. Re-reading at the start of each run gives exactly
  what you want (edit the file, next strip uses it, no restart) with no
  mid-run corruption. The UI shows the values in force for the current run,
  plus a **Reload Config** button.

---

## 7. What gets stored in the database

The point of the encoder is not just to time the captures — it is so that
**every defect can be reported as a position on the strip**. Your example
("a defect from 5 cm to 10 cm") becomes `startMm: 50.0, endMm: 100.0`.

### 7.1 Units: millimetres everywhere

One canonical unit in the database, always `double` millimetres. Centimetres
and metres are a *display* choice in the UI. Mixing units in stored data is
the classic way to end up with a defect reported at 5 mm that was really at
5 cm, so there is exactly one unit and no conversions anywhere in the path —
including from the PCB, which now sends true millimetres already (§5.1).

### 7.2 Three levels of record

```
   StripRun                       one document per strip
   ├── batchId, start/end time, total length
   ├── coverageComplete
   └── defects[]  <-- MERGED, deduped across overlaps: the authoritative
                      "where are the defects on this strip" answer
       │
       ├── DefectReport (segment 1)     one document per captured frame
       │   ├── segmentIndex, segmentStartMm, segmentEndMm
       │   ├── encoderMmAtCapture, triggerErrorMm, lineSpeedMmPerSec
       │   └── defects[]  <-- RAW, exactly as seen in this one frame
       │
       ├── DefectReport (segment 2)
       └── ...
```

**Why both raw and merged.** A defect sitting in an overlap band is genuinely
visible in two frames, so the per-frame records will legitimately list it
twice — that is correct and worth keeping for traceability back to an image.
But it must be *one* defect when the operator asks "what's wrong with this
strip". So the per-segment lists stay raw, and the `StripRun` list is merged
and deduped (task C11). Keep the raw ones; don't overwrite them.

### 7.3 The defect record

```csharp
public sealed class DefectBox
{
    // ── position along the strip: this is the "5 cm to 10 cm" ──
    [BsonElement("startMm")]  public double StartMm  { get; set; }
    [BsonElement("endMm")]    public double EndMm    { get; set; }

    // ── position across the strip width: which side is it on ──
    [BsonElement("acrossStartMm")] public double AcrossStartMm { get; set; }
    [BsonElement("acrossEndMm")]   public double AcrossEndMm   { get; set; }

    [BsonElement("areaMm2")]  public double AreaMm2 { get; set; }
    [BsonElement("score")]    public double Score   { get; set; }  // PatchCore anomaly score

    // ── raw pixel box, kept so a position can always be traced to an image ──
    [BsonElement("px")] public int PxX { get; set; }
    [BsonElement("py")] public int PxY { get; set; }
    [BsonElement("pw")] public int PxW { get; set; }
    [BsonElement("ph")] public int PxH { get; set; }
}
```

On the merged `StripRun.defects[]` entries, two more fields record where the
merged defect came from:

```csharp
[BsonElement("segments")]    public List<int> SourceSegments { get; set; } = new();
[BsonElement("mergedCount")] public int       MergedCount    { get; set; }
```

### 7.4 How the millimetres are computed

```
   StartMm = SegmentStartMm + ( PxX          / PxPerMmAlong )
   EndMm   = SegmentStartMm + ( (PxX + PxW)  / PxPerMmAlong )
```

and when `TravelReversed` is true, the pixel axis runs the other way:

```
   StartMm = SegmentStartMm + ( (ImageWidth − PxX − PxW) / PxPerMmAlong )
   EndMm   = SegmentStartMm + ( (ImageWidth − PxX)       / PxPerMmAlong )
```

`SegmentStartMm` is `(n−1) × Pitch` — the frame's own place on the strip, so a
defect's position is always absolute strip position, never "position within
frame 7".

`TravelAxis` and `TravelReversed` (§6) depend on how the camera is physically
bolted on. Getting them wrong reports every defect mirrored, which is worse
than reporting no position at all — so this gets verified once with a
deliberate mark on a test strip, as part of Y3.

### 7.5 Worked example

Camera FOV 100 mm, `Pitch` 80 mm, `PxPerMmAlong` 41. A defect found in
**segment 2** at pixel x = 410, width = 410:

```
   SegmentStartMm = (2 − 1) × 80          =  80.0 mm
   StartMm        = 80 + (410 / 41)       =  90.0 mm
   EndMm          = 80 + (820 / 41)       = 100.0 mm
```

→ stored as `startMm: 90.0, endMm: 100.0`, i.e. a defect from 9 cm to 10 cm
along the strip.

### 7.6 Indexes and the queries this enables

```
   reports:     { stripRunId: 1, segmentIndex: 1 }
   reports:     { "defects.startMm": 1 }
   strip_runs:  { batchId: 1, startedUtc: -1 }
   strip_runs:  { coverageComplete: 1 }
```

Which makes these one-liners:

- every defect on strip X, in order along the strip;
- all defects between 2000 mm and 3000 mm;
- all strips this shift whose coverage was incomplete;
- defect density per metre, for a trend chart.

That last one is only possible because position is stored per defect rather
than per frame — worth having even though nobody asks for it on day one.

---

## 8. Who does what

### 8.1 Your tasks

| # | Task | Why it's yours | Unblocks |
|---|---|---|---|
| **Y1** | **Time one PatchCore-R18 detection run** in the app as it is today. Just wall-clock seconds. | Decides whether one consumer holds the frame budget, and therefore whether `TwoStage` ships on or off. | §3.2 comparison |
| **Y2** | Capture frames of a **moving** strip at production speed; look at the blur. | Physical; only real images settle it. | Lens / lighting / exposure |
| **Y3** | **Measure the FOV along travel** with a tape. Mark both FOV edges on the guide rail. | Physical measurement. The rail marks make the button press repeatable instead of a judgement call. | `FovAlongTravelMm`, all of §3 |
| **Y4** | **Verify the PCB's millimetres are true millimetres**: mark the strip, run a tape-measured **5 m or more**, check the app's reading matches. Repeat 3×. | The PCB now does the scaling, so this is a check not a calibration — but a wrong scale there silently misplaces every defect position in the database. | Trustworthy defect positions |
| **Y5** | Get the PCB's **IP address**, made static or DHCP-reserved. | Network admin. | `BaseUrl` |
| **Y6** | **Send §5 to the PCB programmer**; get Tier 1 + Tier 2 confirmed. | Their firmware. | Everything encoder-side |
| **Y7** | Confirm camera **model, resolution, global vs rolling shutter, max fps, hardware trigger input?** | Spec sheet. Rolling shutter would skew a moving image and break the geometry. | §3.4 |
| **Y8** | Decide the **smallest defect that must be caught** and the **largest expected defect length** (mm). | Product decision. Drives px/mm and overlap. | `OverlapMm` |
| **Y9** | Answer §10. | Design decisions. | Phase 1 onward |
| **Y10** | Bench test with the simulator, then the first real strip. | Needs the line. | Go-live |
| **Y11** | Once C4 lands: read the timing log, put the number in `CaptureDelay.Ms`. | Needs the real PCB and camera. | Trigger accuracy |

### 8.2 My tasks

| # | Task | Depends on | Needs hardware? |
|---|---|---|---|
| **C1** | `ScanConfig` schema + loader + `Validate()`, wired into `config.json`, re-read at ARM | — | No |
| **C2** | `ScanGeometry` (pitch, thresholds, segment ranges, minimum-safe-overlap) + `ScanController` state machine with latch and no-burst guards | C1 | No |
| **C3** | `IEncoderSource` + **`SimulatedEncoderSource`** (software ramp + on-screen zero button) | C1 | No |
| **C4** | `HttpEncoderSource`: poll loop, interpolation, staleness guard, fault handling, timing log for Y11 | C3, Y5, Y6 | Partly — testable against a fake local endpoint |
| **C5** | Producer/consumer: bounded queue, capture thread, consumer thread, drop accounting | C2 | No |
| **C6** | Extract the UI-free `Inspect` engine out of `RunDetectionPipelineAsync` ([MainWindow.xaml.cs:664](../MainWindow.xaml.cs#L664)) so manual and scanned runs share one code path | — | No |
| **C7** | Two-stage inspection path behind the config toggle | C6 | No |
| **C8** | `StripRun` + `DefectBox` + extended `DefectReport` exactly as §7, Mongo writes and indexes | C6 | No |
| **C9** | Strip Scan UI panel: encoder status, live position/speed, ARM / End Strip, segment counter, Reload Config | C2, C3 | No |
| **C10** | Strip map: one tick per segment, green/red/grey, click to open that image | C8, C9 | No |
| **C11** | Pixel → millimetre mapping (§7.4) + overlap dedupe into the merged `StripRun.defects[]` (§7.2) | C8, Y3 | No |
| **C12** | Diagnostics: trigger-error log, dropped-segment counter, coverage-complete flag | C5, C8 | No |

Everything except C4 can be built and tested at a desk with the simulator.

---

## 9. Phases

| Phase | Contains | Done when |
|---|---|---|
| **0** ← next | C1 + C2 | Trigger math passes tests: variable speed, stop, reverse, jump-past-threshold |
| **1** | C3 + C5 | Simulator drives a full fake run; queue fills and drains; drops get logged |
| **2** | C6 + C7 | Manual Run Detection behaves identically to today; two-stage toggle works |
| **3** | C8 + C9 | A simulated run writes a complete `StripRun` with per-segment reports |
| **4** | C10 + C11 | Strip map shows defect positions in millimetres, clickable |
| **5** | C4 | Real PCB drives a real run |
| **6** | C12 + Y4 | Trigger error measured and stable; first production strip |

### 9.1 Phase 0 in detail — what I'd write next

Purely additive; nothing existing changes behaviour.

1. **`Scan/ScanConfig.cs`** — the nested settings classes from §6, with
   defaults, hanging off `PipelineConfig` as a `Scan` property so it lands in
   the existing `config.json`.
2. **`Scan/ScanConfig.Validate()`** — returns a list of human-readable
   problems: FOV unset, `Fov − Overlap <= 0`, overlap below the §3.1 minimum
   *for the current `CaptureDelay.Enabled` setting*, bad URL, `PxPerMm` unset
   (defect positions would be meaningless without it).
   This is what the pre-flight in step 2 of diagram A calls.
3. **`Scan/ScanGeometry.cs`** — pure functions, no state: `Pitch`,
   `ThresholdFor(n)`, `SegmentRange(n)`, `SegmentCountFor(lengthMm)`,
   `MinimumSafeOverlap(speed, pollHz, delayMs, delayEnabled)`.
4. **`Scan/ScanController.cs`** — the state machine (IDLE → ARMED → SCANNING →
   FINALISING), `OnSample()` returning "capture segment n" or nothing, with
   the latch (never re-fire an index) and the no-burst rule (if `d` jumped past
   several thresholds, fire once for where we actually are and log the skipped
   indices as gaps).
5. **Tests** for 3 and 4 — see the question below.

**One decision I need from you before starting:** there's no test project in
the solution today. The trigger math is exactly the kind of thing that should
have tests — the stop / reverse / jump-past-threshold cases are easy to get
subtly wrong and painful to debug on a live line. Options:

- **(a)** add a small `copperInspection.Tests` xUnit project to the solution —
  ~15 minutes, and gives us a permanent safety net; **my recommendation**
- **(b)** keep the math in a plain static class and I verify it with a
  throwaway console harness, leaving no test project behind.

### 9.2 What you can do while I write Phase 0

None of these depend on my code, and three of them feed straight into it:

1. **Y1** — time a PatchCore-R18 run. ~10 minutes, and it's the input to the
   `TwoStage` default.
2. **Y3** — measure the FOV. This is the single number the whole of §3 hangs
   on, and it's currently a guess.
3. **Y6** — send §5 to the PCB programmer. Longest lead time of anything here,
   so starting it today is worth more than it looks.
4. **Y5** — get the PCB IP pinned.
5. **Q1 and Q3** in §10 — two short answers.

---

## 10. Open questions

| # | Question | Why it matters |
|---|---|---|
| **Q1** | **"Max 10/min" — 10 metres per minute, or 10 strips per minute?** Every number in §3 assumes 10 m/min = 166.7 mm/s. | If it's strips, and a strip is 20 m, that's 200 m/min — **20× faster**, and every row of §3.2 changes. Would likely push this from an area camera to a line-scan camera. **Still the most important open question.** |
| **Q2** | What is the **real FOV**? (= task Y3) | Sets pitch, frame rate, storage and blur all at once. |
| **Q3** | How does the operator signal **end of strip** — button, known length, sensor, or just stop? | Determines when a run finalises and coverage is judged. |
| **Q4** | Can the PCB drive a **hardware trigger** into the camera, and does the camera have a trigger input? | Would remove HTTP latency and poll jitter entirely, and make `CaptureDelay` unnecessary. |
| **Q5** | Does one frame cover the **full strip width**? | If not, this becomes 2-D tiling and needs a second axis or a second camera. |
| **Q6** | Store images for **every** segment, or only defective ones? | At a small FOV this is tens of GB per shift. Default in §6 is `defects-only`. |
| **Q7** | Is the strip **laterally guided**, or does it wander? | Wander breaks pixel-exact reference diff; PatchCore tolerates it better. |

---

## 11. Status

Nothing implemented. Waiting on your go-ahead for **Phase 0 (C1 + C2)** and on
the test-project choice in §9.1.
