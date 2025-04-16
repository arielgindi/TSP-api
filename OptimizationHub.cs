using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace RouteOptimizationApi
{
    public class OptimizationHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(System.Exception? exception)
        {
            await base.OnDisconnectedAsync(exception);
        }
    }
}
