using Microsoft.AspNetCore.SignalR;

namespace RouteOptimizationApi.Hubs;

public class OptimizationHub : Hub
{
    // called by client to get their own ID
    public Task SendConnectionId()
    {
        return Clients.Caller.SendAsync("ReceiveConnectionId", Context.ConnectionId);
    }
}
