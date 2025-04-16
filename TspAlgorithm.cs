using System;
using System.Collections.Generic;
using System.Linq;

namespace RouteOptimizationApi
{
    public static class TspAlgorithm
    {
        private static readonly Random randomGenerator = new Random();
        public static readonly Delivery Depot = new Delivery(0, 0, 0);
        public delegate void ProgressReporter(long itemsProcessed);

        // --- Helper Class for Clarke-Wright Savings ---
        private class Saving
        {
            public int DeliveryIdI { get; }
            public int DeliveryIdJ { get; }
            public double Value { get; }

            public Saving(int i, int j, double value)
            {
                DeliveryIdI = i;
                DeliveryIdJ = j;
                Value = value;
            }
        }
        // --- End Helper Class ---


        public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
        {
            if (count <= 0) return new List<Delivery>();

            List<Delivery> deliveries = new List<Delivery>(count);
            HashSet<(int, int)> usedCoords = new HashSet<(int, int)> { (Depot.X, Depot.Y) };
            long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;

            if (count > availableSlots)
            {
                Console.WriteLine($"Warning: Requested {count} deliveries, but only {availableSlots} unique coordinates available. Generating {availableSlots}.");
                count = (int)Math.Max(0, availableSlots);
            }

            for (int i = 1; i <= count; i++)
            {
                int randomX;
                int randomY;
                int attempts = 0;
                while (true)
                {
                    randomX = randomGenerator.Next(minCoord, maxCoord + 1);
                    randomY = randomGenerator.Next(minCoord, maxCoord + 1);
                    attempts++;
                    if (attempts > Constants.MaxAttempts)
                    {
                        Console.WriteLine($"Error: Could not find unique coordinates after {Constants.MaxAttempts} attempts. Range might be too small or exhausted. Returning {deliveries.Count} deliveries.");
                        return deliveries; // Return what we have generated so far
                    }
                    if (usedCoords.Add((randomX, randomY))) break;
                }
                deliveries.Add(new Delivery(i, randomX, randomY));
            }
            return deliveries;
        }

        public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
        {
            List<Delivery> pending = new List<Delivery>(allDeliveries ?? Enumerable.Empty<Delivery>());
            List<Delivery> route = new List<Delivery>((allDeliveries?.Count ?? 0) + 2) { Depot };
            Delivery current = Depot;

            if (!pending.Any()) // Handle case with no deliveries
            {
                route.Add(Depot);
                return route;
            }

            while (pending.Count > 0)
            {
                double bestDistSq = double.MaxValue;
                Delivery? nextDelivery = null;
                int nextIndex = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    Delivery candidate = pending[i];
                    double distSq = CalculateEuclideanDistanceSquared(current, candidate);
                    // Use Epsilon comparison for finding the *minimum* distance
                    if (distSq < bestDistSq - Constants.Epsilon)
                    {
                        bestDistSq = distSq;
                        nextDelivery = candidate;
                        nextIndex = i;
                    }
                    // Tie-breaking (optional but can help consistency): prefer lower ID if distances are equal
                    else if (Math.Abs(distSq - bestDistSq) < Constants.Epsilon && candidate.Id < (nextDelivery?.Id ?? int.MaxValue))
                    {
                        nextDelivery = candidate;
                        nextIndex = i;
                    }
                }

                if (nextDelivery != null && nextIndex >= 0)
                {
                    route.Add(nextDelivery);
                    current = nextDelivery;
                    // Efficient removal for List<T> when index is known
                    pending.RemoveAt(nextIndex);
                }
                else if (pending.Count > 0) // Should not happen if logic is correct
                {
                    Console.WriteLine($"Warning: NN could not find next point despite {pending.Count} pending deliveries. Breaking loop.");
                    break; // Avoid infinite loop
                }
            }
            route.Add(Depot);
            return route;
        }

        // --- REMOVED ConstructGreedyInsertionRoute ---

