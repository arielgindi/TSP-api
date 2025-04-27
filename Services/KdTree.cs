using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// A 2D KD-Tree specialized for Delivery points (excluding the depot where Id=0).
/// It supports:
///   - Building the tree in O(n log n) time via in-place median selection (Quickselect).
///   - Averaging O(log n) for repeated nearest-neighbor searches.
///   - Logical "removal" by marking nodes as visited and decrementing counts,
///     rather than restructuring the entire tree.
/// </summary>
public sealed class KdTree
{
    /// <summary>
    /// Represents a node in the KD-Tree, holding a Delivery point, 
    /// its axis (0 = X, 1 = Y), visited-state, subtree count of unvisited nodes,
    /// and links to child nodes and parent.
    /// </summary>
    private sealed class Node
    {
        public Delivery Point;
        public int Axis;               // 0 => split by X, 1 => split by Y
        public bool Visited;           // True once taken by PopNearest

        public int UnvisitedCount;     // How many unvisited nodes in this subtree
        public Node? Left;            // Left child subtree
        public Node? Right;           // Right child subtree
        public Node? Parent;          // Link to parent node

        public Node(Delivery point, int axis, Node? parent)
        {
            Point = point;
            Axis = axis;
            Parent = parent;
            UnvisitedCount = 1; // This node counts itself
        }
    }

    private Node? _rootNode;
    private int _totalUnvisited;

    /// <summary>
    /// Builds a KD-Tree from a collection of deliveries, filtering out any with Id=0 (depot).
    /// </summary>
    /// <param name="deliveries">A collection of Delivery objects (possibly including depot).</param>
    /// <exception cref="ArgumentNullException">Thrown if 'deliveries' is null.</exception>
    public KdTree(IEnumerable<Delivery>? deliveries)
    {
        if (deliveries is null)
            throw new ArgumentNullException(nameof(deliveries));

        // Collect only valid deliveries (ignore depot)
        List<Delivery> validPoints = [];
        foreach (Delivery d in deliveries)
        {
            if (d is not null && d.Id != 0)
            {
                validPoints.Add(d);
            }
        }

        // Track how many unvisited deliveries we have in total
        _totalUnvisited = validPoints.Count;
        if (_totalUnvisited == 0)
            return; // Nothing to build if there are no valid deliveries

        // Convert to array for in-place building
        Delivery[] arr = validPoints.ToArray();

        // Build the KD-Tree recursively
        _rootNode = BuildNode(arr, 0, arr.Length - 1, depth: 0, parent: null);
    }

    /// <summary>
    /// Indicates whether there are any unvisited deliveries left in the tree.
    /// </summary>
    public bool HasUnvisited => _totalUnvisited > 0;

    /// <summary>
    /// Finds, returns, and marks the closest unvisited delivery point to the specified query point.
    /// Updates visitation state and subtree counts accordingly.
    /// </summary>
    /// <param name="query">The delivery point used to search for the closest neighbor.</param>
    /// <returns>
    /// The nearest unvisited <see cref="Delivery"/> point, or null if all points have been visited.
    /// </returns>
    public Delivery? PopNearest(Delivery query)
    {
        // No deliveries left to visit
        if (_rootNode is null || _totalUnvisited == 0)
            return null;

        Node? closestNode = null;
        double shortestDistSquared = double.MaxValue;

        // Search recursively through KD-Tree to find the closest unvisited node
        SearchNearest(_rootNode, query, ref closestNode, ref shortestDistSquared);

        // If no closest node found, all nodes are already visited
        if (closestNode is null)
            return null;

        // Mark closest node as visited and decrement unvisited count up the tree
        closestNode.Visited = true;
        UpdateUnvisitedCountUpward(closestNode);

        // Decrement the global unvisited count
        _totalUnvisited--;

        return closestNode.Point;
    }

    /// <summary>
    /// Helper method that updates the UnvisitedCount for the node and its ancestors.
    /// </summary>
    /// <param name="node">The starting node to update.</param>
    private void UpdateUnvisitedCountUpward(Node node)
    {
        Node? currentNode = node;
        while (currentNode is not null)
        {
            currentNode.UnvisitedCount--;
            currentNode = currentNode.Parent;
        }
    }


    #region Building the Tree

    /// <summary>
    /// Recursively builds the KD-Tree by selecting a median in the current dimension.
    /// </summary>
    /// <param name="arr">The array of deliveries being partitioned.</param>
    /// <param name="left">Left boundary in the array slice.</param>
    /// <param name="right">Right boundary in the array slice.</param>
    /// <param name="depth">Current tree depth to decide which axis to split (X or Y).</param>
    /// <param name="parent">The parent node to attach the newly built node to.</param>
    /// <returns>A newly created Node or null if invalid slice.</returns>
    private Node? BuildNode(Delivery[] arr, int left, int right, int depth, Node? parent)
    {
        if (left > right)
            return null;

        int axis = depth % 2; // Even depths => X, odd => Y
        int mid = (left + right) / 2;

        // Move median element to 'mid' index with respect to the chosen axis
        SelectMedian(arr, left, right, mid, axis);

        // Create the parent node from the median
        Node node = new(arr[mid], axis, parent);

        // Recursively build subtrees
        node.Left = BuildNode(arr, left, mid - 1, depth + 1, node);
        node.Right = BuildNode(arr, mid + 1, right, depth + 1, node);

        // Compute the unvisited count in this subtree
        int leftCount = (node.Left is not null) ? node.Left.UnvisitedCount : 0;
        int rightCount = (node.Right is not null) ? node.Right.UnvisitedCount : 0;
        node.UnvisitedCount = 1 + leftCount + rightCount;

        return node;
    }

