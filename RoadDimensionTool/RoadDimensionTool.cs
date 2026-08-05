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
        private readonly string DimensionFeatureLayer = "DimensionLine";
        private const double DimensionOffset = 5.0;
        private readonly List<string> intersection_layers = new List<string>
        {
            "Right of Way", "Edge of Pavement", "Back of Curb", "Easement"
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
            //await CreateDimensionLines(line, dimensionId);

            await QueuedTask.Run(() =>
            {
                var layer = MapView.Active.Map
                    .GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == DimensionFeatureLayer);

                var offsetSketch = OffsetLine(
                    sketch,
                    DimensionOffset);

                var ROWpoints = FindIntersections(sketch);
                var offsetPoints = FindIntersections(offsetSketch); 

                var orderedROWIntersections = OrderIntersections(sketch, ROWpoints);
                var orderedOffsetIntersections = OrderIntersections(offsetSketch, offsetPoints);

                var rowPoints = ROWpoints
                    .Where(i => i.BoundaryType == BoundaryType.ROW)
                    .ToList();

                if (rowPoints.Count != 2)
                    return;

                //Checking dimension sums for symbology changes
                double ROW_sum = GeometryEngine.Instance.Distance(rowPoints[0].Point, rowPoints[1].Point);
                double SHLDR_EOP_sum = 0;
                for (int i = 0; i < orderedOffsetIntersections.Count - 1; i++)
                {
                    double dist = GeometryEngine.Instance.Distance(orderedOffsetIntersections[i].Point, orderedOffsetIntersections[i + 1].Point);
                    SHLDR_EOP_sum += dist;
                }

                var checkMeasurement = ((int)ROW_sum == (int)SHLDR_EOP_sum) ? "No" : "Yes";

                var editOperation = new EditOperation
                {
                    Name = "Create Road Dimensions"
                };

                CreateDimensionLine(
                    editOperation,
                    layer,
                    rowPoints[0].Point,
                    rowPoints[1].Point,
                    dimensionId,
                    GetDimensionPrefix(rowPoints[0].BoundaryType, rowPoints[1].BoundaryType),
                    checkMeasurement);

                for (int i = 0; i < orderedOffsetIntersections.Count - 1; i++)
                {
                    CreateDimensionLine(
                        editOperation,
                        layer,
                        orderedOffsetIntersections[i].Point,
                        orderedOffsetIntersections[i + 1].Point,
                        dimensionId,
                        GetDimensionPrefix(orderedOffsetIntersections[i].BoundaryType, orderedOffsetIntersections[i + 1].BoundaryType),
                        checkMeasurement);
                }

                editOperation.Execute();
            });

            return true;
        }
        private void CreateDimensionLine(
            EditOperation editOperation,
            FeatureLayer layer,
            MapPoint startPoint,
            MapPoint endPoint,
            Guid dimensionId,
            String prefix = "",
            string checkMeasurement = "No")
        {
            var line = PolylineBuilderEx.CreatePolyline(
                new[] { startPoint, endPoint });

            editOperation.Create(
                layer,
                line,
                new Dictionary<string, object>
                {
                    { "DisplayLength", null },
                    { "DimID", dimensionId},
                    { "DimSide", "C"},
                    { "Prefix", prefix},
                    { "CheckMeasurement", checkMeasurement}
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

        private List<FeatureLayer> GetIntersectionLayers()
        {
            return MapView.Active.Map
                .GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .Where(layer => intersection_layers.Contains(layer.Name))
                .ToList();
        }

        private List<IntersectionPoint> FindIntersections(Polyline sketch)
        {
            var intersections = new List<IntersectionPoint>();

            var layers = GetIntersectionLayers();

            foreach (var layer in GetIntersectionLayers())
            {

                //TODO: Replace with some way to check --> if EOP, check the subtype instead
                var boundaryType = layer.Name == "Right of Way" ? BoundaryType.ROW : BoundaryType.EOP;

                using var cursor = layer.Search();

                while (cursor.MoveNext())
                {
                    using var feature = (ArcGIS.Core.Data.Feature)cursor.Current;

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