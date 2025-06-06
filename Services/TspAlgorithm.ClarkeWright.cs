﻿using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// Provides a Clarke-Wright Savings implementation in O(n²) time,
/// using an optimized savings computation and a least-significant-digit
/// (LSD) radix sort for double values.
///
/// Merges are done with union-find in O(1) per merge, ensuring overall O(n²).
/// </summary>
public static partial class TspAlgorithm
{
    /// <summary>
    /// Builds a TSP route using the Clarke-Wright method.
    /// This uses:
    /// 1) A single pass to gather all valid (positive) savings,
    /// 2) An LSD radix sort on double values for sorting in ascending order,
    ///    followed by reversing for descending order,
    /// 3) A union-find structure to merge route segments in constant time.
    ///
    /// Example:
    /// --------
    ///  var deliveries = new List<Delivery> { new Delivery(1,10,10), new Delivery(2,20,20), ... };
    ///  List<Delivery> bestRoute = TspAlgorithm.ConstructClarkeWrightRoute(deliveries);
    ///
    /// Complexity:
    ///  - O(n²) to compute all pairwise savings.
    ///  - O(n²) to LSD-radix-sort the savings.
    ///  - O(n²) merges at O(1) each.
    /// </summary>
    public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> deliveries)
    {
        // Handle trivial cases first.
        List<Delivery>? trivialResult = CheckTrivialCases(deliveries);
        if (trivialResult != null)
        {
            return trivialResult;
        }

        // Convert all deliveries to an array for quick indexing.
        Delivery[] deliveryArray = deliveries.ToArray();
        int totalDeliveries = deliveryArray.Length;

        // 1) Precompute distances in O(n²).
        double[] distanceFromDepot = PrecomputeDistanceFromDepot(deliveryArray);
        double[,] distanceBetweenDeliveries = PrecomputeDistanceBetweenDeliveries(deliveryArray);

        // 2) Gather all positive savings in a single pass (O(n²)).
        SavingPair[] finalSavings = GatherPositiveSavings(distanceFromDepot, distanceBetweenDeliveries);

        // 3) Sort savings in ascending order using LSD Radix, then reverse for descending.
        LSDSortDoubleAscending(finalSavings);
        ReverseArray(finalSavings);

        // 4) Use union-find for merges in O(1).
        RouteUnionFind routeUnion = new RouteUnionFind(totalDeliveries);
        foreach (SavingPair savingPair in finalSavings)
        {
            routeUnion.AttemptMerge(savingPair.IndexA, savingPair.IndexB);
        }

        // 5) Construct final route from the union-find chains.
        List<Delivery> finalRoute = routeUnion.BuildRoute(deliveryArray);

        // Add depot at start and end.
        finalRoute.Insert(0, Depot);
        finalRoute.Add(Depot);

        return finalRoute;
    }

    // Checks if we have zero or one delivery and returns a trivial route if so.
    // Otherwise, returns null to proceed with the main algorithm.
    private static List<Delivery>? CheckTrivialCases(List<Delivery> deliveries)
    {
        if (deliveries == null || deliveries.Count == 0)
        {
            // Return just depot -> depot
            return [Depot, Depot];
        }

        if (deliveries.Count == 1)
        {
            // Return depot -> single delivery -> depot
            return [Depot, deliveries[0], Depot];
        }

        return null;
    }

    // Gets distance from depot to each delivery.
    private static double[] PrecomputeDistanceFromDepot(Delivery[] deliveryArray)
    {
        int totalDeliveries = deliveryArray.Length;
        double[] distanceFromDepot = new double[totalDeliveries];

        for (int deliveryIndex = 0; deliveryIndex < totalDeliveries; deliveryIndex++)
        {
            distanceFromDepot[deliveryIndex] = CalculateEuclideanDistance(Depot, deliveryArray[deliveryIndex]);
        }

        return distanceFromDepot;
    }

    // Creates a matrix of distances between all deliveries.
    private static double[,] PrecomputeDistanceBetweenDeliveries(Delivery[] deliveryArray)
    {
        int totalDeliveries = deliveryArray.Length;
        double[,] distanceMatrix = new double[totalDeliveries, totalDeliveries];

        for (int rowIndex = 0; rowIndex < totalDeliveries; rowIndex++)
        {
            for (int colIndex = rowIndex + 1; colIndex < totalDeliveries; colIndex++)
            {
                double currentDistance = CalculateEuclideanDistance(
                    deliveryArray[rowIndex],
                    deliveryArray[colIndex]
                );

                distanceMatrix[rowIndex, colIndex] = currentDistance;
                distanceMatrix[colIndex, rowIndex] = currentDistance;
            }
        }

        return distanceMatrix;
    }

    // Gathers all positive (beneficial) savings from the precomputed distances.
    private static SavingPair[] GatherPositiveSavings(
        double[] distanceFromDepot,
        double[,] distanceBetweenDeliveries
    )
    {
        int totalDeliveries = distanceFromDepot.Length;
        int maxPairs = totalDeliveries * (totalDeliveries - 1) / 2;
        SavingPair[] savingsBuffer = new SavingPair[maxPairs];
        int validSavingsCount = 0;

        for (int firstDeliveryIndex = 0; firstDeliveryIndex < totalDeliveries; firstDeliveryIndex++)
        {
            double depotDistanceFirst = distanceFromDepot[firstDeliveryIndex];

            for (int secondDeliveryIndex = firstDeliveryIndex + 1; secondDeliveryIndex < totalDeliveries; secondDeliveryIndex++)
            {
                double savingValue = depotDistanceFirst
                                     + distanceFromDepot[secondDeliveryIndex]
                                     - distanceBetweenDeliveries[firstDeliveryIndex, secondDeliveryIndex];

                // Only keep positive (beneficial) savings.
                if (savingValue > 0.0)
                {
                    savingsBuffer[validSavingsCount] = new SavingPair(
                        firstDeliveryIndex,
                        secondDeliveryIndex,
                        savingValue
                    );
                    validSavingsCount++;
                }
            }
        }

        // Copy only the valid portion of the array.
        SavingPair[] finalSavings = new SavingPair[validSavingsCount];
        Array.Copy(savingsBuffer, finalSavings, validSavingsCount);
        return finalSavings;
    }

    /// <summary>
    /// Reverses the given array in place to convert ascending order to descending.
    ///
    /// Example:
    /// --------
    ///   var pairs = new SavingPair[] { new (0,1,10.5), new (0,2,11.2) };
    ///   ReverseArray(pairs);
    ///   // now largest saving is first
    /// </summary>
    private static void ReverseArray(SavingPair[] array)
    {
        int leftIndex = 0;
        int rightIndex = array.Length - 1;
        while (leftIndex < rightIndex)
        {
            SavingPair temp = array[leftIndex];
            array[leftIndex] = array[rightIndex];
            array[rightIndex] = temp;
            leftIndex++;
            rightIndex--;
        }
    }

    /// <summary>
    /// Sorts an array of SavingPair based on the SavingValue in ascending order using a stable
    /// Least Significant Digit (LSD) radix sort tailored for double values.
    ///
    /// It correctly sorts doubles by manipulating their bits to preserve numeric ordering.
    ///
    /// Example:
    /// --------
    /// var savings = new SavingPair[] { new (0,1, 50.0), new (0,2, 10.0), new (1,2, 200.0) };
    /// LSDSortDoubleAscending(savings);
    /// // sorted: [(0,2,10.0), (0,1,50.0), (1,2,200.0)]
    /// </summary>
    private static void LSDSortDoubleAscending(SavingPair[] array)
    {
        int length = array.Length;

        // Early exit if array is empty or has one element (already sorted)
        if (length <= 1) return;

        SavingPair[] buffer = new SavingPair[length];

        // Each double is 64-bit (8 bytes), sorting from least significant byte
        for (int byteShift = 0; byteShift < 64; byteShift += 8)
        {
            int[] count = new int[256];

            // Count occurrence of each byte value
            for (int i = 0; i < length; i++)
            {
                ulong bits = DoubleToSortableBits(array[i].SavingValue);
                int bucket = (int)((bits >> byteShift) & 0xFF);
                count[bucket]++;
            }

            // Compute starting index for each bucket (prefix sum)
            int[] prefixSum = new int[256];
            prefixSum[0] = 0;
            for (int bucketIndex = 1; bucketIndex < 256; bucketIndex++)
            {
                prefixSum[bucketIndex] = prefixSum[bucketIndex - 1] + count[bucketIndex - 1];
            }

            // Distribute elements into buffer in correct bucket positions
            for (int i = 0; i < length; i++)
            {
                ulong bits = DoubleToSortableBits(array[i].SavingValue);
                int bucket = (int)((bits >> byteShift) & 0xFF);
                int position = prefixSum[bucket]++;
                buffer[position] = array[i];
            }

            // Copy sorted data back to original array for next iteration
            Array.Copy(buffer, array, length);
        }
    }


    /// <summary>
    /// Converts a positive double into a sortable unsigned 64-bit pattern
    /// by flipping the sign bit. This ensures that ascending numeric values
    /// map to ascending integer order in an unsigned sense.
    /// 
    /// Example:
    /// --------
    ///   double val = 100.0;
    ///   ulong bits = DoubleToSortableBits(val);
    ///   // bits is a monotonic representation used in LSD sorting
    /// </summary>
    private static ulong DoubleToSortableBits(double value)
    {
        long rawBits = BitConverter.DoubleToInt64Bits(value);
        const long SignBitMask = unchecked((long)0x8000000000000000);
        long flipped = rawBits ^ SignBitMask;
        return (ulong)flipped;
    }

    /// <summary>
    /// Represents a specialized union-find structure for Clarke-Wright routes.
    /// Each delivery starts as its own route. We track the "head" and "tail" of each route,
    /// allowing merges (tail->head or head->tail) in constant time.
    /// </summary>
    private sealed class RouteUnionFind
    {
        private readonly int[] parent;
        private readonly int[] rank;
        private readonly int[] routeHead;
        private readonly int[] routeTail;
        private readonly int[] nextIndexInRoute;

        public RouteUnionFind(int size)
        {
            parent = new int[size];
            rank = new int[size];
            routeHead = new int[size];
            routeTail = new int[size];
            nextIndexInRoute = new int[size];

            // Initialize each node as a separate "route".
            for (int current = 0; current < size; current++)
            {
                parent[current] = current;
                rank[current] = 0;
                routeHead[current] = current;
                routeTail[current] = current;
                nextIndexInRoute[current] = -1;
            }
        }

        /// <summary>
        /// Tries to merge the routes of 'firstIndex' and 'secondIndex' if one is the tail
        /// of its route and the other is the head of its route.
        ///
        /// No complexity beyond O(1) merges thanks to union-find plus head/tail references.
        ///
        /// Example:
        /// --------
        ///   AttemptMerge(5,9);
        ///   // If 5 is route A's tail and 9 is route B's head, merges them.
        /// </summary>
        public void AttemptMerge(int firstIndex, int secondIndex)
        {
            int leaderA = FindLeader(firstIndex);
            int leaderB = FindLeader(secondIndex);

            if (leaderA == leaderB) return; // Already in the same route.

            bool forwardMerge = routeTail[leaderA] == firstIndex && routeHead[leaderB] == secondIndex;
            bool reverseMerge = routeTail[leaderB] == secondIndex && routeHead[leaderA] == firstIndex;

            if (forwardMerge)
            {
                // Connect route A's tail to route B's head.
                nextIndexInRoute[firstIndex] = secondIndex;
                Union(leaderA, leaderB);

                int newLeader = FindLeader(firstIndex);
                routeHead[newLeader] = routeHead[leaderA];
                routeTail[newLeader] = routeTail[leaderB];
            }
            else if (reverseMerge)
            {
                // Connect route B's tail to route A's head.
                nextIndexInRoute[secondIndex] = firstIndex;
                Union(leaderB, leaderA);

                int newLeader = FindLeader(secondIndex);
                routeHead[newLeader] = routeHead[leaderB];
                routeTail[newLeader] = routeTail[leaderA];
            }
        }

        /// <summary>
        /// Builds the final sequence of deliveries by traversing each route from head->tail.
        /// If multiple routes remain disconnected, they are appended in arbitrary order.
        ///
        /// Example:
        /// --------
        ///   List&lt;Delivery&gt; route = BuildRoute(deliveryArray);
        ///   // route now is a single chain of all merged deliveries
        /// </summary>
        public List<Delivery> BuildRoute(Delivery[] deliveryArray)
        {
            List<Delivery> assembledRoute = [];
            HashSet<int> visitedLeaders = [];

            for (int node = 0; node < deliveryArray.Length; node++)
            {
                int leader = FindLeader(node);
                if (!visitedLeaders.Contains(leader))
                {
                    visitedLeaders.Add(leader);

                    int currentNode = routeHead[leader];
                    while (currentNode != -1)
                    {
                        assembledRoute.Add(deliveryArray[currentNode]);
                        currentNode = nextIndexInRoute[currentNode];
                    }
                }
            }
            return assembledRoute;
        }

        private int FindLeader(int node)
        {
            // Path compression for near O(1) performance in practice.
            if (parent[node] != node)
            {
                parent[node] = FindLeader(parent[node]);
            }
            return parent[node];
        }

        private void Union(int rootA, int rootB)
        {
            // Union by rank to keep trees shallow.
            if (rank[rootA] < rank[rootB])
            {
                parent[rootA] = rootB;
            }
            else if (rank[rootA] > rank[rootB])
            {
                parent[rootB] = rootA;
            }
            else
            {
                parent[rootB] = rootA;
                rank[rootA]++;
            }
        }
    }

    /// <summary>
    /// Represents a pair of delivery indices plus the computed saving value.
    ///
    /// Example:
    /// --------
    ///   var sp = new SavingPair(3, 7, 120.5);
    ///   // means merging deliveries 3 and 7 yields a saving of 120.5
    /// </summary>
    private record SavingPair(int IndexA, int IndexB, double SavingValue);
}
