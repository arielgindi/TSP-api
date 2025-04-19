using RouteOptimizationApi.Models;

namespace RouteOptimizationApi.Services
{
    /// <summary>
    /// Represents a node in a doubly linked list for deliveries.
    /// </summary>
    public class DoublyLinkedDeliveryNode(Delivery delivery)
    {
        // The actual delivery information.
        public Delivery Delivery { get; } = delivery;

        // Pointers to the previous and next nodes in the chain.
        public DoublyLinkedDeliveryNode? Previous { get; set; } = null;
        public DoublyLinkedDeliveryNode? Next { get; set; } = null;

        // Used to track the "leader" of the cluster this node belongs to.
        public int ClusterLeaderId { get; set; } = delivery.Id;
    }
}
