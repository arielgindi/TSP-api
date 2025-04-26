using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services
{
    /// <summary>
    /// Partial class implementing a Clarke-Wright Savings method in O(n²) time.
    /// Uses a union-find structure for instant merges and bucket-sort to handle
    /// the savings array efficiently without O(n² log(n²)) overhead.
    /// </summary>
    public static partial class TspAlgorithm
    {
        /// <summary>
        /// Constructs a Traveling Salesman route using the Clarke-Wright Savings
        /// approach in O(n²) time. It merges delivery routes instantly with union-find
        /// and sorts savings in near-linear time using a bucket-based method.
        /// </summary>
        public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> deliveryList)
        {
            // Handle trivial cases (0 or 1 delivery).
            if (deliveryList == null || deliveryList.Count == 0)
            {
                List<Delivery> emptyRoute = new List<Delivery>();
                emptyRoute.Add(Depot);
                emptyRoute.Add(Depot);
                return emptyRoute;
            }
            if (deliveryList.Count == 1)
            {
                List<Delivery> singleRoute = new List<Delivery>();
                singleRoute.Add(Depot);
                singleRoute.Add(deliveryList[0]);
                singleRoute.Add(Depot);
                return singleRoute;
            }

            // 1) Convert deliveries to an array for simpler indexing.
            Delivery[] deliveriesArray = deliveryList.ToArray();
            int deliveryCount = deliveriesArray.Length;

            // 2) Precompute all required distances in O(n²):
            //    (a) Distance from Depot to each delivery
            //    (b) Distances among all deliveries
            double[] distancesFromDepot = new double[deliveryCount];
            for (int i = 0; i < deliveryCount; i++)
            {
                distancesFromDepot[i] = CalculateEuclideanDistance(Depot, deliveriesArray[i]);
            }

            double[,] distancesBetweenDeliveries = new double[deliveryCount, deliveryCount];
            for (int i = 0; i < deliveryCount; i++)
            {
                for (int j = i + 1; j < deliveryCount; j++)
                {
                    double dist = CalculateEuclideanDistance(deliveriesArray[i], deliveriesArray[j]);
                    distancesBetweenDeliveries[i, j] = dist;
                    distancesBetweenDeliveries[j, i] = dist;
                }
            }

            // 3) Compute all savings (i, j) in O(n²). Keep track of max to set up bucket range.
            //    Savings = Dist(Depot, i) + Dist(Depot, j) - Dist(i, j).
            List<SavingRecord> allSavings = new List<SavingRecord>(deliveryCount * (deliveryCount - 1) / 2);
            double maxSavingValue = 0.0;

            for (int i = 0; i < deliveryCount; i++)
            {
                for (int j = i + 1; j < deliveryCount; j++)
                {
                    double currentSaving = distancesFromDepot[i]
                                         + distancesFromDepot[j]
                                         - distancesBetweenDeliveries[i, j];
                    // Only consider positive savings
                    if (currentSaving > 0.0)
                    {
                        allSavings.Add(new SavingRecord(i, j, currentSaving));
                        if (currentSaving > maxSavingValue)
                        {
                            maxSavingValue = currentSaving;
                        }
                    }
                }
            }

            // 4) Sort the savings in descending order using a bucket approach to avoid O(n² log n²).
            List<SavingRecord> sortedSavings = SortSavingsDescendingUsingBuckets(allSavings, maxSavingValue);

            // 5) Union-find for merging routes in constant time per pair.
            DeliveryRouteUnionFind unionFindRoutes = new DeliveryRouteUnionFind(deliveryCount);

            // 6) Merge routes by highest savings first.
            for (int s = 0; s < sortedSavings.Count; s++)
            {
                SavingRecord currentPair = sortedSavings[s];
                unionFindRoutes.TryMergeRoutes(currentPair.FirstDeliveryIndex, currentPair.SecondDeliveryIndex);
            }

            // 7) Build final path from merged chains.
            List<Delivery> finalRouteList = unionFindRoutes.BuildFinalRoute(deliveriesArray);

            // 8) Depot at the start and end.
            finalRouteList.Insert(0, Depot);
            finalRouteList.Add(Depot);

            return finalRouteList;
        }

        /// <summary>
        /// Bucket-sorts all savings in descending order.
        /// The range is determined by 'maximumSaving', which is taken from the largest
        /// savings value encountered. Adjust buffer sizes as needed for your data.
        /// </summary>
        private static List<SavingRecord> SortSavingsDescendingUsingBuckets(
            List<SavingRecord> savings,
            double maximumSaving
        )
        {
            // Decide an integer range for bucket indices. If max is 30000.99 => 30001 buckets, etc.
            // In practice, you could clamp or expand this to match your coordinate range.
            int bucketCount = (int)Math.Ceiling(maximumSaving);
            if (bucketCount < 1)
            {
                bucketCount = 1;
            }

            // Create buckets
            List<SavingRecord>[] buckets = new List<SavingRecord>[bucketCount + 1];
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = new List<SavingRecord>();
            }

            // Distribute each saving into its bucket (rounded).
            for (int i = 0; i < savings.Count; i++)
            {
                double value = savings[i].SavingValue;
                int bucketIndex = (int)Math.Round(value);
                if (bucketIndex < 0)
                {
                    bucketIndex = 0;
                }
                if (bucketIndex > bucketCount)
                {
                    bucketIndex = bucketCount;
                }
                buckets[bucketIndex].Add(savings[i]);
            }

            // Collect from highest bucket to lowest for descending order.
            List<SavingRecord> sortedList = new List<SavingRecord>(savings.Count);
            for (int idx = bucketCount; idx >= 0; idx--)
            {
                List<SavingRecord> currentBucket = buckets[idx];
                for (int b = 0; b < currentBucket.Count; b++)
                {
                    sortedList.Add(currentBucket[b]);
                }
            }

            return sortedList;
        }

        /// <summary>
        /// Union-Find structure that keeps track of route heads/tails so Clarke-Wright merges
        /// (tail->head or head->tail) happen in O(1) time. 
        /// </summary>
        private sealed class DeliveryRouteUnionFind
        {
            private int[] parentSet;
            private int[] setRank;

            // headOfRoute[leader] is the first node's index in that route
            // tailOfRoute[leader] is the last node's index in that route
            private int[] headOfRoute;
            private int[] tailOfRoute;

            // nextNodeIndex[i] points to the next node in i's chain.
            // -1 means this node is a tail.
            private int[] nextNodeIndex;

            public DeliveryRouteUnionFind(int size)
            {
                parentSet = new int[size];
                setRank = new int[size];
                headOfRoute = new int[size];
                tailOfRoute = new int[size];
                nextNodeIndex = new int[size];

                // Initially, each delivery is its own route: itself as head & tail.
                for (int i = 0; i < size; i++)
                {
                    parentSet[i] = i;
                    setRank[i] = 0;
                    headOfRoute[i] = i;
                    tailOfRoute[i] = i;
                    nextNodeIndex[i] = -1;
                }
            }

            /// <summary>
            /// Attempts to merge two routes if one index is a tail and the other is a head.
            /// </summary>
            public void TryMergeRoutes(int firstDeliveryIndex, int secondDeliveryIndex)
            {
                int leaderA = FindLeader(firstDeliveryIndex);
                int leaderB = FindLeader(secondDeliveryIndex);

                // If both deliveries are already in the same route, ignore.
                if (leaderA == leaderB)
                {
                    return;
                }

                // Check if "first" is a tail and "second" is a head
                bool canMergeForward = (tailOfRoute[leaderA] == firstDeliveryIndex
                                     && headOfRoute[leaderB] == secondDeliveryIndex);

                // Check if "second" is a tail and "first" is a head
                bool canMergeReverse = (tailOfRoute[leaderB] == secondDeliveryIndex
                                     && headOfRoute[leaderA] == firstDeliveryIndex);

                if (canMergeForward)
                {
                    // Link A's tail -> B's head
                    nextNodeIndex[firstDeliveryIndex] = secondDeliveryIndex;
                    Union(leaderA, leaderB);

                    int newLeader = FindLeader(firstDeliveryIndex);
                    headOfRoute[newLeader] = headOfRoute[leaderA];
                    tailOfRoute[newLeader] = tailOfRoute[leaderB];
                }
                else if (canMergeReverse)
                {
                    // Link B's tail -> A's head
                    nextNodeIndex[secondDeliveryIndex] = firstDeliveryIndex;
                    Union(leaderB, leaderA);

                    int newLeader = FindLeader(secondDeliveryIndex);
                    headOfRoute[newLeader] = headOfRoute[leaderB];
                    tailOfRoute[newLeader] = tailOfRoute[leaderA];
                }
            }

            /// <summary>
            /// After all merges, builds the final route by traversing each disjoint chain
            /// from head->tail. Multiple chains, if any, are appended in arbitrary order.
            /// </summary>
            public List<Delivery> BuildFinalRoute(Delivery[] deliveriesArray)
            {
                List<Delivery> mergedRoute = new List<Delivery>();
                HashSet<int> visitedLeaders = new HashSet<int>();

                for (int i = 0; i < deliveriesArray.Length; i++)
                {
                    int currentLeader = FindLeader(i);
                    if (!visitedLeaders.Contains(currentLeader))
                    {
                        visitedLeaders.Add(currentLeader);

                        // Traverse from head to tail for this route.
                        int head = headOfRoute[currentLeader];
                        int currentNode = head;
                        while (currentNode != -1)
                        {
                            mergedRoute.Add(deliveriesArray[currentNode]);
                            currentNode = nextNodeIndex[currentNode];
                        }
                    }
                }

                return mergedRoute;
            }

            /// <summary>
            /// Finds the representative leader with path compression for efficiency.
            /// </summary>
            private int FindLeader(int node)
            {
                if (parentSet[node] != node)
                {
                    parentSet[node] = FindLeader(parentSet[node]);
                }
                return parentSet[node];
            }

            /// <summary>
            /// Standard union-by-rank to keep sets shallow.
            /// </summary>
            private void Union(int rootA, int rootB)
            {
                if (setRank[rootA] < setRank[rootB])
                {
                    parentSet[rootA] = rootB;
                }
                else if (setRank[rootA] > setRank[rootB])
                {
                    parentSet[rootB] = rootA;
                }
                else
                {
                    parentSet[rootB] = rootA;
                    setRank[rootA]++;
                }
            }
        }

        /// <summary>
        /// Simple record for storing a pair of deliveries (by index) and their computed saving value.
        /// </summary>
        private record SavingRecord(int FirstDeliveryIndex, int SecondDeliveryIndex, double SavingValue);
    }
}
