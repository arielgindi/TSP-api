using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// This partial class splits a single optimized route among multiple drivers
/// using a binary search to find the minimal makespan (max distance a driver must travel).
/// 
/// Relies on a DistanceCache to compute sub-route distances in O(1) time 
/// by using precomputed prefix sums. This significantly reduces the overall runtime.
/// 
/// Overall complexity is about O(n log R), where n is the number of deliveries
/// (excluding the depot) and R is the numeric range of possible makespan values.
/// 
/// Example usage:
///   List<Delivery> route = ...; // [Depot, Delivery1, Delivery2, ..., Depot]
///   int[] bestCutIndices;
///   double minMakespan;
///
///   TspAlgorithm.FindBestPartitionBinarySearch(
///       route,
///       3,                    // numberOfDrivers
///       reporter,             // optional callback to track progress
///       out bestCutIndices,
///       out minMakespan
///   );
/// </summary>
public static partial class TspAlgorithm
{
    /// <summary>
    /// Splits an optimized route between multiple drivers,
    /// minimizing the longest distance traveled by any driver.
    /// </summary>
    /// <param name="optimizedRoute">
    ///   Complete route: [Depot, D1, D2, ..., Dn, Depot].
    /// </param>
    /// <param name="numberOfDrivers">Number of available drivers.</param>
    /// <param name="progressReporter">
    ///   Optional callback for progress updates.
    /// </param>
    /// <param name="bestCuts">
    ///   Output: indices marking delivery points where drivers switch.
    /// </param>
    /// <param name="minMakespan">
    ///   Output: shortest possible longest distance traveled by any driver.
    /// </param>
    public static void FindBestPartitionBinarySearch(
        List<Delivery> optimizedRoute,
        int numberOfDrivers,
        ProgressReporter progressReporter,
        out int[] bestCuts,
        out double minMakespan
    )
    {
        // set defaults
        bestCuts = Array.Empty<int>();
        minMakespan = 0.0;
        long iterationCount = 0;

        // bail out on really trivial inputs
        if (optimizedRoute == null || optimizedRoute.Count < 2)
        {
            progressReporter?.Invoke(iterationCount);
            return;
        }

        int totalDeliveries = optimizedRoute.Count - 2;
        if (totalDeliveries < 1)
        {
            progressReporter?.Invoke(iterationCount);
            return;
        }

        DistanceCache distanceCache = new DistanceCache(optimizedRoute);

        // special cases: no drivers, one driver, or too many drivers
        if (numberOfDrivers <= 0)
        {
            minMakespan = distanceCache.GetTotalRouteDistance();
            progressReporter?.Invoke(iterationCount);
            return;
        }

        if (numberOfDrivers == 1)
        {
            minMakespan = distanceCache.GetSubRouteDistance(1, totalDeliveries);
            progressReporter?.Invoke(iterationCount);
            return;
        }

        if (numberOfDrivers >= totalDeliveries)
        {
            SplitOneDeliveryPerDriver(
                distanceCache,
                numberOfDrivers,
                totalDeliveries,
                out bestCuts,
                out minMakespan
            );
            progressReporter?.Invoke(iterationCount);
            return;
        }

        // now do the real binary-search work
        PartitionSearchInternal(
            distanceCache,
            numberOfDrivers,
            totalDeliveries,
            progressReporter,
            out bestCuts,
            out minMakespan
        );
    }

