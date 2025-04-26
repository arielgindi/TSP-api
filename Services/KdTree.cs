using System;
using System.Collections.Generic;
using RouteOptimizationApi.Common;
using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services;

/// <summary>
/// A 2D KD-Tree specialized for Delivery points (excluding Depot with Id=0).
/// 
/// Features:
///  - O(n log n) build via in-place median finding (quickselect).
///  - O(log n) repeated nearest neighbor searches on average.
///  - "Removal" done by marking nodes visited + decrementing subtree counts,
///    avoiding costly subtree restructuring.
/// </summary>
public sealed class KdTree
{
    /// <summary>
    /// Internal tree node storing one Delivery.
    /// Axis = 0 => X-split, 1 => Y-split.
    /// </summary>
    private sealed class Node
    {
        public Delivery Point;
        public int Axis;
        public bool Visited;

        public int UnvisitedCount; // how many unvisited nodes in this subtree
        public Node? Left;
        public Node? Right;
        public Node? Parent;

        public Node(Delivery point, int axis, Node? parent)
        {
            Point = point;
            Axis = axis;
            Parent = parent;
            UnvisitedCount = 1; // includes itself
        }
    }

    private Node? _root;
    private int _globalUnvisited;

    /// <summary>
    /// Creates a KD-Tree from a set of deliveries, ignoring any with Id=0 (the depot).
    /// </summary>
    public KdTree(IEnumerable<Delivery>? deliveries)
    {
        if (deliveries == null) throw new ArgumentNullException(nameof(deliveries));

        List<Delivery> points = [];
        foreach (Delivery d in deliveries)
        {
            if (d != null && d.Id != 0)
            {
                points.Add(d);
            }
        }

        _globalUnvisited = points.Count;
        if (_globalUnvisited == 0) return; // no valid deliveries

        Delivery[] arr = points.ToArray();
        _root = BuildNode(arr, 0, arr.Length - 1, depth: 0, parent: null);
    }

    /// <summary>
    /// True if there is at least one unvisited Delivery left in the tree.
    /// </summary>
    public bool HasUnvisited => (_globalUnvisited > 0);

    /// <summary>
    /// Finds and marks the nearest unvisited point to 'query' as visited.
    /// Returns that Delivery, or null if none remain.
    /// </summary>
    public Delivery? PopNearest(Delivery query)
    {
        if (_root == null || _globalUnvisited == 0) return null;

        Node? bestNode = null;
        double bestDistSq = double.MaxValue;

        SearchNearest(_root, query, ref bestNode, ref bestDistSq);

        if (bestNode == null) return null; // shouldn't occur if unvisited remain

        // Mark visited, decrement subtree counts up the chain
        bestNode.Visited = true;
        Node? current = bestNode;
        while (current != null)
        {
            current.UnvisitedCount--;
            current = current.Parent;
        }
        _globalUnvisited--;

        return bestNode.Point;
    }

    #region Build

    /// <summary>
    /// Recursively builds the KD-Tree by selecting the median in the current dimension.
    /// </summary>
    private Node? BuildNode(Delivery[] arr, int left, int right, int depth, Node? parent)
    {
        if (left > right) return null;

        int axis = depth % 2; // 0 => X, 1 => Y
        int mid = (left + right) / 2;

        SelectMedian(arr, left, right, mid, axis);

        Node node = new(arr[mid], axis, parent);

        node.Left = BuildNode(arr, left, mid - 1, depth + 1, node);
        node.Right = BuildNode(arr, mid + 1, right, depth + 1, node);

        int leftCount = (node.Left != null) ? node.Left.UnvisitedCount : 0;
        int rightCount = (node.Right != null) ? node.Right.UnvisitedCount : 0;
        node.UnvisitedCount = 1 + leftCount + rightCount;

        return node;
    }

    /// <summary>
    /// Uses quickselect to place the median element at index 'mid' in dimension 'axis'.
    /// </summary>
    private static void SelectMedian(Delivery[] arr, int left, int right, int mid, int axis)
    {
        while (left < right)
        {
            int pivotIndex = PickPivot(arr, left, right, axis);
            int partitionIndex = Partition(arr, left, right, pivotIndex, axis);

            if (partitionIndex == mid) return;

            if (mid < partitionIndex) right = partitionIndex - 1;
            else left = partitionIndex + 1;
        }
    }

    /// <summary>
    /// Picks a pivot index using 'median of three' to reduce worst-case scenarios.
    /// </summary>
    private static int PickPivot(Delivery[] arr, int left, int right, int axis)
    {
        int mid = (left + right) / 2;
        SortThree(arr, left, mid, right, axis);
        return mid;
    }

    /// <summary>
    /// Sorts arr[a], arr[b], arr[c] by the chosen axis so that arr[a] <= arr[b] <= arr[c].
    /// </summary>
    private static void SortThree(Delivery[] arr, int a, int b, int c, int axis)
    {
        if (Compare(arr[b], arr[a], axis) < 0) Swap(arr, a, b);
        if (Compare(arr[c], arr[a], axis) < 0) Swap(arr, a, c);
        if (Compare(arr[c], arr[b], axis) < 0) Swap(arr, b, c);
    }

    /// <summary>
    /// Partitions the array so that items < pivot go left, items >= pivot go right.
    /// Returns the final pivot index.
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

    /// <summary>Compares a vs. b by the given axis (0=X, 1=Y).</summary>
    private static int Compare(Delivery a, Delivery b, int axis)
    {
        return (axis == 0)
            ? a.X.CompareTo(b.X)
            : a.Y.CompareTo(b.Y);
    }

    private static void Swap(Delivery[] arr, int i, int j)
    {
        if (i != j)
        {
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    #endregion

    #region Nearest Neighbor

    /// <summary>
    /// Recursively searches for the nearest unvisited node to 'query',
    /// pruning subtrees with UnvisitedCount=0 or obviously too far.
    /// </summary>
    private static void SearchNearest(Node node, Delivery query, ref Node? bestNode, ref double bestDistSq)
    {
        // If subtree is entirely visited, skip
        if (node.UnvisitedCount == 0) return;

        // Check current node if unvisited
        if (!node.Visited)
        {
            double distSq = DistanceSq(query, node.Point);

            // Check if strictly closer, or tie-break on Id
            if (distSq < bestDistSq - 1e-9)
            {
                bestDistSq = distSq;
                bestNode = node;
            }
            else if (Math.Abs(distSq - bestDistSq) < 1e-9 && bestNode != null)
            {
                // tie => pick smaller Id
                if (node.Point.Id < bestNode.Point.Id)
                {
                    bestNode = node;
                }
            }
        }

        // Recurse into subtrees
        int axisValue = (node.Axis == 0) ? query.X : query.Y;
        int nodeValue = (node.Axis == 0) ? node.Point.X : node.Point.Y;

        Node? primary = (axisValue < nodeValue) ? node.Left : node.Right;
        Node? secondary = (primary == node.Left) ? node.Right : node.Left;

        // Search the nearer side first
        if (primary != null) SearchNearest(primary, query, ref bestNode, ref bestDistSq);

        // Prune if the plane is beyond our best circle
        double diff = axisValue - nodeValue;
        double diffSq = diff * diff;
        if (diffSq < bestDistSq + 1e-9 && secondary != null)
        {
            SearchNearest(secondary, query, ref bestNode, ref bestDistSq);
        }
    }

    private static double DistanceSq(Delivery a, Delivery b)
    {
        long dx = (long)a.X - b.X;
        long dy = (long)a.Y - b.Y;
        return (double)(dx * dx + dy * dy);
    }

    #endregion
}