        // --- ADDED ConstructClarkeWrightRoute ---
        public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> allDeliveries)
        {
            if (allDeliveries == null || allDeliveries.Count == 0)
            {
                return new List<Delivery> { Depot, Depot };
            }
            if (allDeliveries.Count == 1)
            {
                return new List<Delivery> { Depot, allDeliveries[0], Depot };
            }

            int N = allDeliveries.Count;
            Dictionary<int, Delivery> deliveryMap = allDeliveries.ToDictionary(d => d.Id);

            // Precompute distances for efficiency
            var distCache = new Dictionary<(int, int), double>();
            Func<Delivery, Delivery, double> GetDist = (d1, d2) =>
            {
                int id1 = d1.Id;
                int id2 = d2.Id;
                // Ensure consistent key order (e.g., lower ID first)
                if (id1 > id2) (id1, id2) = (id2, id1);

                if (!distCache.TryGetValue((id1, id2), out double dist))
                {
                    dist = CalculateEuclideanDistance(d1, d2);
                    distCache[(id1, id2)] = dist;
                }
                return dist;
            };

            // 1. Calculate Savings
            List<Saving> savingsList = new List<Saving>();
            for (int i = 0; i < N; i++)
            {
                Delivery deliveryI = allDeliveries[i];
                double distDepotI = GetDist(Depot, deliveryI);
                for (int j = i + 1; j < N; j++)
                {
                    Delivery deliveryJ = allDeliveries[j];
                    double distDepotJ = GetDist(Depot, deliveryJ);
                    double distIJ = GetDist(deliveryI, deliveryJ);

                    double savingValue = distDepotI + distDepotJ - distIJ;
                    if (savingValue > Constants.Epsilon) // Only consider positive savings
                    {
                        savingsList.Add(new Saving(deliveryI.Id, deliveryJ.Id, savingValue));
                    }
                }
            }

            // 2. Sort Savings
            savingsList.Sort((s1, s2) => s2.Value.CompareTo(s1.Value)); // Descending order

            // 3. Initialize Routes & Tracking Structures
            // Maps representative ID -> list of delivery IDs in the route
            Dictionary<int, List<int>> representativeToRoute = new Dictionary<int, List<int>>();
            // Maps delivery ID -> representative ID of its current route
            Dictionary<int, int> deliveryToRepresentative = new Dictionary<int, int>();
            // Maps representative ID -> ID of the first delivery after depot
            Dictionary<int, int> firstDeliveryInRoute = new Dictionary<int, int>();
            // Maps representative ID -> ID of the last delivery before depot
            Dictionary<int, int> lastDeliveryInRoute = new Dictionary<int, int>();

            foreach (Delivery d in allDeliveries)
            {
                int id = d.Id;
                representativeToRoute[id] = new List<int> { id };
                deliveryToRepresentative[id] = id;
                firstDeliveryInRoute[id] = id;
                lastDeliveryInRoute[id] = id;
            }

            // 4. Merge Routes
            foreach (Saving saving in savingsList)
            {
                int idI = saving.DeliveryIdI;
                int idJ = saving.DeliveryIdJ;

                // Find representatives and check if they are already in the same route
                int repI = deliveryToRepresentative[idI];
                int repJ = deliveryToRepresentative[idJ];

                if (repI == repJ) continue; // Already merged

                // Check Clarke-Wright conditions:
                // - Is I the last delivery of its route?
                // - Is J the first delivery of its route?
                bool canMerge = lastDeliveryInRoute.TryGetValue(repI, out int lastI) && lastI == idI &&
                                firstDeliveryInRoute.TryGetValue(repJ, out int firstJ) && firstJ == idJ;

                if (canMerge)
                {
                    // Merge route J into route I
                    List<int> routeI = representativeToRoute[repI];
                    List<int> routeJ = representativeToRoute[repJ];

                    routeI.AddRange(routeJ); // Append J's deliveries to I's

                    // Update representative mapping for all nodes formerly in route J
                    foreach (int deliveryIdInJ in routeJ)
                    {
                        deliveryToRepresentative[deliveryIdInJ] = repI;
                    }

                    // Update the last delivery for the merged route (repI)
                    lastDeliveryInRoute[repI] = lastDeliveryInRoute[repJ];

                    // Remove the now-merged route J's info
                    representativeToRoute.Remove(repJ);
                    firstDeliveryInRoute.Remove(repJ);
                    lastDeliveryInRoute.Remove(repJ);
                }
                // Optional: Check the reverse merge (if J is last of its route and I is first of its route)
                // Standard CWS usually only checks one direction (i last, j first), but checking reverse is possible.
                 else
                 {
                       bool canMergeReverse = lastDeliveryInRoute.TryGetValue(repJ, out int lastJ) && lastJ == idJ &&
                                            firstDeliveryInRoute.TryGetValue(repI, out int firstI) && firstI == idI;
                       if (canMergeReverse)
                       {
                           // Merge route I into route J
                            List<int> routeI = representativeToRoute[repI];
                            List<int> routeJ = representativeToRoute[repJ];

                            routeJ.AddRange(routeI); // Append I's deliveries to J's

                            // Update representative mapping for all nodes formerly in route I
                            foreach (int deliveryIdInI in routeI)
                            {
                                deliveryToRepresentative[deliveryIdInI] = repJ;
                            }

                            // Update the last delivery for the merged route (repJ)
                            lastDeliveryInRoute[repJ] = lastDeliveryInRoute[repI];

                            // Remove the now-merged route I's info
                            representativeToRoute.Remove(repI);
                            firstDeliveryInRoute.Remove(repI);
                            lastDeliveryInRoute.Remove(repI);
                       }
                 }
            }

            // 5. Construct Final Route(s)
            // In the basic TSP case, we expect one final route. If multiple exist, it might
            // indicate a disconnected graph or a scenario where merging wasn't possible.
            // We'll just concatenate them if needed, although this isn't standard CWS.
            List<Delivery> finalRoute = new List<Delivery> { Depot };
            if (representativeToRoute.Count > 1)
            {
                 Console.WriteLine($"Warning: Clarke-Wright resulted in {representativeToRoute.Count} separate routes. Concatenating them.");
                 // Order of concatenation might matter, but for simplicity:
                 foreach(var routeList in representativeToRoute.Values)
                 {
                     foreach(int deliveryId in routeList)
                     {
                         finalRoute.Add(deliveryMap[deliveryId]);
                     }
                 }
            }
            else if (representativeToRoute.Count == 1)
            {
                 List<int> finalRouteIds = representativeToRoute.Values.First();
                 foreach (int deliveryId in finalRouteIds)
                 {
                     finalRoute.Add(deliveryMap[deliveryId]);
                 }
            }
            // Else (Count is 0): This should only happen if N=0, handled earlier.

            finalRoute.Add(Depot);
            return finalRoute;
        }
        // --- End ADDED ConstructClarkeWrightRoute ---


        public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
        {
            // Check if the route is too short for 2-Opt (needs at least 4 points: Depot -> A -> B -> Depot)
            if (initialRoute == null || initialRoute.Count < 4) return initialRoute ?? new List<Delivery>();

            List<Delivery> currentRoute = new List<Delivery>(initialRoute);
            bool improvementFound = true;
            int maxIterations = 10000; // Safety break for potential complex scenarios
            int currentIteration = 0;

            // Precompute distances between adjacent nodes initially? Might not save much as they change.

            while (improvementFound && currentIteration < maxIterations)
            {
                improvementFound = false;
                currentIteration++;
                 // Iterate through all possible pairs of edges to swap (i, i+1) and (j, j+1)
                 // Note: The loop bounds ensure we don't select adjacent edges or edges involving the depots directly in the swap logic
                 // i ranges from 0 to Count-4 (inclusive) -> selects first edge (route[i], route[i+1])
                 // j ranges from i+2 to Count-2 (inclusive) -> selects second edge (route[j], route[j+1])
                for (int i = 0; i < currentRoute.Count - 3; i++) // Start of the first edge
                {
                    for (int j = i + 2; j < currentRoute.Count - 1; j++) // Start of the second edge
                    {
                        Delivery pointA = currentRoute[i];     // Start of first edge
                        Delivery pointB = currentRoute[i + 1]; // End of first edge
                        Delivery pointC = currentRoute[j];     // Start of second edge
                        Delivery pointD = currentRoute[j + 1]; // End of second edge

                        // Calculate distances: current edge pair (A-B + C-D) vs swapped edge pair (A-C + B-D)
                        // No need to recalculate full route distance, just compare the change.
                        double currentCost = CalculateEuclideanDistance(pointA, pointB) + CalculateEuclideanDistance(pointC, pointD);
                        double swappedCost = CalculateEuclideanDistance(pointA, pointC) + CalculateEuclideanDistance(pointB, pointD);

                        // If swapping reduces distance significantly (more than the threshold)
                        if (swappedCost < currentCost - Constants.ImprovementThreshold)
                        {
                            // Perform the 2-Opt swap: Reverse the segment between i+1 and j (inclusive)
                            ReverseSegment(currentRoute, i + 1, j);
                            improvementFound = true;
                            // Optional: Could break inner loops and restart outer loop ('first improvement')
                            // but 'best improvement' (completing loops) is also common. Sticking with best.
                        }
                    }
                }
            }
             if (currentIteration >= maxIterations) {
                 Console.WriteLine("Warning: 2-Opt reached max iterations safety break.");
             }
            return currentRoute;
        }

        // Helper to reverse a sub-list in place
        private static void ReverseSegment(List<Delivery> route, int startIndex, int endIndex)
        {
            while (startIndex < endIndex)
            {
                // Swap elements using tuple deconstruction
                (route[startIndex], route[endIndex]) = (route[endIndex], route[startIndex]);
                startIndex++;
                endIndex--;
            }
        }

        public static void FindBestPartitionBinarySearch(
            List<Delivery> optimizedRoute,
            int numberOfDrivers,
            ProgressReporter? reporter,
            out int[]? bestCuts,
            out double minMakespan
            )
        {
            minMakespan = double.MaxValue;
            bestCuts = null;
            long iterations = 0;

            // Handle cases with no route or too few points
            if (optimizedRoute == null || optimizedRoute.Count < 2)
            {
                minMakespan = 0;
                bestCuts = Array.Empty<int>();
                reporter?.Invoke(iterations);
                return;
            }

            int deliveryCount = optimizedRoute.Count - 2; // Number of actual deliveries

            // Handle cases with no deliveries
            if (deliveryCount <= 0)
            {
                minMakespan = 0;
                bestCuts = Array.Empty<int>(); // No cuts needed
                 reporter?.Invoke(iterations);
                return;
            }

             // Handle invalid driver count (though should be caught earlier)
            if (numberOfDrivers <= 0) {
                 // Treat as 1 driver case, makespan is the full route
                 minMakespan = ComputeTotalRouteDistance(optimizedRoute);
                 bestCuts = Array.Empty<int>();
                 reporter?.Invoke(iterations);
                 return;
             }

            // Handle trivial case: 1 driver gets the whole route
            if (numberOfDrivers == 1)
            {
                // Calculate distance for the single driver: Depot -> delivery 1 -> ... -> delivery N -> Depot
                minMakespan = ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount);
                bestCuts = Array.Empty<int>(); // No cuts needed for 1 driver
                reporter?.Invoke(iterations);
                return;
            }

            // If #drivers >= #deliveries, each driver gets at most one delivery
            if (numberOfDrivers >= deliveryCount)
            {
                minMakespan = 0; // Will be updated below
                List<int> cuts = new List<int>();
                double maxSingleRoute = 0;
                for (int i = 1; i <= deliveryCount; i++)
                {
                    double dist = ComputeSubRouteDistanceWithDepot(optimizedRoute, i, i);
                    maxSingleRoute = Math.Max(maxSingleRoute, dist);
                    if (i < deliveryCount) // Add a cut after each delivery (except the last)
                    {
                        cuts.Add(i);
                    }
                }
                 // Pad cuts if drivers > deliveries
                int lastCut = deliveryCount;
                while(cuts.Count < numberOfDrivers - 1)
                {
                    cuts.Add(lastCut); // Add cuts at the very end (effectively empty routes)
                }

                minMakespan = maxSingleRoute;
                bestCuts = cuts.Take(numberOfDrivers - 1).ToArray(); // Ensure correct number of cuts
                reporter?.Invoke(iterations);
                return;
            }

            // --- Binary Search Setup ---
            // Lower bound: Max distance of any single delivery route (Depot -> i -> Depot)
            double lowerBoundMakespan = 0.0;
            for (int i = 1; i <= deliveryCount; i++) {
                 lowerBoundMakespan = Math.Max(lowerBoundMakespan, ComputeSubRouteDistanceWithDepot(optimizedRoute, i, i));
            }

            // Upper bound: Total distance of the full route (worst case for makespan if 1 driver)
            double upperBoundMakespan = ComputeTotalRouteDistance(optimizedRoute); // Or ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount)

            double optimalMakespanFound = upperBoundMakespan; // Keep track of the best makespan found for a feasible partition
            int[]? currentBestCuts = null; // Stores the cuts for the optimalMakespanFound

             // Safety break and precision control
            int binarySearchIterations = 0;
            int maxBinarySearchIterations = (int)Math.Log2(upperBoundMakespan / Constants.Epsilon) + deliveryCount + 100; // Estimate max iterations needed

            // --- Binary Search Loop ---
            while (lowerBoundMakespan <= upperBoundMakespan && binarySearchIterations < maxBinarySearchIterations)
            {
                iterations++; // Count iterations for reporting
                 binarySearchIterations++;
                double candidateMakespan = lowerBoundMakespan + (upperBoundMakespan - lowerBoundMakespan) / 2.0;

                // Check if a partition is possible with this candidate makespan
                if (IsPartitionFeasible(optimizedRoute, numberOfDrivers, candidateMakespan, out int[]? potentialCuts))
                {
                    // Feasible: This makespan *might* be achievable. Try for an even lower makespan.
                    optimalMakespanFound = candidateMakespan; // Record this feasible makespan
                    currentBestCuts = potentialCuts;          // Record the cuts that achieved it
                    upperBoundMakespan = candidateMakespan - Constants.Epsilon; // Search in the lower half
                }
                else
                {
                    // Not feasible: The target makespan is too low. Need to increase it.
                    lowerBoundMakespan = candidateMakespan + Constants.Epsilon; // Search in the upper half
                }
                 reporter?.Invoke(iterations); // Report progress
            }
             if (binarySearchIterations >= maxBinarySearchIterations) {
                 Console.WriteLine("Warning: Binary search for partition reached max iterations.");
             }

            // --- Finalize Results ---
            minMakespan = optimalMakespanFound; // The best feasible makespan found

             // If bestCuts is still null (e.g., only the initial upperBound worked),
             // run IsPartitionFeasible one last time with the final makespan to get the cuts.
             if (currentBestCuts == null)
             {
                 IsPartitionFeasible(optimizedRoute, numberOfDrivers, minMakespan, out currentBestCuts);
                 // If still null here, something is wrong, default to empty array
                 if (currentBestCuts == null) {
                    Console.WriteLine("Error: Could not determine cuts even for the final makespan.");
                    currentBestCuts = Array.Empty<int>();
                 }
             }

            bestCuts = currentBestCuts;


             // Ensure the correct number of cuts if deliveries < drivers was handled earlier,
             // but double-check for cases where binary search ends with fewer cuts than expected.
            if (bestCuts.Length < numberOfDrivers - 1 && deliveryCount > 0)
            {
                 // This might happen if the optimal solution naturally uses fewer drivers.
                 // Pad with cuts at the end.
                 List<int> paddedCuts = bestCuts.ToList();
                 int lastDeliveryIndex = deliveryCount; // The index of the last actual delivery
                 while (paddedCuts.Count < numberOfDrivers - 1)
                 {
                     paddedCuts.Add(lastDeliveryIndex);
                 }
                 bestCuts = paddedCuts.Distinct().OrderBy(c => c).ToArray(); // Ensure distinct and sorted
            }

              reporter?.Invoke(iterations); // Final report
        }


        // Checks if the route can be partitioned into at most `maxDrivers` segments,
        // where each segment's distance (including travel to/from depot) <= maxAllowedMakespan.
        // Uses a greedy approach.
        private static bool IsPartitionFeasible(List<Delivery> route, int maxDrivers, double maxAllowedMakespan, out int[]? cuts)
        {
            int n = route.Count - 2; // Number of actual deliveries
            cuts = null;
            List<int> cutIndices = new List<int>();

            if (n <= 0) // No deliveries
            {
                cuts = Array.Empty<int>();
                return true; // Feasible with 0 distance
            }

            int driversUsed = 1;
            int currentSegmentStartIndex = 1; // Start index (1-based) of the current driver's deliveries

            for (int i = 1; i <= n; i++) // Iterate through each delivery (index i in the route list)
            {
                // Calculate the cost if the current driver takes deliveries from currentSegmentStartIndex up to i
                double segmentCost = ComputeSubRouteDistanceWithDepot(route, currentSegmentStartIndex, i);

                // If adding delivery `i` exceeds the makespan limit for the current driver
                if (segmentCost > maxAllowedMakespan + Constants.Epsilon)
                {
                    // The previous delivery (i-1) must be the end of the previous driver's route.
                    if (i == currentSegmentStartIndex)
                    {
                        // Even the first delivery of this segment exceeds the limit. Impossible.
                        cuts = null; // Invalid partition
                        return false;
                    }

                    // Add a cut after delivery i-1
                    cutIndices.Add(i - 1);
                    driversUsed++;

                    // Start a new segment for the next driver, beginning with delivery i
                    currentSegmentStartIndex = i;

                    // Check if the new segment (just delivery i) is itself too long
                    double singleDeliveryCost = ComputeSubRouteDistanceWithDepot(route, i, i);
                     if (singleDeliveryCost > maxAllowedMakespan + Constants.Epsilon) {
                         cuts = null; // Invalid partition
                         return false;
                     }

                    // Check if we've exceeded the maximum number of drivers allowed
                    if (driversUsed > maxDrivers)
                    {
                        cuts = null; // Invalid partition
                        return false;
                    }
                }
                // Else: Delivery `i` can be added to the current driver's route without exceeding the limit. Continue.
            }

            // If we finish the loop, the partition is feasible.
            cuts = cutIndices.ToArray();
            return true; // driversUsed <= maxDrivers is implicitly true if we didn't return false earlier
        }


        // Calculate distance for a sub-route segment INCLUDING travel from/to Depot
        // Indices are 1-based relative to the deliveries in the optimizedRoute list
        // e.g., startIndexInRoute=1 means the first delivery, endIndexInRoute=3 means the third delivery.
        public static double ComputeSubRouteDistanceWithDepot(List<Delivery> route, int startIndexInRoute, int endIndexInRoute)
        {
            // Basic validation
             if (route == null || route.Count < 2) return 0; // No route
             if (startIndexInRoute > endIndexInRoute) return 0; // Empty segment requested
             if (startIndexInRoute < 1 || endIndexInRoute >= route.Count - 1) // Indices must point to actual deliveries
             {
                // This case can happen if startIndex > N, meaning an empty route for a later driver.
                if(startIndexInRoute > route.Count - 2) return 0;

                // Other out-of-bounds scenarios are errors.
                 Console.WriteLine($"Warning: Invalid indices for ComputeSubRouteDistanceWithDepot. Start: {startIndexInRoute}, End: {endIndexInRoute}, Route Deliveries: {route.Count-2}");
                 // Allow calculation if endIndex is exactly the last delivery index.
                 if(endIndexInRoute >= route.Count - 1 && startIndexInRoute < route.Count -1 ) {
                     // Adjust end index if it goes beyond last delivery
                     endIndexInRoute = route.Count - 2;
                 } else {
                    return 0; // Return 0 for fundamentally invalid ranges
                 }
             }


            double totalDistance = 0;
            // Distance from Depot to the first delivery of the segment
            totalDistance += CalculateEuclideanDistance(Depot, route[startIndexInRoute]);

            // Distance between deliveries within the segment
            for (int i = startIndexInRoute; i < endIndexInRoute; i++)
            {
                // We are guaranteed i+1 is within bounds because endIndexInRoute < route.Count - 1
                totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
            }

            // Distance from the last delivery of the segment back to Depot
            totalDistance += CalculateEuclideanDistance(route[endIndexInRoute], Depot);

            return totalDistance;
        }

        // Calculates the total length of a full route (including first and last Depot segments)
        public static double ComputeTotalRouteDistance(List<Delivery> route)
        {
            if (route == null || route.Count < 2) return 0;
            double totalDistance = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                // Calculate distance between route[i] and route[i+1]
                 totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
            }
            return totalDistance;
        }

        // Calculates Euclidean distance between two Delivery points
        public static double CalculateEuclideanDistance(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0; // Should not happen with valid data
            // Can skip Sqrt for comparisons if needed, but route distances need actual distance.
            double dx = d2.X - d1.X;
            double dy = d2.Y - d1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Calculates squared Euclidean distance (faster for comparisons)
        private static double CalculateEuclideanDistanceSquared(Delivery d1, Delivery d2)
        {
             if (d1 == null || d2 == null) return 0;
             double dx = d2.X - d1.X;
             double dy = d2.Y - d1.Y;
             return dx * dx + dy * dy;
        }
    }
}