    /// <summary>
    /// Runs a binary search on possible makespan values to find
    /// the smallest max-distance so the route splits into
    /// ? numberOfDrivers sub-routes.
    /// </summary>
    private static void PartitionSearchInternal(
        DistanceCache distanceCache,
        int numberOfDrivers,
        int totalDeliveries,
        ProgressReporter progressReporter,
        out int[] bestCuts,
        out double minMakespan
    )
    {
        // start from “worst single stop” up to full-round trip
        double lowerBound = GetMaximumSingleDeliveryDistance(distanceCache, totalDeliveries);
        double upperBound = distanceCache.GetTotalRouteDistance();

        double bestSoFar = upperBound;
        int[] bestSoFarCuts = Array.Empty<int>();

        int maxIterations = (int)(Math.Log2(Math.Max(upperBound, 1e-9)) + totalDeliveries + 100);
        long tries = 0;

        while (lowerBound <= upperBound && tries < maxIterations)
        {
            tries++;
            double mid = lowerBound + (upperBound - lowerBound) * 0.5;

            bool ok = IsPartitionFeasible(
                distanceCache,
                numberOfDrivers,
                mid,
                out int[] candidateCuts
            );

            if (ok)
            {
                bestSoFar = mid;
                bestSoFarCuts = candidateCuts;
                upperBound = mid - Constants.Epsilon;
            }
            else
            {
                lowerBound = mid + Constants.Epsilon;
            }

            progressReporter?.Invoke(tries);
        }

        // if we never got a valid cut list, try once more at bestSoFar
        if (bestSoFarCuts.Length == 0)
        {
            bool ok = IsPartitionFeasible(
                distanceCache,
                numberOfDrivers,
                bestSoFar,
                out bestSoFarCuts
            );
            if (!ok)
            {
                bestSoFarCuts = Array.Empty<int>();
            }
        }

        // pad with the last delivery if we still lack cuts
        if (bestSoFarCuts.Length < numberOfDrivers - 1 && totalDeliveries > 0)
        {
            bestSoFarCuts = PadCutIndices(
                bestSoFarCuts,
                numberOfDrivers,
                totalDeliveries
            );
        }

        bestCuts = bestSoFarCuts;
        minMakespan = bestSoFar;
        progressReporter?.Invoke(tries);
    }


    /// <summary>
    /// If the number of drivers is greater or equal to the number of deliveries,
    /// we can just assign each delivery to a separate driver. 
    /// </summary>
    private static void SplitOneDeliveryPerDriver(
        DistanceCache distanceCache,
        int numberOfDrivers,
        int totalDeliveries,
        out int[] bestCuts,
        out double minMakespan
    )
    {
        double maximumSingleDeliveryDistance = 0.0;
        List<int> cutIndicesList = [];

        // For each single delivery, record the largest distance
        for (int deliveryIndex = 1; deliveryIndex <= totalDeliveries; deliveryIndex++)
        {
            double singleDistance = distanceCache.GetSubRouteDistance(deliveryIndex, deliveryIndex);
            if (singleDistance > maximumSingleDeliveryDistance)
            {
                maximumSingleDeliveryDistance = singleDistance;
            }
            if (deliveryIndex < totalDeliveries)
            {
                cutIndicesList.Add(deliveryIndex);
            }
        }

        minMakespan = maximumSingleDeliveryDistance;

        // If we don't have enough cuts for all drivers, pad with the last delivery
        while (cutIndicesList.Count < numberOfDrivers - 1)
        {
            cutIndicesList.Add(totalDeliveries);
        }

        bestCuts = cutIndicesList.Take(numberOfDrivers - 1).ToArray();
    }

    /// <summary>
    /// Returns the largest distance for a single delivery sub-route, which helps set the lower bound.
    /// </summary>
    private static double GetMaximumSingleDeliveryDistance(DistanceCache distanceCache, int totalDeliveries)
    {
        double maxDistance = 0.0;
        for (int deliveryIndex = 1; deliveryIndex <= totalDeliveries; deliveryIndex++)
        {
            double distance = distanceCache.GetSubRouteDistance(deliveryIndex, deliveryIndex);
            if (distance > maxDistance)
            {
                maxDistance = distance;
            }
        }
        return maxDistance;
    }

    /// <summary>
    /// Checks if the route can be split so that no sub-route exceeds 'makespanLimit'.
    /// If it's possible, the method returns true and provides the cut indices.
    /// </summary>
    private static bool IsPartitionFeasible(
        DistanceCache distanceCache,
        int driverLimit,
        double makespanLimit,
        out int[] cutIndices
    )
    {
        cutIndices = Array.Empty<int>();

        int totalDeliveries = distanceCache.InternalCount;
        if (totalDeliveries <= 0)
        {
            return true; // No deliveries means no issue
        }

        List<int> cutIndicesList = [];
        int driversUsed = 1;
        int startIndex = 1;

        // Move from the first delivery to the last
        for (int deliveryIndex = 1; deliveryIndex <= totalDeliveries; deliveryIndex++)
        {
            // Calculate this sub-route's distance one time only
            double subRouteDistance = distanceCache.GetSubRouteDistance(startIndex, deliveryIndex);
            if (subRouteDistance > makespanLimit + Constants.Epsilon)
            {
                // If startIndex == deliveryIndex, it means a single delivery is already too big
                if (startIndex == deliveryIndex)
                {
                    return false;
                }

                // Otherwise, we cut right before this delivery
                cutIndicesList.Add(deliveryIndex - 1);
                driversUsed++;
                startIndex = deliveryIndex;

                // Check if we already need more drivers than allowed
                if (driversUsed > driverLimit)
                {
                    return false;
                }
            }
        }

        cutIndices = cutIndicesList.ToArray();
        return true;
    }

