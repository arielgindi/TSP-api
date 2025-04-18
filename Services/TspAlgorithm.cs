using System;
using System.Collections.Generic;
using System.Linq;
using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services
{
    public static class TspAlgorithm
    {
        private static readonly Random randomGenerator = new Random();
        public static readonly Delivery Depot = new Delivery(0, 0, 0);
        public delegate void ProgressReporter(long itemsProcessed);

        private record Saving(int DeliveryIdI, int DeliveryIdJ, double Value);

        public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
        {
            if (count <= 0) return new List<Delivery>();
            List<Delivery> deliveries = new List<Delivery>(count);
            HashSet<(int, int)> usedCoords = new HashSet<(int, int)> { (Depot.X, Depot.Y) };
            long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;
            if (count > availableSlots) count = (int)Math.Max(0, availableSlots);

            for (int i = 1; i <= count; i++)
            {
                int attempts = 0;
                while (true)
                {
                    int rx = randomGenerator.Next(minCoord, maxCoord + 1);
                    int ry = randomGenerator.Next(minCoord, maxCoord + 1);
                    attempts++;
                    if (attempts > Constants.MaxAttempts) return deliveries;
                    if (usedCoords.Add((rx, ry)))
                    {
                        deliveries.Add(new Delivery(i, rx, ry));
                        break;
                    }
                }
            }
            return deliveries;
        }

        public static List<Delivery> ConstructNearestNeighborRoute(List<Delivery> allDeliveries)
        {
            List<Delivery> pending = new List<Delivery>(allDeliveries ?? Enumerable.Empty<Delivery>());
            List<Delivery> route = new List<Delivery> { Depot };
            Delivery current = Depot;
            if (!pending.Any())
            {
                route.Add(Depot);
                return route;
            }
            while (pending.Count > 0)
            {
                double bestDistSq = double.MaxValue;
                Delivery next = null;
                int nextIdx = -1;
                for (int i = 0; i < pending.Count; i++)
                {
                    double distSq = CalculateEuclideanDistanceSquared(current, pending[i]);
                    if (distSq < bestDistSq - Constants.Epsilon)
                    {
                        bestDistSq = distSq;
                        next = pending[i];
                        nextIdx = i;
                    }
                    else if (Math.Abs(distSq - bestDistSq) < Constants.Epsilon && pending[i].Id < (next == null ? int.MaxValue : next.Id))
                    {
                        next = pending[i];
                        nextIdx = i;
                    }
                }
                if (next != null && nextIdx >= 0)
                {
                    route.Add(next);
                    current = next;
                    pending.RemoveAt(nextIdx);
                }
                else
                {
                    break;
                }
            }
            route.Add(Depot);
            return route;
        }

        public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> allDeliveries)
        {
            if (allDeliveries == null || allDeliveries.Count == 0) return new List<Delivery> { Depot, Depot };
            if (allDeliveries.Count == 1) return new List<Delivery> { Depot, allDeliveries[0], Depot };

            Dictionary<(int, int), double> distCache = new Dictionary<(int, int), double>();
            double GetDist(Delivery d1, Delivery d2)
            {
                int id1 = d1.Id;
                int id2 = d2.Id;
                if (id1 > id2)
                {
                    int temp = id1;
                    id1 = id2;
                    id2 = temp;
                }
                if (!distCache.TryGetValue((id1, id2), out double d))
                {
                    d = CalculateEuclideanDistance(d1, d2);
                    distCache[(id1, id2)] = d;
                }
                return d;
            }

            List<Saving> savingsList = new List<Saving>();
            for (int i = 0; i < allDeliveries.Count; i++)
            {
                Delivery di = allDeliveries[i];
                double distDepotI = GetDist(Depot, di);
                for (int j = i + 1; j < allDeliveries.Count; j++)
                {
                    Delivery dj = allDeliveries[j];
                    double distDepotJ = GetDist(Depot, dj);
                    double distIJ = GetDist(di, dj);
                    double savingValue = distDepotI + distDepotJ - distIJ;
                    if (savingValue > Constants.Epsilon) savingsList.Add(new Saving(di.Id, dj.Id, savingValue));
                }
            }

            savingsList.Sort((s1, s2) => s2.Value.CompareTo(s1.Value));

            Dictionary<int, List<int>> representativeToRoute = new Dictionary<int, List<int>>();
            Dictionary<int, int> deliveryToRep = new Dictionary<int, int>();
            Dictionary<int, int> firstDelInRoute = new Dictionary<int, int>();
            Dictionary<int, int> lastDelInRoute = new Dictionary<int, int>();

            foreach (Delivery d in allDeliveries)
            {
                representativeToRoute[d.Id] = new List<int> { d.Id };
                deliveryToRep[d.Id] = d.Id;
                firstDelInRoute[d.Id] = d.Id;
                lastDelInRoute[d.Id] = d.Id;
            }

            foreach (Saving s in savingsList)
            {
                int repI = deliveryToRep[s.DeliveryIdI];
                int repJ = deliveryToRep[s.DeliveryIdJ];
                if (repI == repJ) continue;
                bool canMerge = lastDelInRoute[repI] == s.DeliveryIdI && firstDelInRoute[repJ] == s.DeliveryIdJ;
                if (canMerge)
                {
                    List<int> routeI = representativeToRoute[repI];
                    List<int> routeJ = representativeToRoute[repJ];
                    routeI.AddRange(routeJ);
                    foreach (int dId in routeJ) deliveryToRep[dId] = repI;
                    lastDelInRoute[repI] = lastDelInRoute[repJ];
                    representativeToRoute.Remove(repJ);
                    firstDelInRoute.Remove(repJ);
                    lastDelInRoute.Remove(repJ);
                }
                else
                {
                    bool canMergeReverse = lastDelInRoute[repJ] == s.DeliveryIdJ && firstDelInRoute[repI] == s.DeliveryIdI;
                    if (canMergeReverse)
                    {
                        List<int> routeI = representativeToRoute[repI];
                        List<int> routeJ = representativeToRoute[repJ];
                        routeJ.AddRange(routeI);
                        foreach (int dId in routeI) deliveryToRep[dId] = repJ;
                        lastDelInRoute[repJ] = lastDelInRoute[repI];
                        representativeToRoute.Remove(repI);
                        firstDelInRoute.Remove(repI);
                        lastDelInRoute.Remove(repI);
                    }
                }
            }

            List<Delivery> finalRoute = new List<Delivery> { Depot };
            if (representativeToRoute.Count > 1)
            {
                foreach (List<int> kvp in representativeToRoute.Values)
                {
                    foreach (int dId in kvp)
                    {
                        finalRoute.Add(allDeliveries.First(d => d.Id == dId));
                    }
                }
            }
            else
            {
                List<int> singleList = representativeToRoute.Values.First();
                foreach (int dId in singleList)
                {
                    finalRoute.Add(allDeliveries.First(d => d.Id == dId));
                }
            }
            finalRoute.Add(Depot);
            return finalRoute;
        }

        public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
        {
            if (initialRoute == null || initialRoute.Count < 4) return initialRoute ?? new List<Delivery>();
            List<Delivery> currentRoute = new List<Delivery>(initialRoute);
            bool improvement = true;
            int maxIterations = 10000;
            int iter = 0;

            while (improvement && iter < maxIterations)
            {
                improvement = false;
                iter++;
                for (int i = 0; i < currentRoute.Count - 3; i++)
                {
                    for (int j = i + 2; j < currentRoute.Count - 1; j++)
                    {
                        Delivery A = currentRoute[i];
                        Delivery B = currentRoute[i + 1];
                        Delivery C = currentRoute[j];
                        Delivery D = currentRoute[j + 1];
                        double curCost = CalculateEuclideanDistance(A, B) + CalculateEuclideanDistance(C, D);
                        double newCost = CalculateEuclideanDistance(A, C) + CalculateEuclideanDistance(B, D);
                        if (newCost < curCost - Constants.ImprovementThreshold)
                        {
                            ReverseSegment(currentRoute, i + 1, j);
                            improvement = true;
                        }
                    }
                }
            }
            return currentRoute;
        }

        static void ReverseSegment(List<Delivery> route, int start, int end)
        {
            while (start < end)
            {
                Delivery temp = route[start];
                route[start] = route[end];
                route[end] = temp;
                start++;
                end--;
            }
        }

        public static void FindBestPartitionBinarySearch(
            List<Delivery> optimizedRoute,
            int numberOfDrivers,
            ProgressReporter reporter,
            out int[] bestCuts,
            out double minMakespan
        )
        {
            minMakespan = double.MaxValue;
            bestCuts = null;
            long iterations = 0;
            if (optimizedRoute == null || optimizedRoute.Count < 2)
            {
                minMakespan = 0;
                bestCuts = Array.Empty<int>();
                reporter?.Invoke(iterations);
                return;
            }
            int deliveryCount = optimizedRoute.Count - 2;
            if (deliveryCount <= 0)
            {
                minMakespan = 0;
                bestCuts = Array.Empty<int>();
                reporter?.Invoke(iterations);
                return;
            }
            if (numberOfDrivers <= 0)
            {
                minMakespan = ComputeTotalRouteDistance(optimizedRoute);
                bestCuts = Array.Empty<int>();
                reporter?.Invoke(iterations);
                return;
            }
            if (numberOfDrivers == 1)
            {
                minMakespan = ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount);
                bestCuts = Array.Empty<int>();
                reporter?.Invoke(iterations);
                return;
            }
            if (numberOfDrivers >= deliveryCount)
            {
                minMakespan = 0;
                double maxSingle = 0;
                List<int> cuts = new List<int>();
                for (int i = 1; i <= deliveryCount; i++)
                {
                    double dist = ComputeSubRouteDistanceWithDepot(optimizedRoute, i, i);
                    if (dist > maxSingle) maxSingle = dist;
                    if (i < deliveryCount) cuts.Add(i);
                }
                minMakespan = maxSingle;
                while (cuts.Count < numberOfDrivers - 1) cuts.Add(deliveryCount);
                bestCuts = cuts.Take(numberOfDrivers - 1).ToArray();
                reporter?.Invoke(iterations);
                return;
            }
            double lowerBound = 0.0;
            for (int i = 1; i <= deliveryCount; i++)
            {
                double singleDist = ComputeSubRouteDistanceWithDepot(optimizedRoute, i, i);
                if (singleDist > lowerBound) lowerBound = singleDist;
            }
            double upperBound = ComputeTotalRouteDistance(optimizedRoute);
            double optimal = upperBound;
            int[] currentBestCuts = null;
            int maxBinIters = (int)Math.Log2(upperBound / Constants.Epsilon) + deliveryCount + 100;

            while (lowerBound <= upperBound && iterations < maxBinIters)
            {
                iterations++;
                double mid = lowerBound + (upperBound - lowerBound) / 2.0;
                if (IsPartitionFeasible(optimizedRoute, numberOfDrivers, mid, out int[] potentialCuts))
                {
                    optimal = mid;
                    currentBestCuts = potentialCuts;
                    upperBound = mid - Constants.Epsilon;
                }
                else
                {
                    lowerBound = mid + Constants.Epsilon;
                }
                reporter?.Invoke(iterations);
            }
            minMakespan = optimal;
            if (currentBestCuts == null)
            {
                IsPartitionFeasible(optimizedRoute, numberOfDrivers, minMakespan, out currentBestCuts);
                if (currentBestCuts == null) currentBestCuts = Array.Empty<int>();
            }
            bestCuts = currentBestCuts;
            if (bestCuts.Length < numberOfDrivers - 1 && deliveryCount > 0)
            {
                List<int> padded = new List<int>(bestCuts);
                while (padded.Count < numberOfDrivers - 1) padded.Add(deliveryCount);
                bestCuts = padded.Distinct().OrderBy(c => c).ToArray();
            }
            reporter?.Invoke(iterations);
        }

        static bool IsPartitionFeasible(List<Delivery> route, int maxDrivers, double maxAllowedMakespan, out int[] cuts)
        {
            cuts = null;
            if (route == null || route.Count < 2)
            {
                cuts = Array.Empty<int>();
                return true;
            }
            int n = route.Count - 2;
            int driversUsed = 1;
            int start = 1;
            List<int> cutIndices = new List<int>();

            for (int i = 1; i <= n; i++)
            {
                double cost = ComputeSubRouteDistanceWithDepot(route, start, i);
                if (cost > maxAllowedMakespan + Constants.Epsilon)
                {
                    if (i == start) return false;
                    cutIndices.Add(i - 1);
                    driversUsed++;
                    start = i;
                    double singleCost = ComputeSubRouteDistanceWithDepot(route, i, i);
                    if (singleCost > maxAllowedMakespan + Constants.Epsilon) return false;
                    if (driversUsed > maxDrivers) return false;
                }
            }
            cuts = cutIndices.ToArray();
            return true;
        }

        public static double ComputeSubRouteDistanceWithDepot(List<Delivery> route, int start, int end)
        {
            if (route == null || route.Count < 2 || start > end) return 0;
            if (start < 1 || end >= route.Count - 1)
            {
                if (end >= route.Count - 1 && start < route.Count - 1) end = route.Count - 2;
                else return 0;
            }
            double total = CalculateEuclideanDistance(Depot, route[start]);
            for (int i = start; i < end; i++)
            {
                total += CalculateEuclideanDistance(route[i], route[i + 1]);
            }
            total += CalculateEuclideanDistance(route[end], Depot);
            return total;
        }

        public static double ComputeTotalRouteDistance(List<Delivery> route)
        {
            if (route == null || route.Count < 2) return 0;
            double total = 0;
            for (int i = 0; i < route.Count - 1; i++)
            {
                total += CalculateEuclideanDistance(route[i], route[i + 1]);
            }
            return total;
        }

        public static double CalculateEuclideanDistance(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0;
            double dx = d2.X - d1.X;
            double dy = d2.Y - d1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static double CalculateEuclideanDistanceSquared(Delivery d1, Delivery d2)
        {
            if (d1 == null || d2 == null) return 0;
            double dx = d2.X - d1.X;
            double dy = d2.Y - d1.Y;
            return dx * dx + dy * dy;
        }
    }
}
