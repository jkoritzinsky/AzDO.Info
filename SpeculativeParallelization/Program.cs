using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi.Legacy;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpeculativeParallelization
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 2)
            {
                Uri orgUrl = new Uri("https://dev.azure.com/dnceng");

                string personalAccessToken = args[0];
                int numberOfBuilds = int.Parse(args[1]);

                // Create a connection
                VssConnection connection = new VssConnection(orgUrl, new VssBasicCredential(string.Empty, personalAccessToken));

                var buildClient = await connection.GetClientAsync<BuildHttpClient>();
                var definition = await buildClient.GetDefinitionsAsync("internal", name: "dotnet-runtime-official");

                var builds = await buildClient.GetBuildsAsync("internal", definitions: definition.Select(def => def.Id), branchName: "refs/heads/master", statusFilter: BuildStatus.Completed, resultFilter: BuildResult.Succeeded, top: numberOfBuilds);

                var timelines = await Task.WhenAll(builds.Select(build => buildClient.GetBuildTimelineAsync(build.Project.Name, build.Id)));

                var buildTimeCurrent = timelines.Select(t => t.Records.First(r => r.Name == "Build" && r.RecordType == "Stage")).Select(r => r.GetDuration()).ToArray();

                Array.Sort(buildTimeCurrent);

                OutputStatistics("Current product build times (including incremental signing steps):", buildTimeCurrent);

                var timeCurrentManifest = timelines.Select(t => SpeculativelyParallelizeTimeline_CurrentPlatformManifest(t)).ToArray();

                Array.Sort(timeCurrentManifest);

                OutputStatistics("Likely best parallelization with current manifest generation:", timeCurrentManifest);

                var timeGeneratedManifest = timelines.Select(t => SpeculativelyParallelizeTimeline_GeneratedPlatformManifest(t)).ToArray();

                Array.Sort(timeGeneratedManifest);

                OutputStatistics("Likely best parallelization with generated manifest:", timeGeneratedManifest);
            }
            else
            {
                Console.WriteLine("Usage: SpeculativeParallelization {personalAccessToken} {numberOfBuilds}");
            }
        }

        private static void OutputStatistics(string header, TimeSpan[] times)
        {
            var numTimes = times.Length;
            var average = TimeSpan.FromMilliseconds(times.Average(time => time.TotalMilliseconds));
            var standardDeviation = TimeSpan.FromMilliseconds(Math.Sqrt(times.Sum(time => (time - average).TotalMilliseconds * (time - average).TotalMilliseconds) / numTimes));

            Console.WriteLine(header);
            Console.WriteLine($"25th percentile: {times[numTimes / 4]}");
            Console.WriteLine($"50th percentile: {times[numTimes / 2]}");
            Console.WriteLine($"75th percentile: {times[numTimes * 3 / 4]}");
            Console.WriteLine($"100th percentile: {times[^1]}");
            Console.WriteLine($"Average: {average}");
            Console.WriteLine($"Standard Deviation: {standardDeviation}");
        }

        static TimeSpan SpeculativelyParallelizeTimeline_CurrentPlatformManifest(Timeline timeline)
        {
            var coreClrJobs = timeline.Records.Where(record => record.Name.StartsWith("CoreCLR Product Build"));
            var monoJobs = timeline.Records.Where(record => record.Name.StartsWith("Mono Product Build"));
            var librariesJobs = timeline.Records.Where(record => record.Name.StartsWith("Libraries Build"));
            var installerJobs = timeline.Records.Where(record => record.Name.StartsWith("Installer Build and Test"));
            var runtimeLibrariesTime = coreClrJobs.Concat(monoJobs).Concat(librariesJobs).Max(job => job.GetDuration());
            var installerJobsTime = installerJobs.Max(job => job.GetDuration());
            return runtimeLibrariesTime + installerJobsTime;
        }

        static TimeSpan SpeculativelyParallelizeTimeline_GeneratedPlatformManifest(Timeline timeline)
        {
            return (from record in timeline.Records
                    where record.RecordType == "Job"
                    group record by GetPlatform(record.Name)
                    into platformBuilds
                    where platformBuilds.Key != ""
                    let installerJob = platformBuilds.FirstOrDefault(j => j.Name.StartsWith("Installer Build and Test"))
                    select platformBuilds.Except(new[] { installerJob }).Max(r => r.GetDuration()) + (installerJob?.GetDuration() ?? TimeSpan.Zero))
                    .Max();
        }

        private static Regex platformRegex = new Regex("(Windows NT|Linux( musl)?|OSX|iOS) (x86|x64|arm|arm64) ", RegexOptions.Compiled);

        private static string GetPlatform(string jobName)
        {
            return platformRegex.Match(jobName.Replace('_', ' ')).Value;
        }
    }

    static class Extenions
    {
        public static TimeSpan GetDuration(this TimelineRecord record)
        {
            return (TimeSpan)(record.FinishTime - record.StartTime);
        }
    }
}
