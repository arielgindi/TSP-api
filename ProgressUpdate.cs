// File: ProgressUpdate.cs
namespace RouteOptimizationApi
{
    // Structure for messages sent via SignalR
    public record ProgressUpdate
    {
        public string? Step { get; init; }       // e.g., "INIT", "NN.1", "GI.3", "PARTITION", "FINAL"
        public required string Message { get; init; }
        public required string Style { get; init; } // e.g., "header", "info", "success", "warning", "detail", "result", "debug", "error", "progress"
        public object? Data { get; init; }      // Optional payload (e.g., distance, time, count)
        public bool ClearPreviousProgress { get; init; } = false; // Flag to clear specific progress lines (like combinations checked)
    }
}