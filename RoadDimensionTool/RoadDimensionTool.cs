using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
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

        private readonly List<string> intersection_layers = new List<string>
        {
            "Right of Way", "Edge of Pavement", "Back of Curb", "Easement"
        };

        public RoadDimensionTool()
        {
            IsSketchTool = true;
            SketchType = SketchGeometryType.Line;
            SketchOutputMode = SketchOutputMode.Map;
        }


        protected override async Task<bool> OnSketchCompleteAsync(Geometry geometry)
        {
            if (geometry is not Polyline line)
                return false;

            var dimensionId = Guid.NewGuid();

            //await CreateDimensionLines(line, dimensionId);
            await QueuedTask.Run(() =>
            {
                var points = FindIntersections(line);

                var orderedIntersections = OrderIntersections(line, points);

                foreach (var point in orderedIntersections)
                {
                    System.Diagnostics.Debug.WriteLine(point);
                }
            });

            return true;
        }


        private async Task CreateDimensionLines(Polyline line, Guid dimensionId)
        {
            await QueuedTask.Run(() =>
            {
                var layer = MapView.Active.Map
                    .GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name == "DimensionLine");

                if (layer == null)
                    return;


                var editOperation = new EditOperation
                {
                    Name = "Create Road Dimension Line"
                };

                editOperation.Create(
                    layer,
                    line,
                    new Dictionary<string, object>
                    {
                        { "DisplayLength", null },
                        { "DimID", dimensionId},
                        { "DimSide", "C"}
                    });

                // Offset measurement line
                var offsetLine = OffsetLine(line, 20);

                editOperation.Execute();
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

        private List<MapPoint> FindIntersections(Polyline sketch)
        {
            var intersections = new List<MapPoint>();

            var layers = GetIntersectionLayers();

            foreach (var layer in GetIntersectionLayers())
            {
                using var cursor = layer.Search();

                while (cursor.MoveNext())
                {
                    var feature = cursor.Current as Feature;

                    var intersection =
                        GeometryEngine.Instance.Intersection(
                            sketch,
                            feature.GetShape(),
                            GeometryDimensionType.EsriGeometry0Dimension);

                    if (intersection is Multipoint multipoint)
                    {
                        intersections.AddRange(
                            multipoint.Points);
                    }
                }
            }

            return intersections;
        }

        private List<MapPoint> OrderIntersections(
            Polyline sketch,
            List<MapPoint> points)
        {
            return points
                .OrderBy(point =>
                {
                    GeometryEngine.Instance.QueryPointAndDistance(
                        sketch,
                        SegmentExtensionType.NoExtension,
                        point,
                        AsRatioOrLength.AsLength,
                        out double distanceAlongCurve,
                        out _,
                        out _);

                    return distanceAlongCurve;
                })
                .ToList();
        }
    }
}