# RoadDimensionTool

An ArcGIS Pro add-in that automates the creation of road right-of-way dimension lines from a single sketched cross-section, and keeps their validity flags in sync as attributes are edited afterward.

## What it does

Surveying/engineering road cross-sections normally means manually digitizing a set of dimension lines across a right-of-way — one long outer span, plus the shorter inner segments it's supposed to add up to, plus any easement callouts — and manually checking that the numbers reconcile. RoadDimensionTool turns that into a single sketch.

Draw one line across a road cross-section (through Right of Way, Edge of Pavement/Back of Curb, and/or Easement boundaries) with the **Road Dimensioner** map tool, and the add-in:

- Finds every boundary layer the sketch crosses and classifies each crossing (ROW, EOP, BOC, or Easement).
- Picks the correct **top-level** (outermost) span for the configuration it finds, and — when applicable — breaks it down into the **bottom-level** segments that should sum to that span.
- Draws independent **shoulder** dimension lines for any Easement crossings that aren't already part of the top-level span.
- Computes whether the top-level length equals the sum of the bottom-level lengths and flags the whole set `IsValid = Yes/No` accordingly (driving red/black symbology in the map).
- Keeps that validity flag correct afterward: editing a dimension line's `DisplayLength` override in the attribute table live-recalculates `IsValid` across its whole set, with no need to re-run the sketch tool.

## Dimension line rules

Given the crossings found on a sketch, the top-level span is chosen by priority:

1. **ROW** — if any Right of Way crossing exists, it's always the top-level span (`"ROW"` prefix), regardless of Easement. Requires exactly 2 crossings, or the sketch is treated as malformed and nothing is drawn.
2. **Easement** — stands in for ROW only when there's no ROW present *and* Easement forms a genuine pair (exactly 2 crossings). A single one-sided Easement crossing does not claim this tier — it falls through to be treated as a shoulder instead.
3. **Edge of Pavement / Back of Curb** — used only when neither of the above applies. This is the common simple-roadway case: there's no bottom-level breakdown at all, since there's nothing left to subdivide against.

When the top-level span is ROW or Easement, a **bottom-level breakdown** is generated from an offset copy of the sketch: the connector segment(s) from the top-level type to the nearest EOP/BOC on each side, plus any EOP/BOC-to-EOP/BOC segment in between. Their lengths are summed and compared against the top-level span's length (both truncated to whole map units) to decide `IsValid`.

Independently of that, a **shoulder line** is drawn from the top-level anchor to any Easement crossing that isn't already the top-level span itself — one per side, so a sketch can have zero, one, or two shoulders. Shoulders share their set's `IsValid` flag but are never part of the length-sum calculation.

