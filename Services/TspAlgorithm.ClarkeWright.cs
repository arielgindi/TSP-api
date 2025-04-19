using System;
using System.Collections.Generic;
using System.Linq;
using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services
{
    /// <summary>
    /// Partial class file containing the Clarke-Wright Savings approach for TSP.
    /// </summary>
    public static partial class TspAlgorithm
    {
        /// <summary>
        /// Constructs a route using the Clarke-Wright Savings approach.
        /// Each delivery starts in its own mini-route, and these routes
        /// are merged based on the greatest distance "savings." The result
        /// is a single route starting and ending at the depot.
        /// </summary>
        /// <param name="deliveries">All deliveries to be included in the route.</param>
        /// <returns>A route starting and ending at the depot, containing all deliveries.</returns>
        public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> deliveries)
        {
            // if there are zero deliveries, just return Depot to Depot.
            if (deliveries == null || deliveries.Count == 0)
            {
                return new List<Delivery> { Depot, Depot };
            }
            if (deliveries.Count == 1)
            {
                return new List<Delivery> { Depot, deliveries[0], Depot };
            }

            // That's storing distance between each 2 deleveries points, 
            Dictionary<(int, int), double> distanceCache = new Dictionary<(int, int), double>();
            List<Saving> allSavings = ComputeAllSavings(deliveries, distanceCache);

            (Dictionary<int, List<int>> RepToRoute,
             Dictionary<int, int> DeliveryToRep,
             Dictionary<int, int> FirstDeliveryInRoute,
             Dictionary<int, int> LastDeliveryInRoute) groupData
                = InitializeRouteGroups(deliveries);

            MergeUsingSavings(allSavings, groupData);

            List<Delivery> finalRoute = BuildFinalRoute(deliveries, groupData);
            finalRoute.Add(Depot);
            return finalRoute;
        }

        /// <summary>
        /// Calculates distance "savings" for each pair of deliveries.
        /// The formula of saving betten deleviry points a and b is:
        /// Savings(a,b) = Distance(Depot, a) + Distance(Depot, b) - Distance(a,b)
        /// </summary>
        private static List<Saving> ComputeAllSavings(
            List<Delivery> deliveries,
            Dictionary<(int, int), double> distanceCache
        )
        {
            List<Saving> savingsList = new List<Saving>();

            double GetDistance(Delivery first, Delivery second)
            {
                int lowId = Math.Min(first.Id, second.Id);
                int highId = Math.Max(first.Id, second.Id);

                // If that distance was calculated before, return it
                if (!distanceCache.TryGetValue((lowId, highId), out double dist))
                {
                    // if we went here, thus its the first time asked for this distance of these 2 points, 
                    // thus, its caclulated first time and than store in the distanceCache.
                    dist = CalculateEuclideanDistance(first, second);
                    distanceCache[(lowId, highId)] = dist;
                }
                
                return dist;
            }

            for (int indexA = 0; indexA < deliveries.Count; indexA++)
            {
                Delivery deleviryA = deliveries[indexA];
                double distanceDepotToA = GetDistance(Depot, deleviryA);

                for (int indexB = indexA + 1; indexB < deliveries.Count; indexB++)
                {
                    Delivery deleviryB = deliveries[indexB];
                    double distanceDepotToB = GetDistance(Depot, deleviryB);
                    double distAToB = GetDistance(deleviryA, deleviryB);

                    // Savings(a,b) = Distance(Depot, a) + Distance(Depot, b) - Distance(a,b)
                    double savingValue = distanceDepotToA + distanceDepotToB - distAToB;

                    // TODO: ask about this line later
                    if (savingValue > Constants.Epsilon)
                    {
                        savingsList.Add(new Saving(deleviryA.Id, deleviryB.Id, savingValue));
                    }
                }
            }

            // after storing each saving, we store it for later
            savingsList.Sort((s1, s2) => s2.Value.CompareTo(s1.Value));
            return savingsList;
        }

        /// <summary>
        /// Initializes each delivery as its own mini-route with the same route representative.
        /// </summary>
        /// TODO: change this!!! i dont like this, change it to something like a graph of a linked list
        private static (
            Dictionary<int, List<int>> RepToRoute,
            Dictionary<int, int> DeliveryToRep,
            Dictionary<int, int> FirstDeliveryInRoute,
            Dictionary<int, int> LastDeliveryInRoute
        ) InitializeRouteGroups(List<Delivery> deliveries)
        {
            Dictionary<int, List<int>> repToRoute = new Dictionary<int, List<int>>();
            Dictionary<int, int> deliveryToRep = new Dictionary<int, int>();
            Dictionary<int, int> firstDeliveryInRoute = new Dictionary<int, int>();
            Dictionary<int, int> lastDeliveryInRoute = new Dictionary<int, int>();

            foreach (Delivery delivery in deliveries)
            {
                repToRoute[delivery.Id] = new List<int> { delivery.Id };
                deliveryToRep[delivery.Id] = delivery.Id;
                firstDeliveryInRoute[delivery.Id] = delivery.Id;
                lastDeliveryInRoute[delivery.Id] = delivery.Id;
            }

            return (repToRoute, deliveryToRep, firstDeliveryInRoute, lastDeliveryInRoute);
        }

        /// <summary>
        /// Merges mini-routes according to savings, either forward or reverse.
        /// </summary>
        private static void MergeUsingSavings(
            List<Saving> allSavings,
            (
                Dictionary<int, List<int>> RepToRoute,
                Dictionary<int, int> DeliveryToRep,
                Dictionary<int, int> FirstDeliveryInRoute,
                Dictionary<int, int> LastDeliveryInRoute
            ) groupData
        )
        {
            foreach (Saving savingItem in allSavings)
            {
                int repI = groupData.DeliveryToRep[savingItem.FirstDeliveryId];
                int repJ = groupData.DeliveryToRep[savingItem.SecondDeliveryId];

                if (repI == repJ)
                {
                    continue;
                }

                bool forwardMergePossible =
                    groupData.LastDeliveryInRoute[repI] == savingItem.FirstDeliveryId &&
                    groupData.FirstDeliveryInRoute[repJ] == savingItem.SecondDeliveryId;

                bool reverseMergePossible =
                    groupData.LastDeliveryInRoute[repJ] == savingItem.SecondDeliveryId &&
                    groupData.FirstDeliveryInRoute[repI] == savingItem.FirstDeliveryId;

                if (forwardMergePossible)
                {
                    MergeRoutes(
                        groupData.RepToRoute,
                        groupData.DeliveryToRep,
                        groupData.LastDeliveryInRoute,
                        repI,
                        repJ
                    );
                    groupData.LastDeliveryInRoute[repI] = groupData.LastDeliveryInRoute[repJ];
                    CleanupRoute(repJ, groupData.RepToRoute, groupData.FirstDeliveryInRoute, groupData.LastDeliveryInRoute);
                }
                else if (reverseMergePossible)
                {
                    MergeRoutes(
                        groupData.RepToRoute,
                        groupData.DeliveryToRep,
                        groupData.LastDeliveryInRoute,
                        repJ,
                        repI
                    );
                    groupData.LastDeliveryInRoute[repJ] = groupData.LastDeliveryInRoute[repI];
                    CleanupRoute(repI, groupData.RepToRoute, groupData.FirstDeliveryInRoute, groupData.LastDeliveryInRoute);
                }
            }
        }

        /// <summary>
        /// Appends all deliveries in the merging route to the target route, updating 
        /// route membership and final delivery references.
        /// </summary>
        private static void MergeRoutes(
            Dictionary<int, List<int>> repToRoute,
            Dictionary<int, int> deliveryToRep,
            Dictionary<int, int> lastDelInRoute,
            int targetRep,
            int mergingRep
        )
        {
            List<int> targetRoute = repToRoute[targetRep];
            List<int> mergingRoute = repToRoute[mergingRep];

            targetRoute.AddRange(mergingRoute);

            foreach (int mergedId in mergingRoute)
            {
                deliveryToRep[mergedId] = targetRep;
            }

            lastDelInRoute[targetRep] = lastDelInRoute[mergingRep];
        }

        /// <summary>
        /// Removes the merged route's dictionaries since it no longer exists independently.
        /// </summary>
        private static void CleanupRoute(
            int oldRep,
            Dictionary<int, List<int>> repToRoute,
            Dictionary<int, int> firstDeliveryInRoute,
            Dictionary<int, int> lastDeliveryInRoute
        )
        {
            repToRoute.Remove(oldRep);
            firstDeliveryInRoute.Remove(oldRep);
            lastDeliveryInRoute.Remove(oldRep);
        }

        /// <summary>
        /// Combines the remaining route clusters into the final route. If there's only
        /// one cluster, it already represents a complete route. Otherwise, the
        /// method concatenates all of them.
        /// </summary>
        private static List<Delivery> BuildFinalRoute(
            List<Delivery> deliveries,
            (
                Dictionary<int, List<int>> RepToRoute,
                Dictionary<int, int> DeliveryToRep,
                Dictionary<int, int> FirstDeliveryInRoute,
                Dictionary<int, int> LastDeliveryInRoute
            ) groupData
        )
        {
            List<Delivery> finalRoute = new List<Delivery> { Depot };

            if (groupData.RepToRoute.Count > 1)
            {
                foreach (List<int> routeIds in groupData.RepToRoute.Values)
                {
                    foreach (int deliveryId in routeIds)
                    {
                        Delivery foundDelivery = deliveries.First(d => d.Id == deliveryId);
                        finalRoute.Add(foundDelivery);
                    }
                }
            }
            else
            {
                List<int> singleRouteIds = groupData.RepToRoute.Values.First();
                foreach (int deliveryId in singleRouteIds)
                {
                    Delivery foundDelivery = deliveries.First(d => d.Id == deliveryId);
                    finalRoute.Add(foundDelivery);
                }
            }

            return finalRoute;
        }
    }
}