    /// <summary>
    /// Adds extra cut indices if we don't already have (numberOfDrivers - 1),
    /// padding with the last delivery index if needed.
    /// </summary>
    private static int[] PadCutIndices(int[] currentCuts, int numberOfDrivers, int totalDeliveries)
    {
        List<int> padded = [.. currentCuts];
        while (padded.Count < numberOfDrivers - 1)
        {
            padded.Add(totalDeliveries);
        }
        // Make sure cuts are unique and in ascending order
        return padded.Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// This helper class stores all prefix sums (and depot distances) for quick O(1) sub-route distance checks.
    /// The route should be [Depot, D1, D2, ..., Dn, Depot].
    /// InternalCount = n, the total number of deliveries.
    /// 
    /// distancePrefixSum[i] = sum of distances (D1->D2->...->Di).
    /// depotToDeliveryDistance[i] = distance from Depot to Di.
    /// deliveryToDepotDistance[i] = distance from Di back to the Depot.
    /// 
    /// Example sub-route distance from Dstart..Dend (1-based):
    ///   Depot->Dstart + Dist(Dstart..Dend) + Dend->Depot
    ///   which is: depotToDeliveryDistance[start]
    ///           + (distancePrefixSum[end] - distancePrefixSum[start])
    ///           + deliveryToDepotDistance[end].
    /// </summary>
    private sealed class DistanceCache
    {
        private readonly List<Delivery> entireRoute;
        private readonly double[] distancePrefixSum;
        private readonly double[] depotToDeliveryDistance;
        private readonly double[] deliveryToDepotDistance;

        public int InternalCount { get; }

        public DistanceCache(List<Delivery> route)
        {
            entireRoute = route ?? throw new ArgumentNullException(nameof(route));

            // The route is [Depot + N deliveries + Depot] => total length = N + 2
            InternalCount = route.Count - 2;
            if (InternalCount <= 0)
            {
                // No real deliveries => build minimal arrays
                distancePrefixSum = Array.Empty<double>();
                depotToDeliveryDistance = Array.Empty<double>();
                deliveryToDepotDistance = Array.Empty<double>();
                return;
            }

            distancePrefixSum = new double[InternalCount + 1];
            depotToDeliveryDistance = new double[InternalCount + 1];
            deliveryToDepotDistance = new double[InternalCount + 1];

            // Fill depotToDeliveryDistance / deliveryToDepotDistance
            for (int deliveryIndex = 1; deliveryIndex <= InternalCount; deliveryIndex++)
            {
                Delivery delivery = route[deliveryIndex];
                depotToDeliveryDistance[deliveryIndex] =
                    CalculateEuclideanDistance(route[0], delivery);
                deliveryToDepotDistance[deliveryIndex] =
                    CalculateEuclideanDistance(delivery, route[^1]);
            }

            // Build prefix sums among the deliveries themselves
            distancePrefixSum[0] = 0.0;
            for (int deliveryIndex = 2; deliveryIndex <= InternalCount; deliveryIndex++)
            {
                double distance = CalculateEuclideanDistance(
                    route[deliveryIndex - 1],
                    route[deliveryIndex]
                );
                distancePrefixSum[deliveryIndex] = distancePrefixSum[deliveryIndex - 1] + distance;
            }
        }

        /// <summary>
        /// Returns the total distance of [Depot -> D1 -> D2 -> ... -> Dn -> Depot].
        /// </summary>
        public double GetTotalRouteDistance()
        {
            if (InternalCount <= 0)
            {
                return 0.0;
            }

            return depotToDeliveryDistance[1]
                   + distancePrefixSum[InternalCount]
                   + deliveryToDepotDistance[InternalCount];
        }

        /// <summary>
        /// Returns the distance of [Depot -> Dstart -> ... -> Dend -> Depot].
        /// If the indices are invalid, returns 0.
        /// </summary>
        public double GetSubRouteDistance(int start, int end)
        {
            if (start < 1 || end < start || end > InternalCount)
            {
                return 0.0;
            }

            double depotToStart = depotToDeliveryDistance[start];
            double innerDistance = distancePrefixSum[end] - distancePrefixSum[start];
            double endToDepot = deliveryToDepotDistance[end];
            return depotToStart + innerDistance + endToDepot;
        }


    }
}
