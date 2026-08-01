using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RoadDimensionTool
{
    internal class RoadDimensionTool : MapTool
    {
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

            await CreateDimensionLine(line);

            return true;
        }


        private async Task CreateDimensionLine(Polyline line)
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
                        { "DisplayLength", null }
                    });


                editOperation.Execute();
            });
        }
    }
}