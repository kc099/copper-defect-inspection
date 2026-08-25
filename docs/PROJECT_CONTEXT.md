# Project Context — Copper Strip Defect Inspection (WPF)

> **Purpose of this file:** paste this whole document into a new Claude chat as
> the first message when you want help extending or editing this app. It gives
> that chat everything it needs to get oriented without re-exploring the repo
> from scratch — what the app is, how it's built, what already works, what's
> stubbed out, and what's planned next. Point it at specific files with
> `[filename.cs](path)`-style requests once it has this context.
>
> For the chronology of how the app got to this point (what changed, in what
> order, and why), see [WORK_LOG.md](WORK_LOG.md) — that file also has a full
> step-by-step trace of the PatchCore-ResNet18 detection run. This file is
> the current-state snapshot; that one is the history.

---

## 1. What this app is

`copperInspection` is a **Windows desktop app (WPF, .NET, C#)** that inspects
copper strip images for surface defects. It's a **C#/WPF port of Python
prototypes** (`bg_subtraction_app.py` and `color_diff_inspector.py`, which
live outside this repo). Given one or two photos of a backlit component, it
isolates the component from its background, then flags defects either by
color deviation from a saved reference color or by anomaly score from a
PatchCore ML model.

There is a git repository (`main` branch); recent history: background
subtraction + color-diff pipeline was a full rewrite of an earlier
manual-ROI-cropper prototype, then PatchCore-ResNet18 detection was added on
top. See [WORK_LOG.md](WORK_LOG.md) for details.

---

## 2. Tech stack

| Layer | Choice |
|---|---|
| UI | WPF, `net10.0-windows`, `x64` |
| Computer vision | **OpenCvSharp4** (`OpenCvSharp4`, `.runtime.win`, `.WpfExtensions`) — see [NUGET_PACKAGES.md](NUGET_PACKAGES.md) |
| ML inference | **Microsoft.ML.OnnxRuntime** 1.29.0 — runs the PatchCore ResNet18 backbone |
| Storage | **MongoDB** (`MongoDB.Driver`), local instance at `mongodb://localhost:27017`, db `defect_detection_db`, collection `reports` |
| Camera SDK | none currently referenced — earlier camera-mode planning docs in `Assets/` are aspirational, not implemented |

Full package list/versions: [copperInspection.csproj](../copperInspection.csproj).

---

## 3. Repository layout

```
copperInspection/
├── copperInspection.csproj        # net10.0-windows, WPF, x64
├── config.json                    # hand-editable pipeline defaults (next to the .exe, not %AppData%)
├── App.xaml / App.xaml.cs         # stock WPF entry point, nothing custom
├── MainWindow.xaml(.cs)           # the one screen — folder/image pick, mode radios, run, results
├── ReportsWindow.xaml(.cs)        # gallery of saved inspection reports (MongoDB)
├── PipelineConfig.cs              # PipelineConfig model + ConfigStore (loads/saves config.json)
├── BackgroundSubtraction.cs       # Stage 1: isolate the component from its backlit background
├── Detection/
│   ├── ColorDiffDetector.cs       # Stage 2 option A: per-pixel distance from a saved reference colour
│   └── PatchCoreDetector.cs       # Stage 2 option B: ONNX PatchCore anomaly detector (ResNet18 backbone)
├── DefectDetector.cs              # now just ImageLoader.LoadResized() — shared image-loading helper
├── ReportStore.cs                 # MongoDB model (DefectReport) + queries
├── BytesToImageConverter.cs       # WPF IValueConverter: byte[] PNG -> Image thumbnail
├── Assets/
│   ├── reference_color.json / reference_color1.json   # saved reference BGR colour + ColorDiff params
│   ├── patchcore_resnet18_meta.json   # threshold + memory-bank dims for the R18 model
│   ├── patchcore_resnet18.onnx(.data) # ONNX backbone graph + weights — gitignored, local/shared-storage only
│   ├── patchcore_resnet18_bank.bin    # ~157 MB memory bank (102,400 × 384 float32) — gitignored
│   ├── CAMERA_PATCHCORE_INTEGRATION.md  # early design notes on camera mode (not implemented)
│   └── PATCHCORE_CODE_EXPLAINED.md      # line-by-line explanation of the Python PatchCore trainer/exporter
└── docs/
    ├── WORK_LOG.md                 # chronology + PatchCore-R18 run flowchart — read this too
    ├── PROJECT_CONTEXT.md          # this file
    ├── NUGET_PACKAGES.md           # OpenCvSharp4 package/version notes
    ├── WPF_Conversion_Guide.md     # historical: notes from the ORIGINAL manual-ROI port (superseded pipeline)
    ├── detect_defects_color_roi_explained.md  # historical: walkthrough of the superseded Streamlit script
    └── prompts.txt                 # (empty)
```

`WPF_Conversion_Guide.md` and `detect_defects_color_roi_explained.md`
describe the **manual-ROI-cropper pipeline that no longer exists** in this
repo (`RoiCropper`, `RedRoiExtractor`, the old `DefectDetector.Detect()` with
Euclidean/Z-score/legacy methods were all removed). They're kept for
historical reference only — don't use them to understand current behavior.

---

## 4. What actually works today

The app has **one screen, two independent pipeline stages**, each with two
interchangeable methods, both save results to MongoDB:

**Stage 1 — Background Subtraction** (radio group, `MainWindow.xaml`):
- **Silhouette** (default) — one image; the component is dark against a
  bright backlit field, so Otsu (or manual) threshold on the inverted
  grayscale isolates it. Optional illumination flattening, morphological
  cleanup, keep-N-largest-blobs, hole filling.
- **Reference-Image Diff** — two images (target + a background plate);
  `absdiff` after exposure-matching (median-brightness gain), then threshold
  + cleanup. Requires both images be pixel-aligned and the same size unless
  "Allow Resize" is set.

Implementation: [BackgroundSubtraction.cs](../BackgroundSubtraction.cs).

**Stage 2 — Detection method** (radio group):
- **Color Difference** (default) — every kept pixel's Euclidean distance from
  a saved reference BGR colour (`Assets/reference_color1.json`), thresholded,
  cleaned, verdict = `BAD` if the defect area exceeds a % of the component
  area. Produces a real heatmap + mask overlay.
  Implementation: [Detection/ColorDiffDetector.cs](../Detection/ColorDiffDetector.cs).
- **PatchCore — ResNet18** — ONNX-based anomaly detection against a
  pretrained memory bank; verdict = `BAD` if the worst 256×256-tile patch
  score exceeds a saved threshold. **Full step-by-step trace of this path is
  in [WORK_LOG.md §3](WORK_LOG.md#3-flow-chart--selecting-patchcore--resnet18-and-clicking-run-detection).**
  Implementation: [Detection/PatchCoreDetector.cs](../Detection/PatchCoreDetector.cs).
- **PatchCore — ResNet50** — radio button exists, wired to look for
  `patchcore_resnet50.*` assets, but those files don't exist yet; selecting
  it and running throws a caught `FileNotFoundException` (shown as a
  MessageBox, non-fatal). **Not usable yet.**

Click **Run Detection** (`RunBtn_Click` in
[MainWindow.xaml.cs](../MainWindow.xaml.cs)) runs both stages on a background
`Task`, then:
- Shows 4 result panels (original, background-subtracted, heatmap/distance,
  overlay/mask).
- Shows a red "BAD" or green "GOOD" badge with a score/percentage.
- Saves a `DefectReport` (result PNG + metadata) to MongoDB
  ([ReportStore.cs](../ReportStore.cs)) — fire-and-forget; failure only shows
  a status-bar message, never blocks the UI.
- Live-pushes the new report into an already-open Reports window
  (`Options → Reports`, [ReportsWindow.xaml.cs](../ReportsWindow.xaml.cs)) if
  one is open.

All pipeline defaults + the last-used folder persist to `config.json` next to
the `.exe` (see [PipelineConfig.cs](../PipelineConfig.cs)) — intentionally
hand-editable, not hidden in `%AppData%`.

---

## 5. Known gaps (see [WORK_LOG.md §4](WORK_LOG.md#4-known-gaps--rough-edges-worth-knowing-about) for detail)

- PatchCore's heatmap/overlay panels are currently a blank placeholder — the
  per-tile anomaly grids are computed but not stitched back into a visual map.
- PatchCore-ResNet50 has UI but no shipped model files.
- PatchCore's 256px tiling has no overlap and drops edge remainders.
- The ONNX + memory-bank files (~157 MB total) are gitignored — must be
  copied into `Assets/` manually on any machine that doesn't already have
  them; they don't come with `git clone`.
- No camera/live-stream mode exists yet — `Assets/CAMERA_PATCHCORE_INTEGRATION.md`
  is planning-only.

---

## 6. Data model (MongoDB)

`DefectReport` ([ReportStore.cs](../ReportStore.cs)):

| Field | Type | Notes |
|---|---|---|
| `Id` | ObjectId | Mongo `_id` |
| `Timestamp` | DateTime | stored UTC |
| `DefectCount` | int | `1` if verdict is BAD, else `0` (not a real defect count anymore — both current methods produce a single whole-image verdict) |
| `Method` | string | `"ColorDiff"`, `"PatchCore-R18"`, or `"PatchCore-R50"` |
| `Mode` | string | `"Silhouette"` or `"ReferenceDiff"` (background-subtraction method used) |
| `ReferenceImage` / `TargetImage` | string | filenames only; `ReferenceImage` empty unless Reference-Image-Diff mode was used |
| `ImageData` | byte[] | overlay result PNG, raw bytes |
| `ResultImageBase64` | string | same PNG as a `data:image/png;base64,...` URI |

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
For PatchCore-R18 to work, `Assets/patchcore_resnet18.onnx`,
`.onnx.data`, and `patchcore_resnet18_bank.bin` must be present on disk
(gitignored — see §5).

---

## 8. Conventions worth knowing before editing

- `Mat` (OpenCvSharp) ownership: results from `BackgroundSubtraction`/
  `ColorDiffDetector`/`PatchCoreDetector` are `IDisposable` — always dispose
  once you've extracted what you need. `MainWindow.RunBtn_Click` reads
  scalar/verdict fields *before* disposing the Mats it built for display.
- UI dark theme is hand-rolled in `MainWindow.xaml`'s `<Window.Resources>`
  (custom `ComboBox`/`Button` control templates) rather than a theme library
  — match that style if adding controls.
- `config.json` persistence (`PipelineConfig.cs`) is intentionally
  best-effort (swallows I/O exceptions) — keep new persisted fields
  optional/nullable-safe so old config files don't break on upgrade.
- Large model artifacts (`*.onnx`, `*.onnx.data`, `*.pt`, `*.pth`, `*.pkl`,
  `Assets/*.bin`) are gitignored on purpose (GitHub's 100 MB/file limit) —
  don't try to `git add -f` them; document where they live on shared storage
  instead.
- `PatchCoreDetector` caches one loaded model per backbone name
  (`GetOrLoadDetector` in `MainWindow.xaml.cs`) — switching between R18/R50
  disposes and reloads; switching back and forth repeatedly reloads the ONNX
  session + 157 MB bank each time, which is slow. Fine for now given only R18
  is usable.

---

## 9. Suggested next steps

1. **Implement the real PatchCore heatmap** — stitch `ComputeAnomalyScores`'s
   per-tile grids back into a full-image anomaly map instead of returning a
   blank Mat (see `WORK_LOG.md` §4 for exactly where this is stubbed).
2. **Export and ship PatchCore-ResNet50** assets so that radio option is
   actually usable.
3. **Overlapping/full-coverage tiling** for `Extract256Patches` so edge
   regions aren't silently skipped.
4. **Camera / live-stream mode** — still just planning docs in `Assets/`;
   would need a camera SDK dependency added and a capture loop written from
   scratch.
