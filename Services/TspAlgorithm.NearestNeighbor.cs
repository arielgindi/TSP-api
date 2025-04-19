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
            // Create a copy of the deliveries we need to visit, excluding the depot
            List<Delivery> pendingDeliveries = new List<Delivery>(allDeliveries ?? Enumerable.Empty<Delivery>());

            // Initialize the route with the depot as the starting point
            List<Delivery> route = new List<Delivery> { Depot };
            Delivery currentDelivery = Depot;

            // If there are no deliveries, simply return depot -> depot
            if (!pendingDeliveries.Any())
            {
                route.Add(Depot);
                return route;
            }

            // Continue until all deliveries have been visited or no closer delivery is found
            while (pendingDeliveries.Count > 0)
            {
                double lowestDistanceSquared = double.MaxValue;
                Delivery nextCandidate = null;
                int nextCandidateIndex = -1;

                // Check every unvisited delivery to find the closest one
                for (int pendingIndex = 0; pendingIndex < pendingDeliveries.Count; pendingIndex++)
                {
                    double distanceSquared = CalculateEuclideanDistanceSquared(currentDelivery, pendingDeliveries[pendingIndex]);

                    // If we find a strictly smaller distance, or an equally small distance with a lower ID, we update our best pick
                    bool strictlyBetterDistance = distanceSquared < lowestDistanceSquared - Constants.Epsilon;
                    bool tieButLowerId =
                        Math.Abs(distanceSquared - lowestDistanceSquared) < Constants.Epsilon &&
                        pendingDeliveries[pendingIndex].Id < (nextCandidate?.Id ?? int.MaxValue);

                    if (strictlyBetterDistance || tieButLowerId)
                    {
                        lowestDistanceSquared = distanceSquared;
                        nextCandidate = pendingDeliveries[pendingIndex];
                        nextCandidateIndex = pendingIndex;
                    }
                }

                // If a valid next candidate is found, move there; otherwise, break
                if (nextCandidate != null && nextCandidateIndex >= 0)
                {
                    route.Add(nextCandidate);
                    currentDelivery = nextCandidate;
                    pendingDeliveries.RemoveAt(nextCandidateIndex);
                }
                else
                {
                    break;
                }
            }

            // Close the loop by returning to the depot
            route.Add(Depot);
            return route;
        }
    }
}
