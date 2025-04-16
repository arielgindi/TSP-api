namespace RouteOptimizationApi
{
    public record ProgressUpdate
    {
        public string? Step { get; init; }
        public required string Message { get; init; }
        public required string Style { get; init; }
        public object? Data { get; init; }
        public bool ClearPreviousProgress { get; init; } = false;
    }
}
