using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Events;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Editing.Events;
using ArcGIS.Desktop.Extensions;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.KnowledgeGraph;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RoadDimensionTool
{
    internal class Module1 : Module
    {
        private static Module1 _this = null;

        // Tracks the live DimensionValidityRecalculator subscription so it can be swapped to
        // whichever map is active -- RowChangedEvent.Subscribe(handler, table) is scoped to one
        // specific Table instance, so this has to be re-pointed every time the active map (and
        // therefore the DimensionLine layer/table instance) changes.
        private SubscriptionToken _dimensionLineRowChangedToken;
        private SubscriptionToken _activeMapViewChangedToken;

        /// <summary>
        /// Retrieve the singleton instance to this module here
        /// </summary>
        public static Module1 Current => _this ??= (Module1)FrameworkApplication.FindModule("RoadDimensionTool_Module");

        #region Overrides
        /// <summary>
        /// Called by Framework when the module is first loaded. Config.daml sets this module's
        /// autoLoad to true specifically so this subscription is live as soon as a project opens
        /// -- not only after the user has activated the Road Dimensioner tool at least once --
        /// since a DisplayLength edit via the attribute table should recalculate IsValid
        /// regardless of whether the sketch tool has been used yet this session.
        /// </summary>
        protected override bool Initialize()
        {
            _activeMapViewChangedToken = ActiveMapViewChangedEvent.Subscribe(OnActiveMapViewChanged);

            if (MapView.Active?.Map != null)
                _ = SubscribeDimensionLineRowChanged(MapView.Active.Map);

            return base.Initialize();
        }

        /// <summary>
        /// Called by Framework when ArcGIS Pro is closing
        /// </summary>
        /// <returns>False to prevent Pro from closing, otherwise True</returns>
        protected override bool CanUnload()
        {
            //TODO - add your business logic
            //return false to ~cancel~ Application close
            return true;
        }

        protected override void Uninitialize()
        {
            ActiveMapViewChangedEvent.Unsubscribe(_activeMapViewChangedToken);
            UnsubscribeDimensionLineRowChanged();

            base.Uninitialize();
        }

        #endregion Overrides

        private void OnActiveMapViewChanged(ActiveMapViewChangedEventArgs args)
        {
            UnsubscribeDimensionLineRowChanged();

            if (args.IncomingView?.Map == null)
                return;

            _ = SubscribeDimensionLineRowChanged(args.IncomingView.Map);
        }

        private async Task SubscribeDimensionLineRowChanged(Map map)
        {
            await QueuedTask.Run(() =>
            {
                var lineLayer = RoadDimensionTool.GetFeatureLayer(RoadDimensionTool.DimensionLineLayerName, map);
                if (lineLayer == null)
                    return;

                _dimensionLineRowChangedToken = RowChangedEvent.Subscribe(
                    DimensionValidityRecalculator.OnDimensionLineRowChanged,
                    lineLayer.GetTable());
            });
        }

        private void UnsubscribeDimensionLineRowChanged()
        {
            if (_dimensionLineRowChangedToken == null)
                return;

            RowChangedEvent.Unsubscribe(_dimensionLineRowChangedToken);
            _dimensionLineRowChangedToken = null;
        }
    }
}
