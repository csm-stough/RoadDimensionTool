using ArcGIS.Core.Data;
using ArcGIS.Desktop.Editing.Events;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RoadDimensionTool
{
    /// <summary>
    /// Keeps IsValid in sync across a DimensionLine set whenever a line's DisplayLength is
    /// edited directly (attribute table, Attributes pane, etc.) -- not just at creation time in
    /// RoadDimensionTool.CreateDimensionSetForSketch. Subscribed/unsubscribed by Module1 against
    /// the active map's DimensionLine table (see Module1.OnActiveMapViewChanged).
    ///
    /// This intentionally does NOT use Esri attribute rules. An earlier attempt at this via
    /// attribute rules (a Trigger field on DimensionSet bumping a rule that pushed IsValid back
    /// down to every sibling line) hit ArcGIS's attribute-rule cascade limit, because two
    /// feature classes were each immediately writing into the other. This sidesteps that
    /// entirely: per Esri's own guidance for RowEvent callbacks ("if you need to edit additional
    /// tables/rows within the RowEvent you must use the ArcGIS.Core.Data API directly -- do not
    /// use a new EditOperation"), every cross-row write here goes through Row.Store() directly.
    ///
    /// Recursion is a structural non-issue: this only reacts when DisplayLength itself changed
    /// (via Row.HasValueChanged), and every write this method makes only ever touches IsValid --
    /// so the Store() calls this makes on itself and its siblings never re-trigger the
    /// DisplayLength check that got it here in the first place. The Guid re-entrancy guard below
    /// is belt-and-suspenders on top of that, matching Esri's own sample pattern for RowEvent
    /// handlers that Store() the row they were called for.
    ///
    /// Trade-off worth knowing: because these writes go through Row.Store() directly rather than
    /// the triggering EditOperation, they're not part of that operation's undo step -- undoing a
    /// DisplayLength edit won't automatically revert the IsValid values it caused (editing
    /// DisplayLength again will recompute them correctly, though).
    /// </summary>
    internal static class DimensionValidityRecalculator
    {
        private static Guid _reentrantGuid = Guid.Empty;

        internal static void OnDimensionLineRowChanged(RowChangedEventArgs args)
        {
            if (args.Guid == _reentrantGuid)
                return;

            var row = args.Row;

            int displayLengthIndex = row.FindField(RoadDimensionTool.DisplayLengthField);
            if (displayLengthIndex < 0 || !row.HasValueChanged(displayLengthIndex))
                return;

            Recalculate(args, row);
        }

        private static void Recalculate(RowChangedEventArgs args, Row changedRow)
        {
            var dimId = Convert.ToString(changedRow[RoadDimensionTool.DimIdField]);
            if (string.IsNullOrEmpty(dimId))
                return;

            var table = changedRow.GetTable();
            var changedOid = changedRow.GetObjectID();
            var escapedDimId = dimId.Replace("'", "''");

            var siblingRows = new List<Row>();
            try
            {
                // recycling: false is required here -- Table.Search defaults to a recycling
                // cursor, which reuses the same underlying Row instance for every record
                // (repositioning it on each MoveNext for performance). That's fine for read-only
                // iteration, but these rows are stashed in siblingRows and Store()'d later, after
                // the cursor above has moved on/closed -- against a recycled row that throws
                // "Cannot call Store on a recycled row while editing." Non-recycling rows are
                // independent and stay valid for that later Store() call.
                using (var cursor = table.Search(new QueryFilter
                {
                    WhereClause = $"{RoadDimensionTool.DimIdField} = '{escapedDimId}' AND OBJECTID <> {changedOid}"
                }, false))
                {
                    while (cursor.MoveNext())
                        siblingRows.Add(cursor.Current);
                }

                var allRows = siblingRows.Append(changedRow).ToList();

                var bottomLevelLengths = new List<int>();
                int topLevelLength = 0;
                int topLevelCount = 0;

                foreach (var row in allRows)
                {
                    int effectiveLength = GetEffectiveLength(row);

                    if (IsYes(row, RoadDimensionTool.TopLevelDimensionField))
                    {
                        topLevelLength = effectiveLength;
                        topLevelCount++;
                    }

                    if (IsYes(row, RoadDimensionTool.BottomLevelDimensionField))
                        bottomLevelLengths.Add(effectiveLength);
                }

                string isValid;
                if (bottomLevelLengths.Count == 0)
                {
                    // No bottom-level breakdown for this set (the EOP/BOC-only tier) -- nothing
                    // to compare the top-level line against, so it can never be "wrong".
                    isValid = "Yes";
                }
                else if (topLevelCount != 1)
                {
                    // Unexpected/malformed data (no top-level row found, or more than one) --
                    // don't guess at a new IsValid.
                    return;
                }
                else
                {
                    isValid = topLevelLength == bottomLevelLengths.Sum() ? "Yes" : "No";
                }

                _reentrantGuid = args.Guid;
                try
                {
                    foreach (var row in allRows)
                    {
                        if (string.Equals(Convert.ToString(row[RoadDimensionTool.IsValidField]), isValid, StringComparison.OrdinalIgnoreCase))
                            continue; // Already correct -- skip so this doesn't fire a no-op Store().

                        row[RoadDimensionTool.IsValidField] = isValid;
                        row.Store();
                    }
                }
                finally
                {
                    _reentrantGuid = Guid.Empty;
                }
            }
            finally
            {
                foreach (var row in siblingRows)
                    row.Dispose();
            }
        }

        /// <summary>
        /// DisplayLength is the user-editable override; when it's null, fall back to this
        /// line's own truncated geometric length (Shape_Length) -- the same truncate-to-whole-
        /// map-units rule the original creation-time validity check used.
        /// </summary>
        private static int GetEffectiveLength(Row row)
        {
            var displayLength = row[RoadDimensionTool.DisplayLengthField];
            if (displayLength != null)
                return Convert.ToInt32(displayLength);

            return (int)Convert.ToDouble(row[RoadDimensionTool.ShapeLengthField]);
        }

        private static bool IsYes(Row row, string fieldName)
        {
            return string.Equals(Convert.ToString(row[fieldName]), "Yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