Back of Curb isn't a separate layer — it's an Edge of Pavement feature with its `Subtype` set to the Back of Curb code (see [Data schema](#data-schema) below).

## Live validity recalculation

`IsValid` isn't only computed at creation time. `DimensionValidityRecalculator` subscribes to edit events on the `DimensionLine` table and, whenever a line's `DisplayLength` field changes:

1. Looks up every other `DimensionLine` row sharing that line's `DimID`.
2. For each row in the set, uses `DisplayLength` if it's set, otherwise falls back to the line's own geometric `Shape_Length`.
3. Sums the effective lengths of the rows flagged `BottomLevelDimension = Yes` and compares that to the single row flagged `TopLevelDimension = Yes`.
4. Writes the resulting `Yes`/`No` back to every row in the set (top-level, bottom-level, and shoulder rows alike).

This runs entirely in C# against the ArcGIS Pro SDK's row-change events (`RowChangedEvent` + direct `Row.Store()` calls) rather than through Esri attribute rules — an earlier Arcade-based approach using a `Trigger` field hit ArcGIS's attribute-rule cascade limit, since two feature classes each writing into the other in a loop is an infinite cascade. The C# approach only ever reacts to a genuine `DisplayLength` change and only ever writes `IsValid`, so it can't cascade into itself.

One trade-off worth knowing: because these writes go through `Row.Store()` directly rather than the edit operation that triggered them, they aren't part of that operation's undo step. Undoing a `DisplayLength` edit won't automatically revert the `IsValid` values it caused (editing `DisplayLength` again will recompute them correctly).

## Data schema

The add-in expects the active map to contain these layers, by name:

| Layer name | Geometry | Notes |
|---|---|---|
| `Right of Way` | Line | Plain boundary, no special fields required. |
| `Edge of Pavement` | Line | Carries a `Subtype` field: `0` = Edge of Pavement (default), `1` = Back of Curb. |
| `Easement` | Line | Plain boundary, no special fields required. |
| `DimensionLine` | Line | The generated dimension lines. See fields below. |
| `Dimension Set` | Point | One point per generated set, placed at the top-level span's midpoint. |

`DimensionLine` fields:

| Field | Type | Purpose |
|---|---|---|
| `DimID` | Text | Groups every line belonging to one sketch/set (matches the `Dimension Set` point's `DimID`). |
| `DisplayLength` | Integer | User-editable override length. Left `null` at creation time by design; falls back to `Shape_Length` when null. |
| `DimSide` | Text | Always `"C"` at creation (centered label side). |
| `Prefix` | Text | Boundary-type label (`ROW`/`EOP`/`BOC`/`UE`), blank when a segment's two endpoints don't match. |
| `IsValid` | Text (`Yes`/`No`) | Drives the red/black validity symbology; kept in sync by `DimensionValidityRecalculator`. |
| `TopLevelDimension` | Text (`Yes`/`No` domain) | Exactly one `Yes` per set — the outer span. |
| `BottomLevelDimension` | Text (`Yes`/`No` domain) | The inner segments that should sum to the top-level span. |
| `ShoulderDimension` | Text (`Yes`/`No` domain) | Easement callouts, excluded from the length-sum check. |

`Dimension Set` fields:

| Field | Type | Purpose |
|---|---|---|
| `DimID` | Text | Matches the `DimID` shared by all of this set's `DimensionLine` rows. |

A dimension line's spatial reference should be a true projected coordinate system in the units your surveys are actually recorded in (e.g. a state plane feet-based CRS) — geometry math (`GeometryEngine.Instance.Distance`/`Offset`) assumes true ground distance, which Web Mercator (EPSG:3857) does not provide.

## Requirements

- ArcGIS Pro (built against ArcGIS Pro SDK 3.6.x)
- .NET 8 SDK, Windows (`net8.0-windows`)
- ArcGIS Pro SDK for .NET (Visual Studio extension)

## Building

1. Open `RoadDimensionTool.slnx` in Visual Studio with the ArcGIS Pro SDK extension installed.
2. Build the solution. The add-in packages as a `.esriAddinX`; debugging (F5) launches ArcGIS Pro with it installed.
3. Once built, the add-in loads automatically on project open (`Config.daml`'s module `autoLoad` is set to `true`) — this is required so the live `IsValid` recalculation is active immediately, not only after the Road Dimensioner tool has been used at least once.

## Using it

1. Open a map containing the layers listed above.
2. Activate the **Road Dimensioner** tool (Add-In tab).
3. Sketch a single line across the road cross-section, crossing whichever of Right of Way / Edge of Pavement (or Back of Curb) / Easement boundaries apply, and double-click (or press Enter/F2, per your sketch finish setting) to complete it.
4. The dimension line(s) and a `Dimension Set` point are created in one edit operation. Check the `IsValid` attribute (or the map symbology) to confirm the set reconciles.
5. To correct a set later, edit a line's `DisplayLength` in the attribute table or Attributes pane — `IsValid` recalculates across the whole set automatically.

## Project layout

- `RoadDimensionTool.cs` — the `MapTool` implementation: sketch handling, boundary classification, the top-level/bottom-level/shoulder selection rules, and dimension feature creation.
- `DimensionValidityRecalculator.cs` — live `IsValid` recalculation in response to `DisplayLength` edits.
- `Module1.cs` — add-in module lifecycle; wires up the `DimensionValidityRecalculator` subscription against the active map and keeps it re-pointed as the active map changes.
- `Config.daml` — add-in manifest (module, tool button, metadata).

## Known limitations

- Silent failure paths: a malformed sketch (wrong crossing counts), a missing layer, or a null geometry currently just aborts quietly rather than telling the user what went wrong.
- `FindIntersections` scans every feature in each boundary layer with no spatial filter — worth revisiting for large layers.
- The `Dimension Set` feature class doesn't currently store its own `IsValid` (validity lives entirely on the `DimensionLine` rows).
- Undoing a `DisplayLength` edit doesn't automatically revert the `IsValid` values it triggered (see [Live validity recalculation](#live-validity-recalculation)).
