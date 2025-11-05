

using BH.oM.Base;

namespace Point2D;

public class Point2DClass(double x, double y) : IObject
{
    public double X { get; } = x;
    public double Y { get; } = y;
}