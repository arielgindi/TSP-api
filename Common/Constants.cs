using System;

namespace RouteOptimizationApi.Common
{
    public static class Constants
    {
        public const string DistanceUnit = "meters";
        public const int MaxRouteDisplay = 40;
        public const int MaxAttempts = 30000;
        public const double ImprovementThreshold = 1e-9;
        public const double Epsilon = 1e-9;
        public const int ProgressReportIntervalMs = 200;
        public const int Max2OptIterations = 10000;
    }
}
