using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// Provides various TSP-related tasks such as generating deliveries, 2-Opt optimization, and route partitioning.
/// This is the main partial class file containing common methods shared across TSP operations.
/// </summary>
public static partial class TspAlgorithm
{
    private static readonly Random randomGenerator = new Random();

    /// <summary>
    /// Special delivery that represents the depot (start/end point).
    /// </summary>
    public static readonly Delivery Depot = new Delivery(0, 0, 0);

    /// <summary>
    /// Used to report progress when searching for the best route partition.
    /// </summary>
    public delegate void ProgressReporter(long itemsProcessed);


    /// <summary>
    /// Generates a list of random deliveries within a specified coordinate range, ensuring no duplicates.
    /// </summary>
    public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
    {
        if (count <= 0) return new List<Delivery>();

        List<Delivery> deliveries = new List<Delivery>(count);
        HashSet<(int, int)> usedCoordinates = new HashSet<(int, int)> { (Depot.X, Depot.Y) };

        // availableSlots is the total number of unique coordinate pairs 
        // in the inclusive range [minCoord, maxCoord] minus 1 for the depot's own coordinates.
        // This ensures we don't exceed the coordinate space capacity when generating deliveries.
        long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;

        if (count > availableSlots) count = (int)Math.Max(0, availableSlots);

        for (int deliveryIndex = 1; deliveryIndex <= count; deliveryIndex++)
        {
            // We track how many attempts we've made to find a new, unique (x, y) coordinate.
            // If we exceed Constants.MaxAttempts, we stop to avoid an infinite loop 
            // in cases where the space is too crowded to find unique coordinates.
            int attempts = 0;

            while (true)
            {
                int randomX = randomGenerator.Next(minCoord, maxCoord + 1);
                int randomY = randomGenerator.Next(minCoord, maxCoord + 1);
                attempts++;
                if (attempts > Constants.MaxAttempts) return deliveries;
                if (usedCoordinates.Add((randomX, randomY)))
                {
                    deliveries.Add(new Delivery(deliveryIndex, randomX, randomY));
                    break;
                }
            }
        }
        return deliveries;
    }

    /// <summary>
    /// Applies the 2-Opt algorithm to reduce crossing edges and improve the route distance.
    /// </summary>
    public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
    {
        if (initialRoute == null || initialRoute.Count < 4)
        {
            return initialRoute ?? new List<Delivery>();
        }

        List<Delivery> currentRoute = new List<Delivery>(initialRoute);
        bool improvement = true;
        int maxIterations = Constants.Max2OptIterations;
        int iterationCount = 0;

        while (improvement && iterationCount < maxIterations)
        {
            improvement = false;
            iterationCount++;

            for (int primaryIndex = 0; primaryIndex < currentRoute.Count - 3; primaryIndex++)
            {
                for (int secondaryIndex = primaryIndex + 2; secondaryIndex < currentRoute.Count - 1; secondaryIndex++)
                {
                    Delivery deliveryA = currentRoute[primaryIndex];
                    Delivery deliveryB = currentRoute[primaryIndex + 1];
                    Delivery deliveryC = currentRoute[secondaryIndex];
                    Delivery deliveryD = currentRoute[secondaryIndex + 1];

                    double currentCost = CalculateEuclideanDistance(deliveryA, deliveryB)
                        + CalculateEuclideanDistance(deliveryC, deliveryD);

                    double updatedCost = CalculateEuclideanDistance(deliveryA, deliveryC)
                        + CalculateEuclideanDistance(deliveryB, deliveryD);

                    bool cheaperSwap = updatedCost < currentCost - Constants.ImprovementThreshold;
                    if (cheaperSwap)
                    {
                        ReverseSegment(currentRoute, primaryIndex + 1, secondaryIndex);
                        improvement = true;
                    }
                }
            }
        }
        return currentRoute;
    }

    /// <summary>
    /// Reverses the segment of the route from start to end indices.
    /// </summary>
    private static void ReverseSegment(List<Delivery> route, int startIndex, int endIndex)
    {
        while (startIndex < endIndex)
        {
            Delivery tempDelivery = route[startIndex];
            route[startIndex] = route[endIndex];
            route[endIndex] = tempDelivery;
            startIndex++;
            endIndex--;
        }
    }

