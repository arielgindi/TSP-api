using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace RouteOptimizationApi
{
    // SignalR Hub for sending progress updates to clients
    public class OptimizationHub : Hub
    {
        // Optional: Add methods here if clients need to call the server via SignalR
        // For now, we only need the server to push updates TO clients.

        public override async Task OnConnectedAsync()
        {
            // Optional: Logic when a client connects
            // e.g., add to a group: await Groups.AddToGroupAsync(Context.ConnectionId, "OptimizationUpdates");
            await base.OnConnectedAsync();
            // Send a confirmation or initial state if needed
            // await Clients.Client(Context.ConnectionId).SendAsync("ReceiveMessage", new ProgressUpdate { Message = "Connected to optimization feed.", Style = "info" });
        }

        public override async Task OnDisconnectedAsync(System.Exception? exception)
        {
            // Optional: Logic when a client disconnects
            // e.g., remove from group: await Groups.RemoveFromGroupAsync(Context.ConnectionId, "OptimizationUpdates");
            await base.OnDisconnectedAsync(exception);
        }
    }
}