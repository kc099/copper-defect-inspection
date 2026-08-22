# Required NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `OpenCvSharp4` | ≥ 4.9.0 | Managed .NET wrapper for OpenCV 4 |
| `OpenCvSharp4.runtime.win` | ≥ 4.9.0 | Native OpenCV Windows DLLs (x64) |
| `OpenCvSharp4.WpfExtensions` | ≥ 4.9.0 | `WriteableBitmapConverter` — converts `Mat` ↔ WPF `BitmapSource` |

## Installation (CLI)

```bash
dotnet add package OpenCvSharp4
dotnet add package OpenCvSharp4.runtime.win
dotnet add package OpenCvSharp4.WpfExtensions
```

## Notes

- Target platform **must be x64** (`<PlatformTarget>x64</PlatformTarget>`).  
  The native runtime package ships only 64-bit DLLs.
- If you need the older WinForms-style `BitmapConverter.ToBitmap()`, add  
  `OpenCvSharp4.Extensions` instead of (or alongside) `WpfExtensions`.  
  For pure WPF, `WpfExtensions` alone is sufficient.
- All three packages should use the **same version string** to avoid
  ABI mismatches between the managed wrapper and the native DLLs.