    /// <summary>
    /// Splits an optimized route among multiple drivers by searching for the minimal makespan using a binary search approach.
    /// </summary>
    public static void FindBestPartitionBinarySearch(
        List<Delivery> optimizedRoute,
        int numberOfDrivers,
        ProgressReporter reporter,
        out int[] bestCuts,
        out double minMakespan
    )
    {
        minMakespan = double.MaxValue;
        bestCuts = Array.Empty<int>();
        long iterations = 0;

        if (optimizedRoute == null || optimizedRoute.Count < 2)
        {
            minMakespan = 0;
            reporter?.Invoke(iterations);
            return;
        }

        int deliveryCount = optimizedRoute.Count - 2;
        if (deliveryCount <= 0)
        {
            minMakespan = 0;
            reporter?.Invoke(iterations);
            return;
        }

        if (numberOfDrivers <= 0)
        {
            minMakespan = ComputeTotalRouteDistance(optimizedRoute);
            reporter?.Invoke(iterations);
            return;
        }

        if (numberOfDrivers == 1)
        {
            minMakespan = ComputeSubRouteDistanceWithDepot(optimizedRoute, 1, deliveryCount);
            reporter?.Invoke(iterations);
            return;
        }

        if (numberOfDrivers >= deliveryCount)
        {
            AssignOneDeliveryPerDriver(optimizedRoute, numberOfDrivers, deliveryCount, out bestCuts, out minMakespan);
            reporter?.Invoke(iterations);
            return;
        }

        double lowerBound = GetMaxSingleDeliveryCost(optimizedRoute, deliveryCount);
        double upperBound = ComputeTotalRouteDistance(optimizedRoute);
        double optimal = upperBound;
        int[] currentBestCuts = Array.Empty<int>();
        int maxBinIters = (int)Math.Log2(upperBound / Constants.Epsilon) + deliveryCount + 100;

        while (lowerBound <= upperBound && iterations < maxBinIters)
        {
            iterations++;
            double midValue = lowerBound + (upperBound - lowerBound) / 2.0;

            bool feasible = IsPartitionFeasible(optimizedRoute, numberOfDrivers, midValue, out int[] potentialCuts);
            if (feasible)
            {
                optimal = midValue;
                currentBestCuts = potentialCuts;
                upperBound = midValue - Constants.Epsilon;
            }
            else
            {
                lowerBound = midValue + Constants.Epsilon;
            }
            reporter?.Invoke(iterations);
        }

        minMakespan = optimal;
        if (currentBestCuts.Length == 0)
        {
            bool finalCheck = IsPartitionFeasible(optimizedRoute, numberOfDrivers, minMakespan, out currentBestCuts);
            if (!finalCheck) currentBestCuts = Array.Empty<int>();
        }
        bestCuts = currentBestCuts;

        if (bestCuts.Length < numberOfDrivers - 1 && deliveryCount > 0)
        {
            bestCuts = PadCutIndices(bestCuts, numberOfDrivers, deliveryCount);
        }
        reporter?.Invoke(iterations);
    }

    /// <summary>
    /// Calculates the distance of a sub-route, including travel from the depot to the first delivery and from the last delivery back to the depot.
    /// </summary>
    public static double ComputeSubRouteDistanceWithDepot(List<Delivery> route, int startIndex, int endIndex)
    {
        if (route == null || route.Count < 2 || startIndex > endIndex) return 0;

        if (startIndex < 1 || endIndex >= route.Count - 1)
        {
            if (endIndex >= route.Count - 1 && startIndex < route.Count - 1) endIndex = route.Count - 2;
            else return 0;
        }

        double totalDistance = CalculateEuclideanDistance(Depot, route[startIndex]);
        for (int i = startIndex; i < endIndex; i++)
        {
            totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
        }
        totalDistance += CalculateEuclideanDistance(route[endIndex], Depot);

        return totalDistance;
    }

