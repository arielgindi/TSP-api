using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// Partial class file containing the Clarke-Wright Savings approach for TSP.
/// </summary>
public static partial class TspAlgorithm
{
    /// <summary>
    /// Builds a route using Clarke-Wright Savings. Each delivery starts alone,
    /// then we merge them by the highest "savings" until we get one full route.
    /// </summary>
    public static List<Delivery> ConstructClarkeWrightRoute(List<Delivery> deliveries)
    {
        // Handle a couple simple cases first.
        if (deliveries == null || deliveries.Count == 0)
        {
            List<Delivery> emptyList = new List<Delivery>();
            emptyList.Add(Depot);
            emptyList.Add(Depot);
            return emptyList;
        }

        if (deliveries.Count == 1)
        {
            List<Delivery> singleDeliveryRoute = new List<Delivery>();
            singleDeliveryRoute.Add(Depot);
            singleDeliveryRoute.Add(deliveries[0]);
            singleDeliveryRoute.Add(Depot);
            return singleDeliveryRoute;
        }

        // Grab all pairwise savings info so we know which merges help most.
        Dictionary<(int, int), double> distanceCache = new Dictionary<(int, int), double>();
        List<Saving> savingsList = ComputeAllSavings(deliveries, distanceCache);

        // Set up our new doubly linked structure for the routes.
        CwDoublyLinkedRoute routeManager = new CwDoublyLinkedRoute(deliveries);

        // Merge routes in order of biggest savings first.
        foreach (Saving saving in savingsList)
        {
            routeManager.TryMergeBySavings(saving);
        }

        // Convert merged structure into a final route of deliveries.
        List<Delivery> finalRoute = routeManager.BuildFinalRoute(deliveries);
        finalRoute.Add(Depot);
        return finalRoute;
    }

    /// <summary>
    /// Calculates distance "savings" for each pair of deliveries.
    /// Savings(a,b) = Dist(Depot,a) + Dist(Depot,b) - Dist(a,b).
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

