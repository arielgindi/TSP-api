using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// Implements the Nearest Neighbor heuristic for solving the Traveling Salesman Problem (TSP),
/// utilizing a KD-Tree for efficient nearest-point queries (O(n log n) complexity).
/// </summary>
public static partial class TspAlgorithm
{
    /// <summary>
    /// Constructs an optimized route starting from the depot, visiting each delivery exactly once by 
    /// repeatedly choosing the closest unvisited delivery, and finally returning to the depot.
    /// </summary>
    /// <param name="allDeliveries">List of deliveries to include in the route.</param>
    /// <returns>An ordered list representing the optimized route (Depot → deliveries → Depot).</returns>
    public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
    {
        // Initialize the route starting from the depot
        List<Delivery> route = [Depot];

        // No deliveries to handle; return route as Depot → Depot
        if (allDeliveries is null || allDeliveries.Count == 0)
        {
            route.Add(Depot);
            return route;
        }

        // Build KD-Tree for efficient nearest neighbor queries
        KdTree kdTree = new(allDeliveries);

        // If the KD-Tree has no valid (unvisited) deliveries, end the route at the depot
        if (!kdTree.HasUnvisited)
        {
            route.Add(Depot);
            return route;
        }

        // Iteratively pick the closest unvisited delivery
        Delivery current = Depot;
        Delivery? nextDelivery;

        while (kdTree.HasUnvisited)
        {
            nextDelivery = kdTree.PopNearest(current);

            // If no next delivery is available, end the loop naturally (without break)
            if (nextDelivery is not null)
            {
                route.Add(nextDelivery);
                current = nextDelivery;
            }
            else
            {
                // No more valid next deliveries; end loop naturally
                kdTree = new([]); //  marks HasUnvisited as false
            }
        }

        // Complete the route by returning to the depot
        route.Add(Depot);

        return route;
    }
}
