using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace RouteOptimizationApi
{
    public class OptimizationRequest
    {
        public int NumberOfDeliveries { get; set; }
        public int NumberOfDrivers { get; set; }
        public int MinCoordinate { get; set; } = -10000;
        public int MaxCoordinate { get; set; } = 10000;
    }

    public class DriverRoute
    {
        public int DriverId { get; set; }
        public List<Delivery> RoutePoints { get; set; } = new List<Delivery>();
        public List<int> DeliveryIds { get; set; } = new List<int>();
        public double Distance { get; set; }
        public List<int> OriginalIndices { get; set; } = new List<int>();
    }

    // --- OptimizationResult modified ---
    public class OptimizationResult
    {
        public string BestMethod { get; set; } = string.Empty;
        public List<Delivery> GeneratedDeliveries { get; set; } = new List<Delivery>();
        public List<Delivery> OptimizedRoute { get; set; } = new List<Delivery>();
        public int[] BestCutIndices { get; set; } = Array.Empty<int>();
        public double MinMakespan { get; set; }
        public List<DriverRoute> DriverRoutes { get; set; } = new List<DriverRoute>();
        public double InitialDistanceNN { get; set; }
        public double OptimizedDistanceNN { get; set; }
        public double InitialDistanceCWS { get; set; } // Changed from GI
        public double OptimizedDistanceCWS { get; set; } // Changed from GI
        public string ErrorMessage { get; set; } = string.Empty;
        public double TotalExecutionTimeMs { get; set; }
        public double PathExecutionTimeMs { get; set; } // Stores the time for the *best* path
    }
    // --- End of OptimizationResult modification ---


    public class Delivery
    {
        public int Id { get; }
        public int X { get; }
        public int Y { get; }

        public Delivery(int id, int x, int y)
        {
            Id = id;
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            if (Id == 0) return "Depot(0,0)";
            return "Delivery#" + Id + "(" + X + "," + Y + ")";
        }
    }

    public class Program
    {
        public static async Task Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSignalR();
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowNextApp", policy =>
                {
                    policy.WithOrigins("http://localhost:3000") // Adjust if your frontend runs elsewhere
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            WebApplication app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
                app.UseDeveloperExceptionPage();
            }

            app.UseCors("AllowNextApp");

            app.MapHub<OptimizationHub>("/optimizationhub");

            app.MapPost("/api/optimize", async (OptimizationRequest request, IHostApplicationLifetime lifetime, IHubContext<OptimizationHub> hubContext) =>
            {
                Stopwatch overallStopwatch = new Stopwatch();
                overallStopwatch.Start();
                OptimizationResult? nnResultData = null;
                OptimizationResult? cwsResultData = null; // Changed from giResultData
                OptimizationResult finalCombinedResult = new OptimizationResult();

                try
                {
                    await SendProgressUpdate(hubContext, "INIT", "Starting Optimization Process...", "header");
                    await SendProgressUpdate(hubContext, "SETUP", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Coordinate Range: [{request.MinCoordinate}, {request.MaxCoordinate}]", "info");
                    await SendProgressUpdate(hubContext, "SETUP", $"Depot: {TspAlgorithm.Depot}", "info");
                    // --- Updated Heuristics ---
                    await SendProgressUpdate(hubContext, "SETUP", "Heuristics: Nearest Neighbor, Clarke-Wright Savings", "info");
                    // --- End Update ---
                    await SendProgressUpdate(hubContext, "SETUP", "Optimization: 2-Opt", "info");
                    await SendProgressUpdate(hubContext, "SETUP", "Partitioning Goal: Minimize Makespan (Binary Search)", "info");

                    if (request.NumberOfDeliveries <= 0 || request.NumberOfDrivers <= 0)
                    {
                        throw new ArgumentException("Number of deliveries and drivers must be positive.");
                    }
                    if (request.MinCoordinate >= request.MaxCoordinate)
                    {
                        throw new ArgumentException("Min coordinate must be less than Max coordinate.");
                    }
                    if (request.NumberOfDeliveries < request.NumberOfDrivers)
                    {
                        await SendProgressUpdate(hubContext, "SETUP", $"Warning: Fewer deliveries ({request.NumberOfDeliveries}) than drivers ({request.NumberOfDrivers}). Some drivers may have no route.", "warning");
                    }

                    await SendProgressUpdate(hubContext, "GENERATE", "[1. GENERATING DELIVERIES...]", "step-header");
                    Stopwatch genWatch = new Stopwatch();
                    genWatch.Start();
                    List<Delivery> allDeliveries = TspAlgorithm.GenerateRandomDeliveries(request.NumberOfDeliveries, request.MinCoordinate, request.MaxCoordinate);
                    genWatch.Stop();
                    await SendProgressUpdate(hubContext, "GENERATE", $"✔ {allDeliveries.Count} random deliveries generated successfully.", "success", new { Time = FormatTimeSpan(genWatch.Elapsed) });
                    await SendProgressUpdate(hubContext, "GENERATE", $"Time elapsed: {FormatTimeSpan(genWatch.Elapsed)}", "detail");


                    // --- Run NN and CWS ---
                    nnResultData = await RunOptimizationPath("NN", TspAlgorithm.ConstructNearestNeighborRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    cwsResultData = await RunOptimizationPath("CWS", TspAlgorithm.ConstructClarkeWrightRoute, allDeliveries, request.NumberOfDrivers, hubContext);
                    // --- End Run ---


                    await SendProgressUpdate(hubContext, "COMPARE", "[3. COMPARISON & FINAL RESULTS]", "step-header");

                    // --- Comparison Logic Updated ---
                    if (nnResultData == null || cwsResultData == null)
                    {
                         throw new InvalidOperationException("Optimization paths did not complete successfully.");
                    }

                    bool nnBetter = nnResultData.MinMakespan < cwsResultData.MinMakespan;
                    bool tie = Math.Abs(nnResultData.MinMakespan - cwsResultData.MinMakespan) < Constants.Epsilon;
                    OptimizationResult bestPathResult;
                    string winnerMethod;

                    if (tie || nnBetter)
                    {
                        bestPathResult = nnResultData;
                        winnerMethod = tie ? "NN / CWS (Tie)" : "Nearest Neighbor";
                    }
                    else
                    {
                        bestPathResult = cwsResultData;
                        winnerMethod = "Clarke-Wright Savings";
                    }
                    // --- End Comparison Update ---

                    await SendProgressUpdate(hubContext, "COMPARE", "Comparison of Final Makespan:", "info");
                    await SendProgressUpdate(hubContext, "COMPARE", $"- Nearest Neighbor Path : {nnResultData.MinMakespan:F2} {Constants.DistanceUnit}", "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(nnResultData.PathExecutionTimeMs)) });
                    // --- Update CWS message ---
                    await SendProgressUpdate(hubContext, "COMPARE", $"- Clarke-Wright Path   : {cwsResultData.MinMakespan:F2} {Constants.DistanceUnit}", "detail", new { Time = FormatTimeSpan(TimeSpan.FromMilliseconds(cwsResultData.PathExecutionTimeMs)) });
                    // --- End Update ---
                    await SendProgressUpdate(hubContext, "COMPARE", $"✔ Best Result: '{winnerMethod}' -> Makespan: {bestPathResult.MinMakespan:F2} {Constants.DistanceUnit}", "success");

                    // --- Populate Final Result ---
                    finalCombinedResult = bestPathResult; // Assign the winning path's data
                    finalCombinedResult.BestMethod = winnerMethod;
                    List<Delivery> combinedDeliveries = new List<Delivery> { TspAlgorithm.Depot };
                    combinedDeliveries.AddRange(allDeliveries);
                    finalCombinedResult.GeneratedDeliveries = combinedDeliveries;
                    finalCombinedResult.InitialDistanceNN = nnResultData.InitialDistanceNN;
                    finalCombinedResult.OptimizedDistanceNN = nnResultData.OptimizedDistanceNN;
                    finalCombinedResult.InitialDistanceCWS = cwsResultData.InitialDistanceCWS;   // Changed from GI
                    finalCombinedResult.OptimizedDistanceCWS = cwsResultData.OptimizedDistanceCWS; // Changed from GI
                    finalCombinedResult.PathExecutionTimeMs = bestPathResult.PathExecutionTimeMs; // Store the best path's time
                    // --- End Populate ---


                    await SendProgressUpdate(hubContext, "DETAILS", $"[4. DETAILS OF BEST SOLUTION ({winnerMethod})]", "step-header");
                    await SendProgressUpdate(hubContext, "DETAILS", "Optimized Route Order (Delivery IDs):", "info");
                    IEnumerable<int> ids = finalCombinedResult.OptimizedRoute.Any() ? finalCombinedResult.OptimizedRoute.Select(d => d.Id) : Enumerable.Empty<int>();
                    string routeChain;
                    if (finalCombinedResult.OptimizedRoute.Count > Constants.MaxRouteDisplay + 2)
                    {
                        routeChain = string.Join(" -> ", ids.Take(Constants.MaxRouteDisplay / 2)) + " ... " + string.Join(" -> ", ids.Skip(finalCombinedResult.OptimizedRoute.Count - Constants.MaxRouteDisplay / 2));
                    }
                    else
                    {
                        routeChain = string.Join(" -> ", ids);
                    }
                    await SendProgressUpdate(hubContext, "DETAILS", routeChain, "detail");

                    if (finalCombinedResult.OptimizedRoute.Any())
                    {
                        finalCombinedResult.DriverRoutes = PopulateDriverRoutes(finalCombinedResult.OptimizedRoute, finalCombinedResult.BestCutIndices, request.NumberOfDrivers);

                        await SendProgressUpdate(hubContext, "DRIVERS", "[DRIVER SUB-ROUTE DETAILS]", "step-header");
                        foreach (DriverRoute dr in finalCombinedResult.DriverRoutes)
                        {
                            await SendProgressUpdate(hubContext, "DRIVERS", $"Driver #{dr.DriverId}:", "info");
                            if (dr.OriginalIndices.Count > 2 && dr.DeliveryIds.Count > 2) // Check if it has actual deliveries
                            {
                                int startIdx = dr.OriginalIndices[1];
                                int endIdx = dr.OriginalIndices[dr.OriginalIndices.Count - 2];
                                await SendProgressUpdate(hubContext, "DRIVERS", $"- Route Indices : [{startIdx}..{endIdx}]", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", $"- Total Distance: {dr.Distance:F2} {Constants.DistanceUnit}", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", $" Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
                            }
                            else
                            {
                                // Driver has no deliveries (only Depot -> Depot)
                                await SendProgressUpdate(hubContext, "DRIVERS", "- Route Indices : N/A (No deliveries)", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", $"- Total Distance: {dr.Distance:F2} {Constants.DistanceUnit}", "detail");
                                await SendProgressUpdate(hubContext, "DRIVERS", $" Sub-Route IDs : {string.Join(" -> ", dr.DeliveryIds)}", "detail-mono");
                            }
                        }
                    }
                    else
                    {
                         finalCombinedResult.DriverRoutes = new List<DriverRoute>();
                         await SendProgressUpdate(hubContext, "DRIVERS", "[No driver routes generated]", "warning");
                    }


                    overallStopwatch.Stop();
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;

                    await SendProgressUpdate(hubContext, "SUMMARY", "[FINAL SUMMARY]", "header");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Scenario: {request.NumberOfDeliveries} deliveries, {request.NumberOfDrivers} drivers.", "info");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Best Makespan ({winnerMethod}): {finalCombinedResult.MinMakespan:F2} {Constants.DistanceUnit}", "result");
                    await SendProgressUpdate(hubContext, "SUMMARY", $"Total Execution Time: {FormatTimeSpan(overallStopwatch.Elapsed)}", "info");

                    await SendProgressUpdate(hubContext, "END", "✔ Optimization Complete!", "success-large");
                    return Results.Ok(finalCombinedResult);
                }
                catch (ArgumentException argEx)
                {
                    overallStopwatch.Stop();
                    finalCombinedResult.ErrorMessage = argEx.Message;
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", "Input Error: " + argEx.Message, "error");
                    await SendProgressUpdate(hubContext, "END", "✖ Optimization Failed!", "error-large");
                    return Results.BadRequest(finalCombinedResult);
                }
                catch (Exception ex)
                {
                    overallStopwatch.Stop();
                    Console.WriteLine($"Error during optimization: {ex.Message}{Environment.NewLine}Stack Trace: {ex.StackTrace}");
                    finalCombinedResult.ErrorMessage = app.Environment.IsDevelopment() ? ex.ToString() : "An unexpected error occurred during optimization.";
                    finalCombinedResult.TotalExecutionTimeMs = overallStopwatch.Elapsed.TotalMilliseconds;
                    await SendProgressUpdate(hubContext, "ERROR", "Internal Server Error: " + finalCombinedResult.ErrorMessage, "error");
                    await SendProgressUpdate(hubContext, "END", "✖ Optimization Failed!", "error-large");
                    return Results.Problem(
                        detail: finalCombinedResult.ErrorMessage,
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "Optimization Failed"
                    );
                }
            })
            .WithName("OptimizeRoutes")
            .WithOpenApi()
            .Produces<OptimizationResult>(StatusCodes.Status200OK)
            .Produces<OptimizationResult>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

            await app.RunAsync();
        }


        static async Task SendProgressUpdate(
            IHubContext<OptimizationHub> hubContext,
            string step,
            string message,
            string style,
            object? data = null,
            bool clearPrevious = false
            )
        {
            await hubContext.Clients.All.SendAsync("ReceiveMessage", new ProgressUpdate
            {
                Step = step,
                Message = message,
                Style = style,
                Data = data,
                ClearPreviousProgress = clearPrevious
            });
        }

        // --- RunOptimizationPath modified ---
        static async Task<OptimizationResult> RunOptimizationPath(
            string tag, // "NN" or "CWS"
            Func<List<Delivery>, List<Delivery>> routeBuilder,
            List<Delivery> allDeliveries,
            int numberOfDrivers,
            IHubContext<OptimizationHub> hubContext
            )
        {
            OptimizationResult pathResult = new OptimizationResult();
            Stopwatch totalPathWatch = new Stopwatch();
            totalPathWatch.Start();

            string fullMethodName;
            switch(tag) {
                case "NN": fullMethodName = "Nearest Neighbor"; break;
                case "CWS": fullMethodName = "Clarke-Wright Savings"; break;
                default: fullMethodName = "Unknown Method"; break;
            }

            await SendProgressUpdate(hubContext, tag, $"===== PATH {tag}: {fullMethodName} =====", "header");

            // --- 1. Build Initial Route ---
            Stopwatch buildSw = new Stopwatch();
            buildSw.Start();
            List<Delivery> initialRoute = routeBuilder(allDeliveries);
            buildSw.Stop();
            double initialDist = TspAlgorithm.ComputeTotalRouteDistance(initialRoute);

            // Store initial distance in the correct field based on tag
            if (tag == "NN") pathResult.InitialDistanceNN = initialDist;
            else if (tag == "CWS") pathResult.InitialDistanceCWS = initialDist;

            await SendProgressUpdate(hubContext, tag + ".1", $"✔ {tag} initial TSP route constructed.", "success", new { Time = FormatTimeSpan(buildSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".1", $"* {tag} Initial Route Distance: {initialDist:F2} {Constants.DistanceUnit}", "detail");

            // --- 2. Optimize Route ---
            Stopwatch optSw = new Stopwatch();
            optSw.Start();
            await SendProgressUpdate(hubContext, tag + ".2", $"[{tag}.2 Optimizing Route (2-Opt)...]", "step");
            List<Delivery> optimizedRoute = TspAlgorithm.OptimizeRouteUsing2Opt(initialRoute);
            optSw.Stop();
            double optimizedDist = TspAlgorithm.ComputeTotalRouteDistance(optimizedRoute);

            // Store optimized distance in the correct field based on tag
            if (tag == "NN") pathResult.OptimizedDistanceNN = optimizedDist;
            else if (tag == "CWS") pathResult.OptimizedDistanceCWS = optimizedDist;

            double improvement = initialDist - optimizedDist;
            double percent = initialDist > Constants.Epsilon ? (improvement / initialDist * 100.0) : 0;
            await SendProgressUpdate(hubContext, tag + ".2", $"✔ {tag} route optimized.", "success", new { Time = FormatTimeSpan(optSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".2", $"* {tag} Optimized Route Distance: {optimizedDist:F2} {Constants.DistanceUnit}", "detail");
            await SendProgressUpdate(hubContext, tag + ".2", $"* Improvement Achieved: {improvement:F2} {Constants.DistanceUnit} ({percent:F2}%)", "detail");
            pathResult.OptimizedRoute = optimizedRoute; // Store the optimized route itself

            // --- 3. Partition Route ---
            Stopwatch partSw = new Stopwatch();
            partSw.Start();
            await SendProgressUpdate(hubContext, tag + ".3", $"[{tag}.3 Partitioning for {numberOfDrivers} Drivers (Binary Search)...]", "step");
            long iterations = 0;
            Stopwatch progressReportSw = Stopwatch.StartNew();
            TspAlgorithm.ProgressReporter progressCallback = count =>
            {
                iterations = count;
                if (!progressReportSw.IsRunning || progressReportSw.ElapsedMilliseconds > Constants.ProgressReportIntervalMs)
                {
                    _ = SendProgressUpdate(hubContext, tag + ".3", $"Partitioning (Binary Search Iteration: {iterations})...", "progress", null, true);
                    progressReportSw.Restart();
                }
            };

            TspAlgorithm.FindBestPartitionBinarySearch(
                optimizedRoute,
                numberOfDrivers,
                progressCallback,
                out int[]? bestCuts,
                out double minMakespan
            );
            partSw.Stop();
            await SendProgressUpdate(hubContext, tag + ".3", $"Partitioning complete after {iterations} binary search iterations.", "progress", null, true);

            pathResult.BestCutIndices = bestCuts ?? Array.Empty<int>();
            pathResult.MinMakespan = minMakespan;

            await SendProgressUpdate(hubContext, tag + ".3", $"✔ Optimal partitioning for {tag} route found.", "success", new { Time = FormatTimeSpan(partSw.Elapsed) });
            await SendProgressUpdate(hubContext, tag + ".3", $"* Optimal Cut Indices: [{string.Join(", ", pathResult.BestCutIndices)}]", "detail");
            await SendProgressUpdate(hubContext, tag + ".3", $"* Minimum Makespan Achieved ({tag}): {minMakespan:F2} {Constants.DistanceUnit}", "result");

            // --- Finish Path ---
            totalPathWatch.Stop();
            pathResult.PathExecutionTimeMs = totalPathWatch.Elapsed.TotalMilliseconds;
            await SendProgressUpdate(hubContext, tag, $"Total time for {tag} path: {FormatTimeSpan(totalPathWatch.Elapsed)}", "info");
            return pathResult;
        }
        // --- End of RunOptimizationPath modification ---


        static List<DriverRoute> PopulateDriverRoutes(List<Delivery> optimizedRoute, int[] cutIndices, int driverCount)
        {
            List<DriverRoute> driverRoutes = new List<DriverRoute>();
            // Check for null or route too short (only Depot -> Depot)
            if (optimizedRoute == null || optimizedRoute.Count < 2) return driverRoutes;

            int deliveryCountInRoute = optimizedRoute.Count - 2; // Number of actual deliveries (excluding depots)
            int[] effectiveCuts = cutIndices ?? Array.Empty<int>();

            // Handle case where there are no deliveries
            if (deliveryCountInRoute <= 0)
            {
                for (int d = 1; d <= driverCount; d++)
                {
                    DriverRoute emptyDriverRoute = new DriverRoute { DriverId = d };
                    emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                    emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                    emptyDriverRoute.OriginalIndices.Add(0);
                    emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                    emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                    emptyDriverRoute.OriginalIndices.Add(optimizedRoute.Count > 0 ? optimizedRoute.Count - 1 : 0); // Use actual last index if available
                    emptyDriverRoute.Distance = 0;
                    driverRoutes.Add(emptyDriverRoute);
                }
                return driverRoutes;
            }

            int currentDeliveryRouteIndex = 0; // Tracks the index of the *last* delivery assigned
            for (int d = 1; d <= driverCount; d++)
            {
                DriverRoute driverRoute = new DriverRoute { DriverId = d };
                // Start index in the *optimizedRoute* list (1-based for deliveries)
                int routeStartIndex = currentDeliveryRouteIndex + 1;
                // End index in the *optimizedRoute* list (1-based for deliveries)
                // If this is the last driver, they get all remaining deliveries up to deliveryCountInRoute
                // Otherwise, use the cut index. Cut indices are 1-based relative to deliveries.
                int routeEndIndex = (d - 1) < effectiveCuts.Length ? effectiveCuts[d - 1] : deliveryCountInRoute;

                // Ensure indices are valid and there are deliveries to assign
                bool routeHasDeliveries = routeStartIndex <= routeEndIndex &&
                                          routeStartIndex >= 1 && // Must start at or after the first delivery
                                          routeEndIndex <= deliveryCountInRoute; // Must end at or before the last delivery

                // Always start at the depot
                driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                driverRoute.OriginalIndices.Add(0);

                if (routeHasDeliveries)
                {
                    for (int i = routeStartIndex; i <= routeEndIndex; i++)
                    {
                        // Indices i here correspond to the optimizedRoute list (Depot is 0, first delivery is 1, etc.)
                        if (i > 0 && i < optimizedRoute.Count - 1) // Check bounds carefully
                        {
                            driverRoute.RoutePoints.Add(optimizedRoute[i]);
                            driverRoute.DeliveryIds.Add(optimizedRoute[i].Id);
                            driverRoute.OriginalIndices.Add(i);
                        }
                        else
                        {
                             Console.WriteLine($"Warning: Invalid index {i} accessed during driver route population (Start: {routeStartIndex}, End: {routeEndIndex}, RouteCount: {optimizedRoute.Count})");
                        }
                    }
                }

                // Always end at the depot
                driverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                driverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                driverRoute.OriginalIndices.Add(optimizedRoute.Count - 1); // Last element is always the depot

                // Calculate distance for this sub-route (Depot -> deliveries -> Depot)
                driverRoute.Distance = TspAlgorithm.ComputeSubRouteDistanceWithDepot(optimizedRoute, routeStartIndex, routeEndIndex);

                driverRoutes.Add(driverRoute);
                currentDeliveryRouteIndex = routeEndIndex; // Update the last assigned delivery index

                // If all deliveries are assigned and there are still drivers left, create empty routes for them
                if (currentDeliveryRouteIndex >= deliveryCountInRoute && d < driverCount)
                {
                   for(int remainingDriverId = d + 1; remainingDriverId <= driverCount; remainingDriverId++)
                   {
                        DriverRoute emptyDriverRoute = new DriverRoute { DriverId = remainingDriverId };
                        emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                        emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                        emptyDriverRoute.OriginalIndices.Add(0);
                        emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                        emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                        emptyDriverRoute.OriginalIndices.Add(optimizedRoute.Count - 1);
                        emptyDriverRoute.Distance = 0;
                        driverRoutes.Add(emptyDriverRoute);
                   }
                   break; // Exit the main loop as all deliveries are assigned
                }
            }

            // Ensure we always return exactly `driverCount` routes, even if some are empty
             while (driverRoutes.Count < driverCount)
             {
                 int nextDriverId = driverRoutes.Count + 1;
                 DriverRoute emptyDriverRoute = new DriverRoute { DriverId = nextDriverId };
                 emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                 emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                 emptyDriverRoute.OriginalIndices.Add(0);
                 emptyDriverRoute.RoutePoints.Add(TspAlgorithm.Depot);
                 emptyDriverRoute.DeliveryIds.Add(TspAlgorithm.Depot.Id);
                 emptyDriverRoute.OriginalIndices.Add(optimizedRoute.Count > 0 ? optimizedRoute.Count - 1 : 0);
                 emptyDriverRoute.Distance = 0;
                 driverRoutes.Add(emptyDriverRoute);
             }


            return driverRoutes;
        }


        public static string FormatTimeSpan(TimeSpan span)
        {
            double timeInMs = span.TotalMilliseconds;
            if (timeInMs >= 1000) return (timeInMs / 1000.0).ToString("F3") + " sec";
            if (timeInMs < 1.0 && timeInMs > 0) return (timeInMs * 1000.0).ToString("F0") + " µs";
            if (timeInMs == 0) return "0 ms";
            return timeInMs.ToString("F2") + " ms";
        }
    }
}