            if (!distanceCache.TryGetValue((lowId, highId), out double dist))
            {
                dist = CalculateEuclideanDistance(first, second);
                distanceCache[(lowId, highId)] = dist;
            }
            return dist;
        }

        for (int i = 0; i < deliveries.Count; i++)
        {
            Delivery a = deliveries[i];
            double depotToA = GetDistance(Depot, a);

            for (int j = i + 1; j < deliveries.Count; j++)
            {
                Delivery b = deliveries[j];
                double depotToB = GetDistance(Depot, b);
                double aToB = GetDistance(a, b);

                double savingValue = depotToA + depotToB - aToB;
                if (savingValue > Constants.Epsilon)
                {
                    savingsList.Add(new Saving(a.Id, b.Id, savingValue));
                }
            }
        }

        savingsList.Sort((x, y) => y.Value.CompareTo(x.Value));
        return savingsList;
    }

    /// <summary>
    /// Replaces the old route graph logic with a doubly linked list manager.
    /// </summary>
    private sealed class CwDoublyLinkedRoute
    {
        // A quick lookup table so we can find each node by its delivery ID.
        private readonly Dictionary<int, DoublyLinkedDeliveryNode> nodeLookup;

        // The set of delivery IDs that are currently at the start of their chains.
        // If a node is in here, it means it's a "root" (has no previous node).
        private readonly HashSet<int> routeRoots;

        public CwDoublyLinkedRoute(List<Delivery> deliveries)
        {
            nodeLookup = new Dictionary<int, DoublyLinkedDeliveryNode>();
            routeRoots = new HashSet<int>();

            // Build a node for each delivery and treat each as its own route initially.
            foreach (Delivery d in deliveries)
            {
                DoublyLinkedDeliveryNode node = new DoublyLinkedDeliveryNode(d);
                nodeLookup[d.Id] = node;
                routeRoots.Add(d.Id);
            }
        }

        /// <summary>
        /// Tries merging two routes if the nodes fit the "ends" of their chains
        /// and are in different clusters.
        /// </summary>
        public void TryMergeBySavings(Saving saving)
        {
            int firstId = saving.FirstDeliveryId;
            int secondId = saving.SecondDeliveryId;

            // Find each node and see who leads their respective cluster.
            DoublyLinkedDeliveryNode nodeA = nodeLookup[firstId];
            DoublyLinkedDeliveryNode nodeB = nodeLookup[secondId];
            int leaderA = nodeA.ClusterLeaderId;
            int leaderB = nodeB.ClusterLeaderId;

            // Already in the same cluster? No need to merge.
            if (leaderA == leaderB)
            {
                return;
            }

            // Check if we can do a forward merge (A is tail, B is head).
            bool canForwardMerge = IsRouteEnd(nodeA, leaderA, true)
                && IsRouteEnd(nodeB, leaderB, false);

            // Check if we can do a reverse merge (B is tail, A is head).
            bool canReverseMerge = IsRouteEnd(nodeB, leaderB, true)
                && IsRouteEnd(nodeA, leaderA, false);

            if (canForwardMerge)
            {
                LinkRoutes(nodeA, nodeB, leaderA, leaderB);
            }
            else if (canReverseMerge)
            {
                LinkRoutes(nodeB, nodeA, leaderB, leaderA);
            }
        }

        /// <summary>
        /// Checks if a node is the "first" or "last" in its chain. 
        /// "isEnd=true" means tail (no Next). "isEnd=false" means head (no Previous).
        /// </summary>
        private bool IsRouteEnd(DoublyLinkedDeliveryNode node, int routeLeader, bool isEnd)
        {
            // If it's the tail, "Next" must be null and the leader must match.
            if (isEnd)
            {
                return (node.Next == null) && (node.ClusterLeaderId == routeLeader);
            }
            else
            {
                // If it's the head, "Previous" must be null and the leader must match.
                return (node.Previous == null) && (node.ClusterLeaderId == routeLeader);
            }
        }

        /// <summary>
        /// Merges one route's tail into another route's head, and updates cluster leaders.
        /// </summary>
        private void LinkRoutes(
            DoublyLinkedDeliveryNode tailNode,
            DoublyLinkedDeliveryNode headNode,
            int leaderA,
            int leaderB
        )
        {
            // Link them: A's tail -> B's head.
            tailNode.Next = headNode;
            headNode.Previous = tailNode;

            // Update cluster for everyone in B's cluster to match A's leader.
            UpdateClusterLeaders(leaderB, leaderA);

            // If the head node was considered a root, it's no longer a root now.
            if (routeRoots.Contains(headNode.Delivery.Id))
            {
                routeRoots.Remove(headNode.Delivery.Id);
            }
        }

        /// <summary>
        /// Updates everyone who had oldLeader to have newLeader instead.
        /// This collapses two clusters into a single one.
        /// </summary>
        private void UpdateClusterLeaders(int oldLeader, int newLeader)
        {
            // Just loop all known nodes and update.
            List<int> allIds = nodeLookup.Keys.ToList();
            for (int i = 0; i < allIds.Count; i++)
            {
                int currentId = allIds[i];
                DoublyLinkedDeliveryNode node = nodeLookup[currentId];
                if (node.ClusterLeaderId == oldLeader)
                {
                    node.ClusterLeaderId = newLeader;
                }
            }
        }

        /// <summary>
        /// Converts the linked-list chains into a final list of deliveries, 
        /// appending them in order.
        /// </summary>
        public List<Delivery> BuildFinalRoute(List<Delivery> allDeliveries)
        {
            List<Delivery> finalRoute = new List<Delivery>();
            finalRoute.Add(Depot);

            // Go through each root and traverse forward.
            // If we have more than one root, we append them in some order.
            foreach (int rootId in routeRoots)
            {
                DoublyLinkedDeliveryNode currentNode = nodeLookup[rootId];
                while (currentNode != null)
                {
                    // Grab the matching Delivery from the node itself.
                    finalRoute.Add(currentNode.Delivery);
                    currentNode = currentNode.Next;
                }
            }

            return finalRoute;
        }
    }
}