    /// <summary>
    /// Uses a Quickselect approach to position the median element at 'mid' for the specified axis.
    /// </summary>
    /// <param name="arr">Array of deliveries.</param>
    /// <param name="left">Left index for selection.</param>
    /// <param name="right">Right index for selection.</param>
    /// <param name="mid">Index where the median should be placed.</param>
    /// <param name="axis">Axis (0 for X, 1 for Y).</param>
    private static void SelectMedian(Delivery[] arr, int left, int right, int mid, int axis)
    {
        while (left < right)
        {
            int pivotIndex = PickPivot(arr, left, right, axis);
            int partitionIndex = Partition(arr, left, right, pivotIndex, axis);

            if (partitionIndex == mid)
                return;

            if (mid < partitionIndex)
                right = partitionIndex - 1;
            else
                left = partitionIndex + 1;
        }
    }

    /// <summary>
    /// Chooses a pivot index by a simple "median of three" method 
    /// to reduce the likelihood of worst-case pivot choices.
    /// </summary>
    private static int PickPivot(Delivery[] arr, int left, int right, int axis)
    {
        int mid = (left + right) / 2;
        SortThree(arr, left, mid, right, axis);
        return mid;
    }

    /// <summary>
    /// Sorts the elements at indices 'a', 'b', and 'c' so that 
    /// arr[a] <= arr[b] <= arr[c] based on the specified axis.
    /// </summary>
    private static void SortThree(Delivery[] arr, int a, int b, int c, int axis)
    {
        if (Compare(arr[b], arr[a], axis) < 0)
            Swap(arr, a, b);

        if (Compare(arr[c], arr[a], axis) < 0)
            Swap(arr, a, c);

        if (Compare(arr[c], arr[b], axis) < 0)
            Swap(arr, b, c);
    }

    /// <summary>
    /// Partitions 'arr' such that elements less than pivot go to the left, 
    /// while elements greater or equal go to the right.
    /// Returns the final index of the pivot after partition.
    /// </summary>
    private static int Partition(Delivery[] arr, int left, int right, int pivotIndex, int axis)
    {
        Delivery pivotValue = arr[pivotIndex];
        Swap(arr, pivotIndex, right);

        int storeIndex = left;
        for (int i = left; i < right; i++)
        {
            if (Compare(arr[i], pivotValue, axis) < 0)
            {
                Swap(arr, i, storeIndex);
                storeIndex++;
            }
        }

        Swap(arr, storeIndex, right);
        return storeIndex;
    }

    /// <summary>
    /// Compares two deliveries by the specified axis (0 => X, 1 => Y).
    /// </summary>
    private static int Compare(Delivery a, Delivery b, int axis)
    {
        // Compare by X if axis=0, else compare by Y
        return (axis == 0)
            ? a.X.CompareTo(b.X)
            : a.Y.CompareTo(b.Y);
    }

    /// <summary>
    /// Swaps the array elements at indices i and j if i != j.
    /// </summary>
    private static void Swap(Delivery[] arr, int i, int j)
    {
        if (i != j)
        {
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    #endregion

    #region Nearest-Neighbor Search

    /// <summary>
    /// Recursively finds the nearest unvisited node to 'query'. 
    /// The search is pruned when a subtree is fully visited or obviously outside the radius of the current best.
    /// </summary>
    private static void SearchNearest(Node node, Delivery query, ref Node? bestNode, ref double bestDistSq)
    {
        // If there's nothing unvisited in this subtree, skip
        if (node.UnvisitedCount == 0)
            return;

        // Check the current node if it's still unvisited
        if (!node.Visited)
        {
            double distSq = DistanceSq(query, node.Point);

            // If we found a strictly closer node, update
            if (distSq < bestDistSq - 1e-9)
            {
                bestDistSq = distSq;
                bestNode = node;
            }
            else if (Math.Abs(distSq - bestDistSq) < 1e-9 && bestNode is not null)
            {
                // Tie-break by lower Delivery Id
                if (node.Point.Id < bestNode.Point.Id)
                    bestNode = node;
            }
        }

        // Determine which side to search first based on the node's axis
        int axisValue = (node.Axis == 0) ? query.X : query.Y;
        int nodeValue = (node.Axis == 0) ? node.Point.X : node.Point.Y;

        Node? primary = (axisValue < nodeValue) ? node.Left : node.Right;
        Node? secondary = (primary == node.Left) ? node.Right : node.Left;

        // Search deeper on the primary side
        if (primary is not null)
            SearchNearest(primary, query, ref bestNode, ref bestDistSq);

        // Check if we need to explore the secondary side 
        // (only if distance along this axis is within the current best distance)
        double diff = axisValue - nodeValue;
        double diffSq = diff * diff;

        if (diffSq < bestDistSq + 1e-9 && secondary is not null)
            SearchNearest(secondary, query, ref bestNode, ref bestDistSq);
    }

    /// <summary>
    /// Computes the squared distance between two deliveries, 
    /// avoiding floating-point overhead from sqrt until necessary.
    /// </summary>
    private static double DistanceSq(Delivery a, Delivery b)
    {
        long dx = (long)a.X - b.X;
        long dy = (long)a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    #endregion
}
