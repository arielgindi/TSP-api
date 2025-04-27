using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

// Quick note: This is the partial class file for TSP tasks.
// We removed the partition/binary-search methods and moved them
// to a new partial file called TspAlgorithm.PartitionBinarySearch.cs

public static partial class TspAlgorithm
{
    private static readonly Random randomGenerator = new Random();

    public static readonly Delivery Depot = new Delivery(0, 0, 0);

    public delegate void ProgressReporter(long itemsProcessed);

    public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
    {
        if (count <= 0) return new List<Delivery>();

        List<Delivery> deliveries = new List<Delivery>(count);
        HashSet<(int, int)> usedCoordinates = new HashSet<(int, int)>
        {
            (Depot.X, Depot.Y)
        };

        long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;
        if (count > availableSlots) count = (int)Math.Max(0, availableSlots);

        for (int deliveryIndex = 1; deliveryIndex <= count; deliveryIndex++)
        {
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

    // NOTE: FindBestPartitionBinarySearch and related helpers are now
    // in TspAlgorithm.PartitionBinarySearch.cs.

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

    public static double CalculateEuclideanDistance(Delivery firstDelivery, Delivery secondDelivery)
    {
        if (firstDelivery == null || secondDelivery == null) return 0;
        double deltaX = secondDelivery.X - firstDelivery.X;
        double deltaY = secondDelivery.Y - firstDelivery.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }
}
