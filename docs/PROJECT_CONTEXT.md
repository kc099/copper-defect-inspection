# Project Context — Copper Strip Defect Inspection (WPF)

> **Purpose of this file:** paste this whole document into a new Claude chat as
> the first message when you want help extending or editing this app. It gives
> that chat everything it needs to get oriented without re-exploring the repo
> from scratch — what the app is, how it's built, what already works, what's
> stubbed out, and what's planned next. Point it at specific files with
> `[filename.cs](path)`-style requests once it has this context.

---

## 1. What this app is

`copperInspection` is a **Windows desktop app (WPF, .NET, C#)** that inspects
copper strip images for surface defects by comparing a target image against a
known-good reference image. It's a **C#/WPF port of a Python/Streamlit/OpenCV
prototype** (`detect_defects_color_roi.py` / `detect_defects_ref_gui.py`,
which live outside this repo). The porting rationale and translation map are
in [WPF_Conversion_Guide.md](WPF_Conversion_Guide.md).

There is no git repository initialized here yet (`git init` has not been run).

---

## 2. Tech stack

| Layer | Choice |
|---|---|
| UI | WPF, `net10.0-windows`, `x64` |
| Computer vision | **OpenCvSharp4** (`OpenCvSharp4`, `.runtime.win`, `.WpfExtensions`) — see [NUGET_PACKAGES.md](NUGET_PACKAGES.md) |
| Storage | **MongoDB** (`MongoDB.Driver`), local instance at `mongodb://localhost:27017`, db `defect_detection_db`, collection `reports` |
| Camera SDK | **Baumer neoAPI** 1.6.0 — referenced in the `.csproj` but **not yet used anywhere in code** (see §5) |
| ML (planned) | PatchCore anomaly-detection model, exported to `Assets/patchcore.onnx` — **not yet wired into the app** (see §5) |

Full package list/versions: [copperInspection.csproj](../copperInspection.csproj).

---

## 3. Repository layout

```
copperInspection/
├── copperInspection.csproj        # net10.0-windows, WPF, x64
├── App.xaml / App.xaml.cs         # stock WPF entry point, nothing custom
├── MainWindow.xaml(.cs)           # the one real screen — see §4
├── ReportsWindow.xaml(.cs)        # gallery of saved inspection reports (MongoDB)
├── RoiCropper.xaml(.cs)           # reusable draggable/resizable ROI-box control
├── DefectDetector.cs              # pure CV pipeline (no UI) — the core logic
├── RedRoiExtractor.cs             # HSV red-strip auto-segmentation (for the disabled "Auto ROI" mode)
├── ReportStore.cs                 # MongoDB model (DefectReport) + queries
├── Settings.cs                    # JSON settings persisted to %AppData%\copperInspection\settings.json
├── BytesToImageConverter.cs       # WPF IValueConverter: byte[] PNG -> Image thumbnail
├── Assets/
│   ├── memory_bank.pkl            # trained PatchCore model (Python pickle — not usable from C# as-is)
│   ├── patchcore.onnx             # PatchCore ALREADY exported to ONNX (37 MB) — ready for ONNX Runtime, unused so far
│   ├── patchcore_onnx_meta.json   # preprocessing recipe + threshold for the ONNX model (see §5.2)
│   ├── CAMERA_PATCHCORE_INTEGRATION.md  # design doc: camera mode + PatchCore options
│   └── PATCHCORE_CODE_EXPLAINED.md      # line-by-line explanation of the Python PatchCore trainer/exporter
└── docs/
    ├── WPF_Conversion_Guide.md            # Python→WPF porting notes + codegen prompt used to bootstrap this app
    ├── NUGET_PACKAGES.md                  # OpenCvSharp4 package/version notes
    ├── detect_defects_color_roi_explained.md  # walkthrough of the ORIGINAL Streamlit script this app replicates
    ├── prompts.txt                        # (empty)
    └── PROJECT_CONTEXT.md                 # this file
```

---

## 4. What actually works today (Manual mode)

The app currently has **one working end-to-end flow**: manual, offline,
folder-based image inspection.

1. **Browse Folder** — pick any image in a folder; the app lists all
   `.png/.jpg/.jpeg/.bmp/.tif/.tiff/.webp` files in that folder
   ([MainWindow.xaml.cs](../MainWindow.xaml.cs) `LoadImagesFromFolder`).
2. Pick a **Reference** and a **Target** image from two dropdowns. Each loads
   into a [RoiCropper](../RoiCropper.xaml.cs) — a draggable/resizable ROI box
   the user drags over the "good copper" patch (reference, blue box) and the
   "area to inspect" (target, green box), mirroring the original Streamlit
   `st_cropper` widget.
3. Pick a **Detection Method** (radio buttons, mutually exclusive):
   - **Color Deviation (Euclidean)** — mean RGB of the reference ROI vs.
     per-pixel Euclidean distance in the target ROI.
   - **Channel Statistics (Z-Score)** — per-channel mean/std from the
     reference ROI; flag pixels whose Z-score exceeds a threshold in *any*
     channel.
   - **Self-Reference (Legacy)** — same as Euclidean but the target ROI is
     compared against *its own* mean color (reference ROI ignored).
4. Tune sliders (blur, threshold, morphology kernel, min contour area) and hit
   **Run Detection**. The pipeline (blur → per-method distance/Z-score →
   threshold → morphology open+dilate → find contours → filter by area →
   draw green boxes) runs on a background `Task` — see
   [DefectDetector.cs](../DefectDetector.cs) `Detect()`.
5. Results render in the right-hand image grid, a defect-count badge shows,
   and a `DefectReport` (result PNG + metadata) is saved to MongoDB
   ([ReportStore.cs](../ReportStore.cs)) — fire-and-forget, failure only
   shows a status-bar message, never blocks the UI.
6. **Options → Reports** opens a modeless gallery window
   ([ReportsWindow.xaml.cs](../ReportsWindow.xaml.cs)) showing the last 6
   reports or a date-range query, with per-card view/download and
   download-all. New detections push live into an already-open Reports
   window via `AddLiveReport`.
7. All slider/radio/folder state persists to
   `%AppData%\copperInspection\settings.json` between launches
   ([Settings.cs](../Settings.cs)).

Behavioral note ported from the original: the original Streamlit script has a
small bug where the brightness-filter ROI uses the *unaligned* target rather
than the aligned one — see §4 of
[WPF_Conversion_Guide.md](WPF_Conversion_Guide.md#4-note-on-a-quirk-in-the-original)
if exact parity with the Python original ever matters.

---

## 5. What's designed but NOT wired up yet

### 5.1 "Auto Red ROI" pipeline mode
[DefectDetector.cs](../DefectDetector.cs) and
[RedRoiExtractor.cs](../RedRoiExtractor.cs) already fully implement a second
`PipelineMode.AutoRoi` — HSV segmentation that auto-finds a red strip in the
frame (instead of a user-dragged ROI) and adds aspect-ratio + glare
(brightness) filtering on top of area filtering. **The UI toggle for it is
commented out** in [MainWindow.xaml:256-261](../MainWindow.xaml). Turning it
back on is mostly a XAML uncomment + wiring `RadioAuto` — the C# and detection
logic are already there and already branch on `p.Mode`.

### 5.2 Camera / live-stream mode + PatchCore anomaly detection
The two-camera image grid in `MainWindow.xaml` (`Cam1TargetImage` /
`Cam1ResultImage` / `Cam2TargetImage` / `Cam2ResultImage`) is laid out to
anticipate a **live camera mode**, but today Cam2 just mirrors Cam1's
manual-mode result — there is no actual second camera feed, and `neoAPI`
(Baumer camera SDK, already in the `.csproj`) is **not referenced anywhere in
the C# code yet**.

The plan (fully written up in
[Assets/CAMERA_PATCHCORE_INTEGRATION.md](../Assets/CAMERA_PATCHCORE_INTEGRATION.md)):

| Mode | Input | Pipeline |
|---|---|---|
| Manual (done) | Folder-browsed image | `DefectDetector.Detect()` — no changes needed |
| Camera (not built) | Live Baumer neoAPI frames | **PatchCore** anomaly detection against a pretrained memory bank |

PatchCore background, in brief (full explanation in
[Assets/PATCHCORE_CODE_EXPLAINED.md](../Assets/PATCHCORE_CODE_EXPLAINED.md)):
a ResNet18 backbone turns an image into a 28×28 grid of 384-dim "patch
fingerprints"; each patch's anomaly score is its distance to the nearest
fingerprints in a memory bank built from known-good images; the image score is
the worst patch's score; `score > threshold` ⇒ defect, with a heatmap for
free.

**Current state of that model:**
- `Assets/memory_bank.pkl` — the original Python-trained model (pickle, not
  loadable from .NET).
- `Assets/patchcore.onnx` (already exported, ~37 MB) + `Assets/patchcore_onnx_meta.json`
  — a **self-contained ONNX graph** (backbone + memory bank + k-NN scoring +
  threshold baked in) that **is** loadable from .NET via
  `Microsoft.ML.OnnxRuntime` (not yet added to the `.csproj`). Per
  `patchcore_onnx_meta.json`: input `float32 (1,3,224,224)` RGB,
  ImageNet-normalized (resize short side to 256 → center-crop 224 → `/255` →
  normalize mean `[0.485,0.456,0.406]` std `[0.229,0.224,0.225]`); outputs
  `score`, `anomaly_map (224×224)`, `threshold` (= `0.6286286314328512`);
  verdict is `score > threshold`.
- This is **"Option B" from the integration doc** (ONNX + C#, no Python at
  runtime) and the model-export half of it is already done — what remains is
  the C# side: `Microsoft.ML.OnnxRuntime` NuGet, an image-preprocessing
  helper matching the meta.json recipe exactly, and wiring `InferenceSession`
  results into a live camera loop. The doc also describes an alternative
  "Option A" (Python sidecar process) if that's ever preferred instead —
  weigh both against current needs before picking; Option B's model is
  already exported so it's the shorter path today.
- Camera capture itself (`neoAPI`) still needs to be written from scratch:
  grab frame → PatchCore inference → update live image + GOOD/BAD badge +
  heatmap overlay → on defect, save a `DefectReport` to MongoDB and
  live-push it to an open Reports window, all on a background task so the UI
  thread stays responsive. See §8 of the integration doc for the exact wiring
  checklist.

---

## 6. Data model (MongoDB)

`DefectReport` ([ReportStore.cs](../ReportStore.cs)):

| Field | Type | Notes |
|---|---|---|
| `Id` | ObjectId | Mongo `_id` |
| `Timestamp` | DateTime | stored UTC |
| `DefectCount` | int | |
| `Method` | string | enum name, e.g. `ColorDeviationEuclidean` |
| `Mode` | string | `"Manual"` or `"Auto"` |
| `ReferenceImage` / `TargetImage` | string | filenames only |
| `ImageData` | byte[] | result PNG (boxes drawn), raw bytes |
| `ResultImageBase64` | string | same PNG as a `data:image/png;base64,...` URI for quick viewing outside the app |

Connection string / db / collection names are constants at the top of
`ReportStore.cs` — change there if Mongo runs elsewhere. **A local MongoDB
instance must be running** for report saving/loading to work; the app
degrades gracefully (status-bar message, no crash) if it isn't.

---

## 7. Build & run

Requires the .NET 10 SDK (Windows) and Visual Studio or `dotnet` CLI, x64.

```powershell
dotnet build copperInspection.csproj -c Debug
dotnet run --project copperInspection.csproj
```

For MongoDB-backed features (saving/viewing reports), a local MongoDB server
must be reachable at `mongodb://localhost:27017` (see §6 to point elsewhere).
No camera or Python runtime is required for anything that currently works —
those are only needed once §5.2 is implemented.

---

## 8. Conventions worth knowing before editing

- `Mat` (OpenCvSharp) ownership: `DefectDetector.Detect()` does **not** dispose
  the ref/target Mats passed in (caller owns them — `MainWindow` caches
  `_refMat`/`_targetMat` and disposes on window close or when swapping
  images). `DetectionResult` **does** own and dispose its own Mats via
  `IDisposable` — always call `.Dispose()` (or scope with `using`) once
  you've extracted what you need (see how `MainWindow.RunBtn_Click` reads
  scalar fields *before* disposing `result`).
- UI dark theme is hand-rolled in `MainWindow.xaml`'s `<Window.Resources>`
  (custom `ComboBox`/`Button` control templates) rather than a theme library
  — match that style if adding controls.
- Settings persistence (`Settings.cs`) is intentionally best-effort (swallows
  I/O exceptions) — keep new persisted fields optional/nullable-safe so old
  settings files don't break on upgrade.
- `RadioManual` is currently forced checked in code
  (`ApplySettings`/`MainWindow` constructor) since Auto mode's radio button is
  commented out — remember to re-enable the persisted `Mode` setting's other
  branch if you restore Auto mode in the UI.

---

## 9. Suggested next steps (pick based on what's asked)

1. **Re-enable Auto Red ROI mode** — uncomment `RadioAuto` in
   `MainWindow.xaml`, confirm `Mode_Checked`/`CollectSettings` round-trip it
   correctly. Low effort, logic already exists.
2. **Wire up `Microsoft.ML.OnnxRuntime` + `Assets/patchcore.onnx`** for a
   "test single image against PatchCore" button, before tackling live camera
   — validates preprocessing correctness against `patchcore_onnx_meta.json` in
   isolation.
3. **Baumer neoAPI camera capture** — a minimal live-preview window/mode
   first (no inference), then fold in PatchCore scoring per the pipeline
   diagram in `CAMERA_PATCHCORE_INTEGRATION.md`.
4. **Second camera / dual-cam support** — `Cam2*` UI elements exist but need
   a real second camera source; currently just mirrors Cam1.
