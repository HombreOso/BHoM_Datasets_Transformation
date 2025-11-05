using System.Collections.Generic;
using BH.oM.Base;

namespace BH.oM.PlantRoomSizer
{
    public partial class DataSeries : BHoMObject
    {
        public List<CurvePoint> DataPoints { get; set; }

    }
    public partial class DoubleDataSeries
    {
        public DataSeries Upper { get; set; }
        public DataSeries Lower { get; set; }

    }
}
