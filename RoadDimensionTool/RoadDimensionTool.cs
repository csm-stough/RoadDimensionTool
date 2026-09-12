using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RoadDimensionTool
{
    internal class RoadDimensionTool : MapTool
    {
        // Layer names this tool looks for on the active map. Internal (not private) because
        // DimensionValidityRecalculator (and Module1, to subscribe to it) reference these too.
        internal const string DimensionLineLayerName = "DimensionLine";
        internal const string DimensionSetLayerName = "Dimension Set";
        private const string RightOfWayLayerName = "Right of Way";
        private const string EdgeOfPavementLayerName = "Edge of Pavement";
        private const string EasementLayerName = "Easement";

        // How far (in map units) to offset the sketch when generating the bottom-level
        // (EOP/BOC) dimension lines, purely so they draw visually separated from the
        // top-level line rather than overlapping it.
        private const double DimensionOffset = 5.0;

        // Attribute field names / values on the dimension feature classes. Internal (not
        // private) because DimensionValidityRecalculator references these too, so the field
        // names stay defined in exactly one place.
        internal const string DimIdField = "DimID";
        internal const string DisplayLengthField = "DisplayLength";
        private const string DimSideField = "DimSide";
        private const string DimSideCenterValue = "C";
        private const string PrefixField = "Prefix";
        internal const string IsValidField = "IsValid";

        // Standard line-feature-class field carrying the geometric length -- used by
        // DimensionValidityRecalculator as the fallback "effective length" for a line whose
        // DisplayLength override is null.
        internal const string ShapeLengthField = "Shape_Length";

        // Yes/No (existing coded-value domain) fields on DimensionLine recording each line's
        // role within its DimID set -- exactly one of these three is "Yes" per line. These
        // exist so that anything recalculating validity later (after the sketch/geometry is
        // long gone -- e.g. in response to a DisplayLength edit) can tell top-level, bottom-level,
        // and shoulder lines apart from the stored attributes alone. Prefix can't reliably do
        // this on its own: a mismatched-type segment (a ROW-to-EOP/BOC connector, a shoulder, or
        // even a mismatched EOP/BOC top-level line) always gets a blank Prefix, so multiple lines
        // in the same set can share a blank Prefix with no way to tell which is which.
        internal const string TopLevelDimensionField = "TopLevelDimension";
        internal const string BottomLevelDimensionField = "BottomLevelDimension";
        internal const string ShoulderDimensionField = "ShoulderDimension";
        private const string YesValue = "Yes";
        private const string NoValue = "No";

        private enum DimensionRole
        {
            TopLevel,
            BottomLevel,
            Shoulder
        }

        // Edge of Pavement carries a Subtype code distinguishing plain edge-of-pavement from
        // back-of-curb (Back of Curb is not its own layer). No coded-value domain exists yet
        // for this field in the gdb -- these codes are a convention (0 matches the layer's
        // default feature template) until a real domain is set up in ArcGIS Pro. Update these
        // if/when that domain is defined with different codes.
        private const string SubtypeField = "Subtype";
        private const int EopSubtypeEdgeOfPavement = 0;
        private const int EopSubtypeBackOfCurb = 1;

        // Single source of truth for which layers to intersect against and what boundary
        // type each one represents by default. Add an entry here to support another boundary
        // layer. (Edge of Pavement's BOC/EOP split is further refined per-feature -- see
        // GetEopBoundaryType.)
        private static readonly Dictionary<string, BoundaryType> _boundaryTypesByLayerName = new()
        {
            { RightOfWayLayerName, BoundaryType.ROW },
            { EdgeOfPavementLayerName, BoundaryType.EOP },
            { EasementLayerName, BoundaryType.UE }
        };

        internal enum BoundaryType
        {
            ROW,
            EOP,
            BOC,
            UE
        }

        internal class IntersectionPoint
        {
            public MapPoint Point { get; set; }

            public BoundaryType BoundaryType { get; set; }
        }

        public RoadDimensionTool()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Line;
            SketchOutputMode = SketchOutputMode.Map;
        }

        protected override async Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry is not Polyline sketch)
                return false;

            var dimensionId = Guid.NewGuid();

            await QueuedTask.Run(() => CreateDimensionSetForSketch(sketch, dimensionId));

            return true;
        }

        /// <summary>
        /// Builds the top-level dimension line, the bottom-level breakdown (when one applies),
        /// and any Easement shoulder lines for a completed sketch, and commits them all in a
        /// single edit operation. See <see cref="SelectTopLevelPoints"/> and
        /// <see cref="FindShoulderPairs"/> for the full set of rules this implements.
        /// </summary>
        private void CreateDimensionSetForSketch(Polyline sketch, Guid dimensionId)
        {
            var lineLayer = GetFeatureLayer(DimensionLineLayerName);
            var setLayer = GetFeatureLayer(DimensionSetLayerName);

            var originalIntersections = FindIntersections(sketch);

            var topLevelPoints = SelectTopLevelPoints(originalIntersections, out bool hasBottomLevelBreakdown);

            if (topLevelPoints == null)
                return;

            var orderedBottomLevelPoints = new List<IntersectionPoint>();
            string isValid;

            if (hasBottomLevelBreakdown)
            {
                var offsetSketch = OffsetLine(sketch, DimensionOffset);

                // topLevelPoints are all the same type here (enforced by SelectTopLevelPoints'
                // per-tier family match), so this tells us whether ROW or Easement is acting as
                // the top-level anchor for this sketch.
                var topLevelType = topLevelPoints[0].BoundaryType;

                // The bottom-level breakdown needs the *outer* connector lines (top-level type
                // to the nearest EOP/BOC, on each side) plus the EOP/BOC-to-EOP/BOC line(s) in
                // between -- so it needs the winning top-level type's own crossings on the
                // offset sketch, alongside EOP/BOC. Only the *other* of {ROW, UE} (whichever
                // isn't acting as the top-level anchor -- i.e. Easement when ROW wins) is
                // excluded, so a shoulder-side Easement crossing on the offset sketch can't leak
                // into this sum.
                var offsetIntersections = FindIntersections(offsetSketch)
                    .Where(i => i.BoundaryType == BoundaryType.EOP
                             || i.BoundaryType == BoundaryType.BOC
                             || i.BoundaryType == topLevelType)
                    .ToList();

                orderedBottomLevelPoints = OrderIntersections(offsetSketch, offsetIntersections);

                isValid = ComputeValidity(topLevelPoints, orderedBottomLevelPoints);
            }
            else
            {
                // EOP/BOC is the top-level (and only) line here -- nothing to sum it against,
                // so it can never be "wrong".
                isValid = "Yes";
            }

            var shoulderPairs = FindShoulderPairs(sketch, originalIntersections, topLevelPoints);

            var editOperation = new EditOperation
            {
                Name = "Create Road Dimension Set"
            };

            var midpoint = MapPointBuilderEx.CreateMapPoint(
                (topLevelPoints[0].Point.X + topLevelPoints[1].Point.X) / 2,
                (topLevelPoints[0].Point.Y + topLevelPoints[1].Point.Y) / 2,
                topLevelPoints[0].Point.SpatialReference);

            CreateDimensionSet(editOperation, setLayer, midpoint, dimensionId, isValid);

            CreateDimensionLine(
                editOperation,
                lineLayer,
                topLevelPoints[0].Point,
                topLevelPoints[1].Point,
                dimensionId,
                isValid,
                DimensionRole.TopLevel,
                GetDimensionPrefix(topLevelPoints[0].BoundaryType, topLevelPoints[1].BoundaryType));

            if (hasBottomLevelBreakdown)
                CreateOffsetDimensionLines(editOperation, lineLayer, orderedBottomLevelPoints, dimensionId, isValid);

            foreach (var (anchorPoint, easementPoint) in shoulderPairs)
            {
                CreateDimensionLine(
                    editOperation,
                    lineLayer,
                    anchorPoint.Point,
                    easementPoint.Point,
                    dimensionId,
                    isValid,
                    DimensionRole.Shoulder,
                    GetDimensionPrefix(anchorPoint.BoundaryType, easementPoint.BoundaryType));
            }

            editOperation.Execute();
        }

        /// <summary>
        /// Picks the top-level (outermost) pair of points from the intersections found on the
        /// original (unoffset) sketch, in priority order:
        ///   1. ROW -- wins whenever any ROW crossing is present. Must be exactly 2 or the
        ///      sketch is treated as malformed/ambiguous and this bails (returns null) rather
        ///      than falling through to a lower-priority tier.
        ///   2. Easement (UE) -- stands in for ROW only when it forms a genuine pair (exactly
        ///      2) with no ROW present. A single (one-sided) Easement point does NOT claim this
        ///      tier on its own -- with no ROW, a lone Easement crossing falls through to be
        ///      treated as a shoulder against EOP/BOC instead (see FindShoulderPairs), rather
        ///      than forcing a bail here.
        ///   3. EOP/BOC -- used only when neither of the above applies. Must be exactly 2 or
        ///      this bails, same as ROW.
        /// Returns null (caller bails, nothing created) if none of the above produced a clean
        /// pair.
        /// </summary>
        private List<IntersectionPoint> SelectTopLevelPoints(
            List<IntersectionPoint> originalSketchIntersections,
            out bool hasBottomLevelBreakdown)
        {
            var rowPoints = originalSketchIntersections
                .Where(i => i.BoundaryType == BoundaryType.ROW)
                .ToList();

            if (rowPoints.Count > 0)
            {
                hasBottomLevelBreakdown = true;
                return rowPoints.Count == 2 ? rowPoints : null;
            }

            var easementPoints = originalSketchIntersections
                .Where(i => i.BoundaryType == BoundaryType.UE)
                .ToList();

            if (easementPoints.Count == 2)
            {
                hasBottomLevelBreakdown = true;
                return easementPoints;
            }

            var eopBocPoints = originalSketchIntersections
                .Where(i => i.BoundaryType == BoundaryType.EOP || i.BoundaryType == BoundaryType.BOC)
                .ToList();

            if (eopBocPoints.Count > 0)
            {
                hasBottomLevelBreakdown = false;
                return eopBocPoints.Count == 2 ? eopBocPoints : null;
            }

            hasBottomLevelBreakdown = false;
            return null;
        }

        /// <summary>
        /// Finds the independent shoulder line(s) between Easement and whichever type is
        /// acting as the top-level anchor (ROW, or EOP/BOC when there's no ROW) -- one per side
        /// where an Easement crossing exists -- excluded entirely from the top/bottom-level
        /// validity sum. Easement never gets a shoulder drawn against itself when it's the one
        /// standing in as the top-level anchor. Easement always sits outside ROW/EOP/BOC
        /// geometrically, so ordering the top-level points and Easement points together along
        /// the original sketch (the same trick <see cref="OrderIntersections"/> already uses
        /// for the offset sketch) reliably puts each Easement point immediately next to its
        /// anchor neighbor on that side, one-sided Easement included.
        /// </summary>
        private List<(IntersectionPoint Anchor, IntersectionPoint Easement)> FindShoulderPairs(
            Polyline sketch,
            List<IntersectionPoint> originalSketchIntersections,
            List<IntersectionPoint> topLevelPoints)
        {
            var pairs = new List<(IntersectionPoint, IntersectionPoint)>();

            bool topLevelIsEasement = topLevelPoints[0].BoundaryType == BoundaryType.UE;
            if (topLevelIsEasement)
                return pairs;

            var easementPoints = originalSketchIntersections
                .Where(i => i.BoundaryType == BoundaryType.UE)
                .ToList();

            if (easementPoints.Count == 0)
                return pairs;

            var ordered = OrderIntersections(sketch, topLevelPoints.Concat(easementPoints).ToList());

            if (ordered.First().BoundaryType == BoundaryType.UE)
                pairs.Add((ordered[1], ordered[0]));

            if (ordered.Last().BoundaryType == BoundaryType.UE)
                pairs.Add((ordered[ordered.Count - 2], ordered[ordered.Count - 1]));

            return pairs;
        }

        private void CreateOffsetDimensionLines(
            EditOperation editOperation,
            FeatureLayer lineLayer,
            List<IntersectionPoint> orderedBottomLevelPoints,
            Guid dimensionId,
            string isValid)
        {
            for (int i = 0; i < orderedBottomLevelPoints.Count - 1; i++)
            {
                CreateDimensionLine(
                    editOperation,
                    lineLayer,
                    orderedBottomLevelPoints[i].Point,
                    orderedBottomLevelPoints[i + 1].Point,
                    dimensionId,
                    isValid,
                    DimensionRole.BottomLevel,
                    GetDimensionPrefix(orderedBottomLevelPoints[i].BoundaryType, orderedBottomLevelPoints[i + 1].BoundaryType));
            }
        }

        /// <summary>
        /// Flags whether the top-level span length matches the summed length of the bottom-level
        /// segments -- the outermost dimension line is supposed to equal the sum of the inner ones,
        /// and when it doesn't, the dimension set needs fixing. Truncating both sides to whole map
        /// units before comparing is intentional (by design), not a rounding shortcut to revisit.
        /// Only called when a bottom-level breakdown actually exists -- see CreateDimensionSetForSketch.
        /// </summary>
        private string ComputeValidity(
            List<IntersectionPoint> topLevelPoints,
            List<IntersectionPoint> orderedBottomLevelPoints)
        {
            int topLevelSpanLength = (int)GeometryEngine.Instance.Distance(topLevelPoints[0].Point, topLevelPoints[1].Point);

            int bottomLevelSegmentLength = 0;
            for (int i = 0; i < orderedBottomLevelPoints.Count - 1; i++)
            {
                bottomLevelSegmentLength += (int)GeometryEngine.Instance.Distance(
                    orderedBottomLevelPoints[i].Point,
                    orderedBottomLevelPoints[i + 1].Point);
            }

            return (topLevelSpanLength == bottomLevelSegmentLength) ? "Yes" : "No";
        }

        private void CreateDimensionSet(
            EditOperation editOperation,
            FeatureLayer layer,
            MapPoint point,
            Guid dimensionId,
            string isValid)
        {
            editOperation.Create(
                layer,
                point,
                new Dictionary<string, object>
                {
                    { DimIdField, dimensionId }
                });
        }

        private void CreateDimensionLine(
            EditOperation editOperation,
            FeatureLayer layer,
            MapPoint startPoint,
            MapPoint endPoint,
            Guid dimensionId,
            string isValid,
            DimensionRole role,
            string prefix = "")
        {
            var line = PolylineBuilderEx.CreatePolyline(
                new[] { startPoint, endPoint });

            editOperation.Create(
                layer,
                line,
                new Dictionary<string, object>
                {
                    { DisplayLengthField, null },
                    { DimIdField, dimensionId },
                    { DimSideField, DimSideCenterValue },
                    { PrefixField, prefix },
                    { IsValidField, isValid },
                    { TopLevelDimensionField, role == DimensionRole.TopLevel ? YesValue : NoValue },
                    { BottomLevelDimensionField, role == DimensionRole.BottomLevel ? YesValue : NoValue },
                    { ShoulderDimensionField, role == DimensionRole.Shoulder ? YesValue : NoValue }
                });
        }

        private Polyline OffsetLine(Polyline line, double distance)
        {
            var geometry = GeometryEngine.Instance.Offset(
                line,
                distance,
                OffsetType.Miter,
                1.0);

            return geometry as Polyline;
        }

        // Static + internal (not instance/private) so Module1 can look up the DimensionLine
        // layer for a specific map (the one it's just been notified is becoming active),
        // rather than only ever the currently-active one -- and so DimensionValidityRecalculator
        // could reuse the same lookup if it ever needs to. Defaults to the active map's layers
        // for every existing call site within this class, which all omit the map argument.
        private static List<FeatureLayer> GetAllFeatureLayers(Map map = null)
        {
            map ??= MapView.Active?.Map;

            return map?.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .ToList() ?? new List<FeatureLayer>();
        }

        internal static FeatureLayer GetFeatureLayer(string name, Map map = null)
        {
            return GetAllFeatureLayers(map)
                .FirstOrDefault(l => l.Name == name);
        }

        private List<FeatureLayer> GetIntersectionLayers()
        {
            return GetAllFeatureLayers()
                .Where(layer => _boundaryTypesByLayerName.ContainsKey(layer.Name))
                .ToList();
        }

        private List<IntersectionPoint> FindIntersections(Polyline sketch)
        {
            var intersections = new List<IntersectionPoint>();

            foreach (var layer in GetIntersectionLayers())
            {
                var defaultBoundaryType = _boundaryTypesByLayerName[layer.Name];

                using var cursor = layer.Search();

                while (cursor.MoveNext())
                {
                    using var feature = (ArcGIS.Core.Data.Feature)cursor.Current;

                    var boundaryType = layer.Name == EdgeOfPavementLayerName
                        ? GetEopBoundaryType(feature)
                        : defaultBoundaryType;

                    var intersection =
                        GeometryEngine.Instance.Intersection(
                            sketch,
                            feature.GetShape(),
                            GeometryDimensionType.EsriGeometry0Dimension);

                    if (intersection is Multipoint multipoint)
                    {
                        foreach (var point in multipoint.Points)
                        {
                            intersections.Add(new IntersectionPoint
                            {
                                Point = point,
                                BoundaryType = boundaryType
                            });
                        }
                    }
                }
            }

            return intersections;
        }

        /// <summary>
        /// Distinguishes Back of Curb from plain Edge of Pavement using the feature's Subtype
        /// value, per <see cref="EopSubtypeBackOfCurb"/> / <see cref="EopSubtypeEdgeOfPavement"/>.
        /// Any code other than the Back of Curb one (including a missing value) is treated as
        /// plain EOP.
        /// </summary>
        private BoundaryType GetEopBoundaryType(ArcGIS.Core.Data.Feature feature)
        {
            var subtypeValue = feature[SubtypeField];
            var subtypeCode = subtypeValue == null ? EopSubtypeEdgeOfPavement : Convert.ToInt32(subtypeValue);

            return subtypeCode == EopSubtypeBackOfCurb ? BoundaryType.BOC : BoundaryType.EOP;
        }

        private List<IntersectionPoint> OrderIntersections(
            Polyline sketch,
            List<IntersectionPoint> intersections)
        {
            return intersections
                .OrderBy(i =>
                {
                    GeometryEngine.Instance.QueryPointAndDistance(
                        sketch,
                        SegmentExtensionType.NoExtension,
                        i.Point,
                        AsRatioOrLength.AsLength,
                        out double distanceAlongCurve,
                        out _,
                        out _);

                    return distanceAlongCurve;
                })
                .ToList();
        }

        private string GetDimensionPrefix(
            BoundaryType startType,
            BoundaryType endType)
        {
            if (startType != endType)
                return string.Empty;

            return startType switch
            {
                BoundaryType.ROW => "ROW",
                BoundaryType.EOP => "EOP",
                BoundaryType.BOC => "BOC",
                BoundaryType.UE => "UE",
                _ => string.Empty
            };
        }
    }
}
