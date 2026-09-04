# Reports Window — Fixed 6 Live Slots + Scrollable Date-Range View

**Goal**

1. **Live view** = exactly **6 fixed frames** (2 rows x 3 columns). When a new
   detection is saved it goes *into* the six frames and the oldest one falls
   out. The number of frames never grows and the grid never reshapes.
2. **Date-range view** = *all* reports in the chosen range (can be far more than
   6), laid out in a scrollable gallery with readable card sizes.

**What is wrong today**

| Where | Problem |
|---|---|
| [ReportsWindow.xaml.cs:117-125](../ReportsWindow.xaml.cs#L117-L125) | `AddLiveReport` does `_items.Insert(0, report)` and never trims, so the list grows without limit. |
| [ReportsWindow.xaml.cs:120](../ReportsWindow.xaml.cs#L120) | It then calls `SetGridShape(_items.Count)`, so the whole grid reshapes (3 -> 4 -> 5 columns) and every card shrinks on each new result. |
| [ReportsWindow.xaml.cs:31-38](../ReportsWindow.xaml.cs#L31-L38) | `SetGridShape` squeezes *N* cards into one non-scrolling screen, so a 200-report date range becomes a 15x14 grid of unreadable thumbnails. |
| — | There is no notion of "which view am I in", so a live result is appended even while the user is browsing a date range. |
| — | Every kept report holds a PNG `byte[]` plus a decoded `BitmapImage`. Unbounded growth is a slow memory leak on a long production run. |

The fix is a **view-mode switch**: `Live` (capped at 6, fixed panel) vs `Range`
(unbounded, scrolling panel).

---

## Step 1 — Add a mode enum + state fields

In `ReportsWindow.xaml.cs`, at the top of the class (replacing lines 15-38):

```csharp
private const int LiveSlots = 6;          // was RecentCount

private enum GalleryMode { Live, Range }
private GalleryMode _mode = GalleryMode.Live;

// How many live results arrived while the user was browsing a date range.
private int _pendingLive;

private readonly ObservableCollection<DefectReport> _items = new();
```

**Delete** `GridRows`, `GridColumns`, `SetGridShape`, and (optionally) the whole
`INotifyPropertyChanged` implementation — the two panel templates in Step 2
replace them. If you would rather keep `INotifyPropertyChanged` for later, just
leave it unused; nothing binds to it after Step 2.

---

## Step 2 — Two ItemsPanel templates in XAML

In `ReportsWindow.xaml`, inside `<Window.Resources>` (after the `ReportCard`
template, around line 92), add:

```xml
<!-- LIVE: exactly six frames filling the area, never scrolls, never reshapes -->
<ItemsPanelTemplate x:Key="LivePanel">
    <UniformGrid Rows="2" Columns="3"/>
</ItemsPanelTemplate>

<!-- RANGE: fixed-size cards that wrap and scroll vertically -->
<ItemsPanelTemplate x:Key="RangePanel">
    <WrapPanel Orientation="Horizontal" ItemWidth="290" ItemHeight="310"/>
</ItemsPanelTemplate>
```

Then replace the gallery block
([ReportsWindow.xaml:141-160](../ReportsWindow.xaml#L141-L160)) with a version
that wraps the `ItemsControl` in a `ScrollViewer`:

```xml
<Border Grid.Row="1" Background="#0A0A16" CornerRadius="8"
        BorderBrush="#2A2A44" BorderThickness="1">
    <Grid>
        <ScrollViewer x:Name="GalleryScroll"
                      VerticalScrollBarVisibility="Disabled"
                      HorizontalScrollBarVisibility="Disabled">
            <ItemsControl x:Name="ReportsItems"
                          ItemTemplate="{StaticResource ReportCard}"
                          ItemsPanel="{StaticResource LivePanel}"
                          Margin="6"/>
        </ScrollViewer>

        <TextBlock x:Name="EmptyLabel" Text="No reports to show."
                   Foreground="#4A4A6A" FontSize="14"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   Visibility="Collapsed"/>
    </Grid>
</Border>
```

Notes:

- `HorizontalScrollBarVisibility="Disabled"` is what makes the `WrapPanel`
  actually wrap at the viewport width instead of running off to the right.
- With `VerticalScrollBarVisibility="Disabled"` in Live mode the `UniformGrid`
  is given the viewport height, so the six cards stretch to fill the area
  exactly as they do now.
- **Remove** the `GridRows` / `GridColumns` bindings from the old `UniformGrid`;
  those properties are gone from the code-behind.

---

## Step 3 — Switch panel + scrolling per mode

Add one helper to `ReportsWindow.xaml.cs`:

```csharp
private void ApplyMode(GalleryMode mode)
{
    _mode = mode;
    bool live = mode == GalleryMode.Live;

    ReportsItems.ItemsPanel = (ItemsPanelTemplate)FindResource(live ? "LivePanel" : "RangePanel");
    GalleryScroll.VerticalScrollBarVisibility =
        live ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    if (live) { _pendingLive = 0; UpdateLiveHint(); }
}
```

---

## Step 4 — Live load fills the six slots (and pads them)

Replace `LoadRecentAsync` (lines 59-64):

```csharp
private async Task LoadRecentAsync()
{
    ApplyMode(GalleryMode.Live);
    await RunQueryAsync("Live — Last 6 Runs", () => ReportStore.QueryRecentAsync(LiveSlots));
}
```

In `RunQueryAsync` (lines 83-108) drop the `SetGridShape` call and pad in Live
mode, so the six frames exist even before six results have been captured:

```csharp
List<DefectReport> reports = await query();
foreach (DefectReport r in reports) _items.Add(r);

if (_mode == GalleryMode.Live)
    while (_items.Count < LiveSlots) _items.Add(DefectReport.Placeholder());

int real = reports.Count;
CountLabel.Text = _mode == GalleryMode.Live
    ? $"Live · {real} of {LiveSlots} slots filled."
    : $"{real} report(s) shown.";
EmptyLabel.Visibility    = real == 0 && _mode == GalleryMode.Range
                           ? Visibility.Visible : Visibility.Collapsed;
DownloadAllBtn.IsEnabled = real > 0;
```

In Live mode the empty label is not needed — the six empty frames say it better.
Keep `EmptyLabel` for Range mode only.

---

## Step 5 — The actual fix: `AddLiveReport` replaces instead of appends

Replace lines 117-125 with:

```csharp
/// <summary>
/// Push a freshly-detected report into the six live slots. The newest result
/// takes the first frame and the oldest one drops off the end — the slot count
/// never changes. Ignored while the user is browsing a date range.
/// </summary>
public void AddLiveReport(DefectReport report)
{
    if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddLiveReport(report)); return; }

    if (_mode == GalleryMode.Range)
    {
        _pendingLive++;                 // don't disturb the range the user is reading
        UpdateLiveHint();
        return;
    }

    _items.Insert(0, report);
    while (_items.Count > LiveSlots) _items.RemoveAt(_items.Count - 1);   // drop oldest / placeholder

    int real = _items.Count(r => !r.IsPlaceholder);
    CountLabel.Text          = $"Live · {real} of {LiveSlots} slots filled.";
    EmptyLabel.Visibility    = Visibility.Collapsed;
    DownloadAllBtn.IsEnabled = true;
}
```

This is the core behaviour change: **`Insert(0)` + trim to 6**, and **no grid
reshape**. Because the collection length is pinned at `LiveSlots`, the
`UniformGrid` keeps its 2x3 shape forever and the images that fall off the end
are released for GC, which also fixes the unbounded memory growth.

`Dispatcher.CheckAccess()` is defensive: `SaveReportAsync`
([MainWindow.xaml.cs:911-923](../MainWindow.xaml.cs#L911-L923)) is fire-and-forget
from the UI thread today, so the continuation returns to the UI thread — but
that is easy to break later.

> **Alternative (one cell changes at a time).** If you would rather have a single
> frame repaint per result instead of all six shifting, use a ring index:
> `_items[_ring] = report; _ring = (_ring + 1) % LiveSlots;` with the collection
> pre-filled with 6 placeholders. Positionally stable, but "newest" moves around
> the grid. The shift version above is recommended — newest is always top-left.

---

## Step 6 — Placeholder frames

So that empty slots look like slots rather than blank background, add to
`DefectReport` in [ReportStore.cs](../ReportStore.cs):

```csharp
[BsonIgnore] public bool IsPlaceholder { get; init; }

public static DefectReport Placeholder() => new() { IsPlaceholder = true };
```

Then in the `ReportCard` DataTemplate add a trigger that swaps a placeholder
card to an empty look:

```xml
<DataTemplate.Triggers>
    <DataTrigger Binding="{Binding IsPlaceholder}" Value="True">
        <Setter TargetName="CardBody"    Property="Opacity"    Value="0.35"/>
        <Setter TargetName="CardActions" Property="Visibility" Value="Collapsed"/>
        <Setter TargetName="CardCount"   Property="Visibility" Value="Hidden"/>
    </DataTrigger>
</DataTemplate.Triggers>
```

Give the outer `Grid` `x:Name="CardBody"`, the defect-count `TextBlock`
`x:Name="CardCount"`, and the button `StackPanel` `x:Name="CardActions"`.

Also guard the handlers so placeholders are skipped:
`if (r.IsPlaceholder || r.ImageData.Length == 0) return;` in
[CardView_Click](../ReportsWindow.xaml.cs#L133) and
[CardDownload_Click](../ReportsWindow.xaml.cs#L164), and
`if (r.IsPlaceholder || r.ImageData.Length == 0) continue;` in the
`DownloadAll_Click` loop ([line 198](../ReportsWindow.xaml.cs#L198)).

*Skip this whole step if you are happy with fewer-than-6 results simply leaving
blank cells* — the fixed 2x3 `UniformGrid` from Step 2 already keeps the layout
stable on its own.

---

## Step 7 — Date range shows everything, and says when live results are waiting

`LoadRange_Click` (lines 66-81) only needs the mode switch:

```csharp
ApplyMode(GalleryMode.Range);
string header = $"{fromLocal:dd MMM yyyy}  →  {toLocal.AddDays(-1):dd MMM yyyy}";
await RunQueryAsync(header,
    () => ReportStore.QueryByDateAsync(fromLocal.ToUniversalTime(), toLocal.ToUniversalTime()));
```

`QueryByDateAsync` is already unlimited, so the range view shows every match; the
`WrapPanel` + `ScrollViewer` from Step 2 keep the cards readable and scrollable.

Add a hint label under `CountLabel` in the left panel
([ReportsWindow.xaml:125](../ReportsWindow.xaml#L125)):

```xml
<TextBlock x:Name="LiveHintLabel" Foreground="#38BDF8" FontSize="11"
           TextWrapping="Wrap" Margin="0,8,0,0" Visibility="Collapsed"/>
```

```csharp
private void UpdateLiveHint()
{
    if (_mode == GalleryMode.Range && _pendingLive > 0)
    {
        LiveHintLabel.Text = $"{_pendingLive} new result(s) captured — press \"Live — Last 6\" to see them.";
        LiveHintLabel.Visibility = Visibility.Visible;
    }
    else LiveHintLabel.Visibility = Visibility.Collapsed;
}
```

Rename the button at [ReportsWindow.xaml:116](../ReportsWindow.xaml#L116) to
`"🕒  Live — Last 6"` so the two buttons read as two *modes*, not two queries.
Pressing it re-queries Mongo, so results captured while browsing appear.

---

## Step 8 — Optional: cap the range query

A wide range on a busy line can return thousands of documents, each carrying a
full PNG `byte[]` **plus** an equally large `ResultImageBase64` string
([ReportStore.cs:36-44](../ReportStore.cs#L36-L44)) — roughly 2.3x the image
bytes per row over the wire. Two cheap safeguards, most valuable first:

1. **Project away `resultImageBase64`** in `QueryByDateAsync`
   (`.Project<DefectReport>(Builders<DefectReport>.Projection.Exclude("resultImageBase64"))`).
   It exists only for manual inspection in a Mongo GUI and is never displayed.
2. **Add `.Limit(500)`** (as a constant) and say in `CountLabel` when the range
   was truncated.

Neither is required for the behaviour you asked for — do them if the range view
starts feeling slow.

---

## Files touched

| File | Changes |
|---|---|
| [ReportsWindow.xaml](../ReportsWindow.xaml) | Two `ItemsPanelTemplate` resources; `ScrollViewer` around `ItemsControl`; drop `GridRows`/`GridColumns` bindings; card element names + placeholder trigger; `LiveHintLabel`; button rename. |
| [ReportsWindow.xaml.cs](../ReportsWindow.xaml.cs) | `GalleryMode` enum + `_mode`/`_pendingLive`; delete `SetGridShape`/`GridRows`/`GridColumns`; add `ApplyMode`; padded `RunQueryAsync`; **rewritten `AddLiveReport`**; placeholder guards; `UpdateLiveHint`. |
| [ReportStore.cs](../ReportStore.cs) | `IsPlaceholder` + `Placeholder()` (Step 6); optional projection/limit (Step 8). |
| [MainWindow.xaml.cs](../MainWindow.xaml.cs) | **No change** — `SaveReportAsync` still calls `_reportsWindow?.AddLiveReport(report)`; all the new behaviour lives behind that call. |

## How to verify

1. Open Reports with fewer than 6 rows in Mongo: six frames are drawn, the empty
   ones dimmed, grid is 2x3.
2. Run detections one after another: each new result appears top-left, the others
   shift right/down, the oldest leaves. **The grid stays 2x3 and the card size
   never changes.**
3. Press *Load Date Range* over a day with more than 6 results: more than six
   cards, fixed card size, vertical scrollbar appears.
4. Run a detection while a range is displayed: the range does not move, and the
   blue hint reads "1 new result(s) captured".
5. Press *Live — Last 6*: back to the six frames, with that new result first.
6. *Download All Shown* in each mode saves only real reports, no placeholders.
