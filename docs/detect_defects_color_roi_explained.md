# `detect_defects_color_roi.py` — Flow Explained

A **Streamlit web app** for detecting defects on copper strips by comparing colors
against a known-good reference region.

---

## Input → Output (high level)

- **Input:**
  - PNG images from a local `snaps_png/` folder
  - Two user-cropped regions (ROIs): a clean *reference* patch and a *target* area to inspect
  - Slider/radio settings (method, blur, thresholds, cleanup size)
- **Output:**
  - A count of detected defects
  - The target ROI with green bounding boxes around each defect
  - Optional heatmap / binary-mask visualizations

---

## Stage-by-stage

### Setup (lines 11–35)
- Checks that `snaps_png/` exists (stops the app if not).
- Lists all `.png` files and offers two sidebar dropdowns:
  - **Reference image** — the "golden" / good sample
  - **Target image** — the one to inspect
- `load_and_resize()` loads each with PIL and shrinks it to max 600px wide for display.

### Stage 1 — Define Regions (ROI selection) (lines 37–56)
- Uses `st_cropper` to let the user draw a box on each image:
  - **`ref_roi`** (blue box) — a *clean* patch of copper from the reference.
    Defines "what good copper looks like."
  - **`roi`** (green box) — the area on the target image to actually inspect.
- Both crops are converted to NumPy arrays (RGB).

### Stage 2 — Detection Algorithm (lines 58–138)
First, **noise reduction**: both ROIs get a Gaussian blur (lines 67–68) so texture
grain isn't mistaken for defects. Then one of three sidebar-selected methods produces
a `defect_mask`:

**A. Color Deviation (Euclidean)** — lines 73–90
- Computes the **average RGB color** of the *reference* ROI.
- For every pixel in the *target* ROI, computes Euclidean distance from that average
  color → a `distance_map`.
- Pixels whose distance exceeds the **Color Deviation Threshold** slider become
  defects (white in mask).

**B. Channel Statistics (Z-Score)** — lines 112–138
- For each R, G, B channel, learns the **mean and std-dev** from the reference ROI.
- For each target pixel, computes a Z-score `|pixel − mean| / std`.
- Any pixel exceeding the **Z-Score Threshold** in *any* channel is flagged
  (the three channel masks are OR'd together).

**C. Self-Reference Deviation (Legacy)** — lines 92–110
- Same as Euclidean, but uses the target ROI's *own* average color as the baseline
  (ignores the reference). Useful when the target is mostly good copper with small
  defects.

### Stage 3 — Filtering & Display (lines 140–174)
- **Morphological cleanup** (143–145): `OPEN` removes tiny speckle noise, then
  `DILATE` grows the remaining blobs. Kernel size is slider-controlled.
- **Find contours** (148) on the binary mask.
- For each contour with `area > 10` pixels, draw a **green bounding box** on a copy
  of the target ROI and increment `count` (153–158).
- **Visualization expander** (160–170): for deviation methods, shows a JET heatmap
  (blue=good, red=defect) plus the binary mask; for Z-score, shows the combined mask.
- **Final output** (172–174): the defect count and the annotated image.

---

## Data flow summary

```
PNG file ──▶ PIL resize ──▶ user crop (ROI)
                                 │
        ref_roi ──blur──▶ baseline color/stats
                                 │
        roi ─────blur──▶ per-pixel deviation/z-score ──▶ threshold ──▶ mask
                                                                          │
                                          morphology cleanup ──▶ contours ──▶ boxes + count
```

---

## Notable behavioral notes

- The detection re-runs on *every* widget interaction (Streamlit reruns top-to-bottom),
  so changing any slider instantly updates results — there's no explicit "Run" button.
- Images are read as RGB (PIL/NumPy), and OpenCV draws use `(0,255,0)`; since the array
  is RGB, that's correctly green — consistent throughout, so no channel-swap issue here.
