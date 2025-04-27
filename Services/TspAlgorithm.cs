using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

// Quick note: This is the partial class file for TSP tasks.
// We removed the partition/binary-search methods and moved them
// to a new partial file called TspAlgorithm.PartitionBinarySearch.cs

public static partial class TspAlgorithm
{
    private static readonly Random randomGenerator = new();

    public static readonly Delivery Depot = new(0, 0, 0);

    public delegate void ProgressReporter(long itemsProcessed);

    /// <summary>
    /// Generates a list of random delivery points within the specified coordinate range,
    /// ensuring that each delivery point is unique and does not overlap with the depot.
    /// </summary>
    /// <param name="count">Number of deliveries to generate.</param>
    /// <param name="minCoord">Minimum allowed coordinate value.</param>
    /// <param name="maxCoord">Maximum allowed coordinate value.</param>
    /// <returns>A list of randomly generated unique deliveries.</returns>
    public static List<Delivery> GenerateRandomDeliveries(int count, int minCoord, int maxCoord)
    {
        // No deliveries requested, return empty list immediately
        if (count <= 0)
            return [];

        // Initialize deliveries list and used coordinates set (depot included)
        List<Delivery> deliveries = new(count);
        HashSet<(int, int)> usedCoordinates = [(Depot.X, Depot.Y)];

        // Calculate total available unique positions in the given range (excluding depot)
        long availableSlots = ((long)maxCoord - minCoord + 1) * ((long)maxCoord - minCoord + 1) - 1;

        // Ensure we don't exceed available unique slots
        count = (int)Math.Min(count, availableSlots);

        for (int deliveryIndex = 1; deliveryIndex <= count; deliveryIndex++)
        {
            bool addedSuccessfully = false;

            // Try generating unique coordinates until we reach the maximum number of attempts
            for (int attempts = 0; attempts < Constants.MaxAttempts; attempts++)
            {
                int randomX = randomGenerator.Next(minCoord, maxCoord + 1);
                int randomY = randomGenerator.Next(minCoord, maxCoord + 1);

                // Try adding the random coordinates; if successful, create the delivery
                if (usedCoordinates.Add((randomX, randomY)))
                {
                    deliveries.Add(new Delivery(deliveryIndex, randomX, randomY));
                    addedSuccessfully = true;
                    break;
                }
            }

            // If we reach maximum attempts without adding successfully, stop generating further deliveries
            if (!addedSuccessfully)
                break;
        }

        return deliveries;
    }


    /// <summary>
    /// Optimizes the given delivery route using the 2-Opt algorithm.
    /// Continuously improves the route by swapping segments until no further improvements are possible,
    /// or the maximum number of iterations is reached.
    /// </summary>
    /// <param name="initialRoute">The initial delivery route to be optimized.</param>
    /// <returns>The optimized delivery route.</returns>
    public static List<Delivery> OptimizeRouteUsing2Opt(List<Delivery> initialRoute)
    {
        // No optimization needed for null or short routes
        if (initialRoute is null || initialRoute.Count < 4)
            return initialRoute ?? [];

        List<Delivery> optimizedRoute = [.. initialRoute];
        bool isImproved;
        int iteration = 0;

        do
        {
            isImproved = false;
            iteration++;

            for (int i = 0; i < optimizedRoute.Count - 3; i++)
            {
                for (int j = i + 2; j < optimizedRoute.Count - 1; j++)
                {
                    Delivery nodeA = optimizedRoute[i];
                    Delivery nodeB = optimizedRoute[i + 1];
                    Delivery nodeC = optimizedRoute[j];
                    Delivery nodeD = optimizedRoute[j + 1];

                    double originalDistance = CalculateEuclideanDistance(nodeA, nodeB)
                                            + CalculateEuclideanDistance(nodeC, nodeD);

                    double swappedDistance = CalculateEuclideanDistance(nodeA, nodeC)
                                           + CalculateEuclideanDistance(nodeB, nodeD);

                    // Check if swapping results in a shorter route segment
                    if (swappedDistance < originalDistance - Constants.ImprovementThreshold)
                    {
                        ReverseSegment(optimizedRoute, i + 1, j);
                        isImproved = true;
                    }
                }
            }

        } while (isImproved && iteration < Constants.Max2OptIterations);

        return optimizedRoute;
    }

    /// <summary>
    /// Reverses the segment of deliveries in the route between the specified indices.
    /// </summary>
    /// <param name="route">The delivery route to modify.</param>
    /// <param name="start">Start index of the segment to reverse.</param>
    /// <param name="end">End index of the segment to reverse.</param>
    private static void ReverseSegment(List<Delivery> route, int start, int end)
    {
        while (start < end)
        {
            (route[start], route[end]) = (route[end], route[start]);
            start++;
            end--;
        }
    }


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
