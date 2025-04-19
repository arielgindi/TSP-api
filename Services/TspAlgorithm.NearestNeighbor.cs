using System;
using System.Collections.Generic;
using System.Linq;
using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services
{
    /// <summary>
    /// Partial class file containing the Nearest Neighbor approach for TSP.
    /// </summary>
    public static partial class TspAlgorithm
    {
        /// <summary>
        /// Builds a TSP route by repeatedly choosing the nearest unvisited delivery from the current delivery.
        /// </summary>
        /// <param name="allDeliveries">All available deliveries to be routed.</param>
        /// <returns>A list of deliveries forming a route starting and ending at the depot.</returns>
        public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
        {
            // Make a mutable copy of the deliveries we still need to hit (the depot isn't in this list)
            List<Delivery> pendingDeliveries = new List<Delivery>(allDeliveries ?? Enumerable.Empty<Delivery>());

            // Start the route at the depot
            List<Delivery> route = new List<Delivery> { Depot };
            Delivery currentDelivery = Depot;

            // No deliveries?  Easy – depot → depot and done.
            if (!pendingDeliveries.Any())
            {
                route.Add(Depot);
                return route;
            }

            // Keep hopping to the closest unvisited stop until we run out of stops
            while (pendingDeliveries.Count > 0)
            {
                double lowestDistanceSquared = double.MaxValue;
                Delivery nextCandidate = null;
                int nextCandidateIndex = -1;

                // Scan every unvisited delivery and remember the best one
                for (int pendingIndex = 0; pendingIndex < pendingDeliveries.Count; pendingIndex++)
                {
                    double distanceSquared = CalculateEuclideanDistance(currentDelivery, pendingDeliveries[pendingIndex]);

                    // --------- WHY THESE TWO FLAGS EXIST ----------
                    // strictlyBetterDistance
                    // true when this delivery is clearly closer (by more than Epsilon).
                    // tieButLowerId
                    // true when it's basically the *same* distance as our current best (within Epsilon)
                    // and its Id is smaller.  We pick the lower Id so:
                    // 1. the algorithm is deterministic – run it twice, get the same route.
                    // 2. edge tests don't fail randomly because two points were tied.
                    bool strictlyBetterDistance = distanceSquared < lowestDistanceSquared - Constants.Epsilon;
                    bool tieButLowerId =
                        Math.Abs(distanceSquared - lowestDistanceSquared) < Constants.Epsilon &&
                        pendingDeliveries[pendingIndex].Id < (nextCandidate?.Id ?? int.MaxValue);

                    // If we found something closer *or* equally close but with a lower Id,
                    // we treat it as the new best move.
                    if (strictlyBetterDistance || tieButLowerId)
                    {
                        lowestDistanceSquared = distanceSquared;
                        nextCandidate = pendingDeliveries[pendingIndex];
                        nextCandidateIndex = pendingIndex;
                    }
                }

                // Move to the chosen delivery (if any).  If none was found, bail out.
                if (nextCandidate != null && nextCandidateIndex >= 0)
                {
                    route.Add(nextCandidate);
                    currentDelivery = nextCandidate;
                    pendingDeliveries.RemoveAt(nextCandidateIndex);
                }
                else
                {
                    break; // Shouldn't happen, but keeps us safe.
                }
            }

            // Finally, head back home.
            route.Add(Depot);
            return route;
        }
    }
}
