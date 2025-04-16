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

        public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
        {
            if (count <= 0) return new List<Delivery>();
            List<Delivery> deliveries = new List<Delivery>(count);
            HashSet<(int, int)> usedCoords = new HashSet<(int, int)> { (Depot.X, Depot.Y) };
            long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;
            if (count > availableSlots)
            {
                Console.WriteLine("Warning: Requested " + count + " deliveries, but only " + availableSlots + " unique coordinates available. Generating " + availableSlots + ".");
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
                        Console.WriteLine("Error: Could not find unique coordinates after " + Constants.MaxAttempts + " attempts. Range might be too small or exhausted. Returning " + deliveries.Count + " deliveries.");
                        return deliveries;
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
            while (pending.Count > 0)
            {
                double bestDistSq = double.MaxValue;
                Delivery? nextDelivery = null;
                int nextIndex = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    Delivery candidate = pending[i];
                    double distSq = CalculateEuclideanDistanceSquared(current, candidate);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        nextDelivery = candidate;
                        nextIndex = i;
                    }
                }
                if (nextDelivery != null && nextIndex >= 0)
                {
                    route.Add(nextDelivery);
                    current = nextDelivery;
                    pending.RemoveAt(nextIndex);
                }
                else if (pending.Count > 0)
                {
                    Console.WriteLine("Warning: NN could not find next point despite pending deliveries.");
                    break;
                }
            }
            route.Add(Depot);
            return route;
        }

        public static List<Delivery> ConstructGreedyInsertionRoute(List<Delivery> allDeliveries)
        {
            if (allDeliveries == null || allDeliveries.Count == 0)
            {
                List<Delivery> emptyRoute = new List<Delivery> { Depot, Depot };
                return emptyRoute;
            }
            List<Delivery> pending = new List<Delivery>(allDeliveries);
            List<Delivery> route = new List<Delivery>(allDeliveries.Count + 2);
            Delivery firstDelivery = pending[0];
            route.Add(Depot);
            route.Add(firstDelivery);
            route.Add(Depot);
            pending.RemoveAt(0);
            while (pending.Count > 0)
            {
                Delivery? bestCandidateToInsert = null;
                int bestInsertionIndex = -1;
                double minIncrease = double.MaxValue;
                foreach (Delivery candidate in pending)
                {
                    for (int i = 0; i < route.Count - 1; i++)
                    {
                        Delivery currentRouteNode = route[i];
                        Delivery nextRouteNode = route[i + 1];
                        double originalEdgeCost = CalculateEuclideanDistance(currentRouteNode, nextRouteNode);
                        double costWithInsertion = CalculateEuclideanDistance(currentRouteNode, candidate) + CalculateEuclideanDistance(candidate, nextRouteNode);
                        double costIncrease = costWithInsertion - originalEdgeCost;
                        if (costIncrease < minIncrease)
                        {
                            minIncrease = costIncrease;
                            bestCandidateToInsert = candidate;
                            bestInsertionIndex = i + 1;
                        }
                    }
                }
                if (bestCandidateToInsert != null && bestInsertionIndex != -1)
                {
                    route.Insert(bestInsertionIndex, bestCandidateToInsert);
                    bool removed = pending.Remove(bestCandidateToInsert);
                    if (!removed)
                    {
                        Console.WriteLine("Warning: Failed to remove inserted candidate from pending list in GI.");
                        pending.RemoveAll(d => d.Id == bestCandidateToInsert.Id);
                    }
                }
                else if (pending.Count > 0)
                {
                    Console.WriteLine("Warning: GI could not find insertion point despite pending deliveries.");
                    break;
                }
            }
            return route;
        }

        public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
        {
            if (initialRoute == null || initialRoute.Count < 4) return initialRoute ?? new List<Delivery>();
            List<Delivery> currentRoute = new List<Delivery>(initialRoute);
            bool improvementFound = true;
            while (improvementFound)
            {
                improvementFound = false;
                for (int i = 0; i < currentRoute.Count - 3; i++)
                {
                    for (int j = i + 2; j < currentRoute.Count - 1; j++)
                    {
                        Delivery pointA = currentRoute[i];
                        Delivery pointB = currentRoute[i + 1];
                        Delivery pointC = currentRoute[j];
                        Delivery pointD = currentRoute[j + 1];
                        double currentCost = CalculateEuclideanDistance(pointA, pointB) + CalculateEuclideanDistance(pointC, pointD);
                        double swappedCost = CalculateEuclideanDistance(pointA, pointC) + CalculateEuclideanDistance(pointB, pointD);
                        if (swappedCost < currentCost - Constants.ImprovementThreshold)
                        {
                            ReverseSegment(currentRoute, i + 1, j);
                            improvementFound = true;
                        }
                    }
                }
            }
            return currentRoute;
        }

        private static void ReverseSegment(List<Delivery> route, int startIndex, int endIndex)
        {
            while (startIndex < endIndex)
            {
                Delivery temp = route[startIndex];
                route[startIndex] = route[endIndex];
                route[endIndex] = temp;
                startIndex++;
                endIndex--;
            }
        }

        public static void FindBestPartition(
            List<Delivery> optimizedRoute,
            int numberOfDrivers,
            ProgressReporter? reporter,
            out int[]? bestCuts,
            out double minMakespan,
            out long combosChecked
        )
        {
            minMakespan = double.MaxValue;
            combosChecked = 0;
            bestCuts = null;
            if (optimizedRoute == null || optimizedRoute.Count < 2)
            {
                minMakespan = 0;
                return;
            }
            int cutsNeeded = numberOfDrivers - 1;
            int deliveryCount = optimizedRoute.Count - 2;
            if (deliveryCount <= 0)
            {
                bestCuts = Array.Empty<int>();
                minMakespan = 0;
                combosChecked = 1;
                if (reporter != null) reporter.Invoke(combosChecked);
                return;
            }
            if (numberOfDrivers <= 1)
            {
                bestCuts = Array.Empty<int>();
                minMakespan = ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount);
                combosChecked = 1;
                if (reporter != null) reporter.Invoke(combosChecked);
                return;
            }
            bestCuts = new int[cutsNeeded];
            if (deliveryCount < numberOfDrivers)
            {
                for (int i = 0; i < cutsNeeded; i++)
                {
                    bestCuts[i] = Math.Min(i + 1, deliveryCount);
                }
                minMakespan = CalculateMakespanForCuts(optimizedRoute, numberOfDrivers, bestCuts);
                combosChecked = 1;
                if (reporter != null) reporter.Invoke(combosChecked);
                return;
            }
            int[] currentCutCombination = new int[cutsNeeded];
            GenerateCutCombinationsRecursive(
                optimizedRoute,
                numberOfDrivers,
                1,
                deliveryCount,
                0,
                currentCutCombination,
                ref minMakespan,
                ref bestCuts,
                ref combosChecked,
                reporter
            );
            if (reporter != null) reporter.Invoke(combosChecked);
        }

        private static void GenerateCutCombinationsRecursive(
            List<Delivery> route,
            int drivers,
            int searchStartIndex,
            int maxDeliveryIndex,
            int currentCutDepth,
            int[] currentCuts,
            ref double bestMakespanSoFar,
            ref int[]? bestCutCombination,
            ref long combinationsCounter,
            ProgressReporter? reporter
        )
        {
            int cutsNeeded = drivers - 1;
            if (currentCutDepth == cutsNeeded)
            {
                combinationsCounter++;
                double currentMakespan = CalculateMakespanForCuts(route, drivers, currentCuts);
                if (currentMakespan < bestMakespanSoFar)
                {
                    bestMakespanSoFar = currentMakespan;
                    if (bestCutCombination != null)
                    {
                        Array.Copy(currentCuts, bestCutCombination, cutsNeeded);
                    }
                    else
                    {
                        Console.WriteLine("Error: bestCutCombination was null inside GenerateCutCombinationsRecursive.");
                    }
                }
                if (combinationsCounter % 1000 == 0 && reporter != null) reporter.Invoke(combinationsCounter);
                return;
            }
            int previousCutIndex = currentCutDepth == 0 ? 0 : currentCuts[currentCutDepth - 1];
            int maxPossibleCutIndex = maxDeliveryIndex - (cutsNeeded - (currentCutDepth + 1));
            for (int i = Math.Max(searchStartIndex, previousCutIndex + 1); i <= maxPossibleCutIndex; i++)
            {
                currentCuts[currentCutDepth] = i;
                GenerateCutCombinationsRecursive(
                    route,
                    drivers,
                    i + 1,
                    maxDeliveryIndex,
                    currentCutDepth + 1,
                    currentCuts,
                    ref bestMakespanSoFar,
                    ref bestCutCombination,
                    ref combinationsCounter,
                    reporter
                );
            }
        }

        private static double CalculateMakespanForCuts(List<Delivery> route, int drivers, int[] cuts)
        {
            double maxDistance = 0;
            int deliveryCount = route.Count - 2;
            int cutCount = cuts.Length;
            int startDeliveryIndexInRoute = 0;
            for (int driverIndex = 0; driverIndex < drivers; driverIndex++)
            {
                int endDeliveryIndexInRoute = driverIndex < cutCount ? cuts[driverIndex] : deliveryCount;
                double currentSegmentDistance = ComputeSubRouteDistanceWithDepot(route, startDeliveryIndexInRoute + 1, endDeliveryIndexInRoute);
                if (currentSegmentDistance > maxDistance) maxDistance = currentSegmentDistance;
                startDeliveryIndexInRoute = endDeliveryIndexInRoute;
            }
            return maxDistance;
        }

        public static double ComputeSubRouteDistanceWithDepot(List<Delivery> route, int startIndexInRoute, int endIndexInRoute)
        {
            if (startIndexInRoute > endIndexInRoute || startIndexInRoute < 1 || endIndexInRoute >= route.Count - 1)
            {
                return 0;
            }
            if (startIndexInRoute >= route.Count || endIndexInRoute >= route.Count)
            {
                Console.WriteLine("Warning: Index out of bounds in ComputeSubRouteDistanceWithDepot. Start: " + startIndexInRoute + ", End: " + endIndexInRoute + ", Route Count: " + route.Count);
                return 0;
            }
            double totalDistance = CalculateEuclideanDistance(Depot, route[startIndexInRoute]);
            for (int i = startIndexInRoute; i < endIndexInRoute; i++)
            {
                if (i + 1 < route.Count)
                {
                    totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
                }
            }
            totalDistance += CalculateEuclideanDistance(route[endIndexInRoute], Depot);
            return totalDistance;
        }

        public static double ComputeTotalRouteDistance(List<Delivery> route)
        {
            if (route == null || route.Count < 2) return 0;
            double totalDistance = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                if (i + 1 < route.Count)
                {
                    totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
                }
            }
            return totalDistance;
        }

        public static double CalculateEuclideanDistance(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0;
            if (d1.X == d2.X && d1.Y == d2.Y) return 0;
            double dx = d2.X - d1.X;
            double dy = d2.Y - d1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double CalculateEuclideanDistanceSquared(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0;
            if (d1.X == d2.X && d2.Y == d1.Y) return 0;
            double dx = d2.X - d1.X;
            double dy = d2.Y - d1.Y;
            return dx * dx + dy * dy;
        }
    }
}
