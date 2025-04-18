namespace RouteOptimizationApi.Common
{
    public record ProgressUpdate(
        string Step,
        string Message,
        string Style,
        object Data,
        bool ClearPreviousProgress = false
    );
}
