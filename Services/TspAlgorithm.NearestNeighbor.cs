using System.Collections.Generic;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// Partial class: Nearest Neighbor TSP using a KD-Tree for O(n log n) performance.
/// </summary>
public static partial class TspAlgorithm
{
    /// <summary>
    /// Constructs a TSP route by repeatedly picking the nearest unvisited delivery
    /// (via KD-Tree) from the current location. Returns depot->deliveries->depot.
    /// </summary>
    public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
    {
        // Always start at the depot
        var route = new List<Delivery> { Depot };

        // If no deliveries given, just return depot->depot
        if (allDeliveries == null || allDeliveries.Count == 0)
        {
            route.Add(Depot);
            return route;
        }

        // Build KD-Tree from all deliveries (ignoring Id=0)
        var kdTree = new KdTree(allDeliveries);

        // If no unvisited deliveries, we're done
        if (!kdTree.HasUnvisited)
        {
            route.Add(Depot);
            return route;
        }

        // Walk through all unvisited deliveries, always choosing the nearest
        Delivery current = Depot;
        while (kdTree.HasUnvisited)
        {
            Delivery? next = kdTree.PopNearest(current);
            if (next == null) break; // no more unvisited or unexpected

            route.Add(next);
            current = next;
        }

        // End at the depot
        route.Add(Depot);
        return route;
    }
}