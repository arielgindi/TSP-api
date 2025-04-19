namespace RouteOptimizationApi.Models;

public class DriverRoute
{
    public int DriverId { get; set; }
    public List<Delivery> RoutePoints { get; set; } = new List<Delivery>();
    public List<int> DeliveryIds { get; set; } = new List<int>();
    public double Distance { get; set; }
    public List<int> OriginalIndices { get; set; } = new List<int>();
}

public class Delivery
{
    public int Id { get; }
    public int X { get; }
    public int Y { get; }

    public Delivery(int id, int x, int y)
    {
        Id = id;
        X = x;
        Y = y;
    }

    public override string ToString()
    {
        if (Id == 0) return "Depot(0,0)";
        return "Delivery#" + Id + "(" + X + "," + Y + ")";
    }
}