    /// <summary>
    /// Computes the total distance of a full route by summing distances between consecutive points.
    /// </summary>
    public static double ComputeTotalRouteDistance(List<Delivery> route)
    {
        if (route == null || route.Count < 2) return 0;
        double totalDistance = 0;

        for (int i = 0; i < route.Count - 1; i++)
        {
            totalDistance += CalculateEuclideanDistance(route[i], route[i + 1]);
        }
        return totalDistance;
    }

    /// <summary>
    /// Calculates Euclidean distance between two deliveries.
    /// </summary>
    public static double CalculateEuclideanDistance(Delivery firstDelivery, Delivery secondDelivery)
    {
        if (firstDelivery == null || secondDelivery == null) return 0;
        double deltaX = secondDelivery.X - firstDelivery.X;
        double deltaY = secondDelivery.Y - firstDelivery.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }


    /// <summary>
    /// Assigns one delivery per driver when there are more or equal drivers than deliveries, or nearly so.
    /// </summary>
    private static void AssignOneDeliveryPerDriver(
        List<Delivery> optimizedRoute,
        int numberOfDrivers,
        int deliveryCount,
        out int[] bestCuts,
        out double minMakespan
    )
    {
        minMakespan = 0;
        double maxSingle = 0;
        List<int> cuts = new List<int>();

        for (int i = 1; i <= deliveryCount; i++)
        {
            double subDistance = ComputeSubRouteDistanceWithDepot(optimizedRoute, i, i);
            if (subDistance > maxSingle) maxSingle = subDistance;
            if (i < deliveryCount) cuts.Add(i);
        }

        minMakespan = maxSingle;
        while (cuts.Count < numberOfDrivers - 1)
        {
            cuts.Add(deliveryCount);
        }
        bestCuts = cuts.Take(numberOfDrivers - 1).ToArray();
    }

    /// <summary>
    /// Pads the cut indices so there are enough cuts for each driver, using distinct cut positions if possible.
    /// </summary>
    private static int[] PadCutIndices(int[] currentCuts, int numberOfDrivers, int deliveryCount)
    {
        List<int> paddedList = new List<int>(currentCuts);

        while (paddedList.Count < numberOfDrivers - 1)
        {
            paddedList.Add(deliveryCount);
        }
        return paddedList.Distinct().OrderBy(c => c).ToArray();
    }

    /// <summary>
    /// Checks if a route can be split among drivers without exceeding a specified max allowed distance per driver.
    /// </summary>
    private static bool IsPartitionFeasible(List<Delivery> route, int maxDrivers, double maxAllowedMakespan, out int[] cuts)
    {
        cuts = Array.Empty<int>();
        if (route == null || route.Count < 2) return true;

        int totalDeliveries = route.Count - 2;
        int usedDriverCount = 1;
        int start = 1;
        List<int> subRouteCuts = new List<int>();

        for (int i = 1; i <= totalDeliveries; i++)
        {
            double segmentDistance = ComputeSubRouteDistanceWithDepot(route, start, i);
            if (segmentDistance > maxAllowedMakespan + Constants.Epsilon)
            {
                if (i == start) return false;
                subRouteCuts.Add(i - 1);
                usedDriverCount++;
                start = i;

                double singleCost = ComputeSubRouteDistanceWithDepot(route, i, i);
                if (singleCost > maxAllowedMakespan + Constants.Epsilon) return false;
                if (usedDriverCount > maxDrivers) return false;
            }
        }
        cuts = subRouteCuts.ToArray();
        return true;
    }

    /// <summary>
    /// Finds the largest single-delivery sub-route cost to establish a lower bound for the binary search.
    /// </summary>
    private static double GetMaxSingleDeliveryCost(List<Delivery> route, int deliveryCount)
    {
        double lowerBound = 0.0;

        for (int i = 1; i <= deliveryCount; i++)
        {
            double singleDistance = ComputeSubRouteDistanceWithDepot(route, i, i);
            if (singleDistance > lowerBound)
            {
                lowerBound = singleDistance;
            }
        }
        return lowerBound;
    }
}
