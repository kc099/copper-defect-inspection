# Converting `detect_defects_ref_gui.py` to a C# WPF App

A guide for porting the reference-based copper-strip defect detector from
Python / Streamlit / OpenCV to a Windows desktop app built with C# WPF and
**OpenCvSharp4**.

---

## 1. The translation map

The Streamlit script is Python + OpenCV (`cv2`) + a web UI. For WPF you
replace each layer:

| Streamlit / Python piece        | C# / WPF equivalent                                              |
| ------------------------------- | ---------------------------------------------------------------- |
| `cv2` (OpenCV)                  | **OpenCvSharp4** NuGet (`OpenCvSharp4` + `OpenCvSharp4.runtime.win`) |
| `numpy` array math              | `Mat` / `Cv2` operations (OpenCvSharp does it natively)          |
| `st.selectbox` for images       | `ComboBox` bound to the file list                                |
| `st.slider` / `st.number_input` | `Slider` / `TextBox` (or an `xceed` NumericUpDown)               |
| `st.checkbox`                   | `CheckBox`                                                       |
| `st.button("Run Detection")`    | `Button` + click handler                                         |
| `st.image(...)`                 | `Image` control (convert `Mat` → `BitmapSource`)                 |
| `st.spinner`                    | run work on a background `Task`, show a progress ring            |

The whole CV pipeline (ORB align → homography warp → blur → absdiff →
threshold → morphology → contours → filter → draw) ports almost
line-for-line into OpenCvSharp because the API names match: `Cv2.AbsDiff`,
`Cv2.Threshold`, `Cv2.FindContours`, `ORB.Create`, `Cv2.FindHomography`,
`Cv2.WarpPerspective`, etc.

---

## 2. Steps to build it yourself

1. **Create the project**
   `dotnet new wpf -n CopperDefectDetector`
   (or in Visual Studio: *WPF App*, target *.NET 8*).

2. **Add NuGet packages**
   `OpenCvSharp4`, `OpenCvSharp4.runtime.win`.
   Optional: `OpenCvSharp4.Extensions` for easy `Mat` → `BitmapSource`
   conversion via `WriteableBitmapConverter`.

3. **Build the layout** (`MainWindow.xaml`)
   A folder picker, two ComboBoxes (reference / target), the four setting
   controls, a Run button, and 3+ Image controls for results plus debug
   views.

4. **Write a `DefectDetector` class**
   Hold the pure CV pipeline here (no UI). It takes the reference `Mat`,
   target `Mat`, and settings, and returns the result `Mat` + defect count
   + debug `Mat`s.

5. **Add a `Mat` → `BitmapSource` helper**
   So results display in the WPF `Image` controls.

6. **Wire the Run button**
   Call the detector on a background thread and update the UI when done.

---

## 3. Prompt for Perplexity (or any code-gen AI)

Paste this to generate the WPF code templates. It captures the exact
pipeline and parameters so the generated code matches the original
behavior:

> Generate a complete C# WPF application (.NET 8) that performs
> reference-based image defect detection, using the **OpenCvSharp4**
> library. This is a port of a Python/Streamlit/OpenCV script. Please
> provide `MainWindow.xaml`, `MainWindow.xaml.cs`, a separate
> `DefectDetector.cs` class for the CV logic, and the list of required
> NuGet packages (`OpenCvSharp4`, `OpenCvSharp4.runtime.win`,
> `OpenCvSharp4.Extensions`).
>
> **UI requirements (XAML):**
> - A button to browse and select an image folder; load all `.png` files
>   from it.
> - Two ComboBoxes: "Reference (Golden) Image" and "Target Image to
>   Inspect", populated with the PNG filenames.
> - Detection settings controls:
>   - "Difference Threshold" — slider, range 10–100, default 25.
>   - "Min Defect Area (px)" — numeric input, default 10, minimum 1.
>   - "Max Aspect Ratio" — slider, range 1.0–10.0, default 4.0.
>   - "Ignore Brighter Defects (Reflections)" — checkbox, default
>     unchecked.
> - A "Run Detection" button.
> - Image display areas to show: the Reference, the Aligned Target, and the
>   Result (with defect boxes drawn), plus a collapsible/secondary area
>   showing two debug images: the raw grayscale difference map and the
>   final defect mask.
> - A label showing the detected defect count.
>
> **CV pipeline (in DefectDetector.cs, using OpenCvSharp4):**
> 1. Load reference and target images as `Mat`. Resize both to a width of
>    800 px, preserving aspect ratio (use `Cv2.Resize` with
>    `InterpolationFlags.Area`).
> 2. **Align** the target to the reference: convert both to grayscale, use
>    `ORB.Create(5000)` to detect keypoints and descriptors, match with
>    `BFMatcher` using `NormTypes.Hamming` and crossCheck=true, sort matches
>    by distance, keep the top 20%, extract matched point pairs, compute
>    homography with `Cv2.FindHomography(..., HomographyMethods.Ransac)`,
>    then `Cv2.WarpPerspective` the target to the reference's size. Handle
>    the case where descriptors are null or alignment fails gracefully.
> 3. Apply `Cv2.GaussianBlur` (5x5) to both the reference and the aligned
>    target.
> 4. Compute `Cv2.AbsDiff` of the two blurred images, then convert the
>    difference to grayscale.
> 5. Apply `Cv2.Threshold` (binary) using the Difference Threshold value.
> 6. Clean up with morphology: `MorphologyEx` Open then Dilate, using a 3x3
>    kernel.
> 7. `Cv2.FindContours` (external, simple approximation). For each contour:
>    compute area, skip if area ≤ Min Defect Area; compute aspect ratio as
>    max(w,h)/min(w,h) from the bounding rect and skip if it exceeds Max
>    Aspect Ratio (this removes thin reflection lines); if "Ignore Brighter
>    Defects" is checked, skip the contour when the mean brightness of the
>    target ROI exceeds the reference ROI mean by more than 30. For
>    surviving contours, draw a red rectangle on the result image and
>    increment the defect count.
> 8. Return the result image, the defect count, the grayscale difference
>    map, and the final filtered mask.
>
> **Other requirements:**
> - Run the detection on a background thread (`Task.Run`) so the UI stays
>   responsive, with a progress indicator while it runs.
> - Include a helper to convert OpenCvSharp `Mat` to WPF `BitmapSource` for
>   display.
> - Add basic error handling (e.g., no folder selected, fewer than 2
>   images, alignment failure) with message boxes.
> - Add brief comments explaining each pipeline stage.

---

## 4. Note on a quirk in the original

The Python original has a small bug: it computes the brightness-filter ROI
from the *unaligned* blurred target rather than the aligned one. The prompt
above says "aligned target" since that is almost certainly the intended
behavior. If you want a byte-for-byte port instead, change that step to use
the unaligned target ROI.
