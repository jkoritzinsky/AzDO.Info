using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi.Legacy;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SigningTime
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

                var builds = await buildClient.GetBuildsAsync("internal", definitions: definition.Select(def => def.Id), branchName: "refs/heads/master", statusFilter: BuildStatus.Completed, resultFilter : BuildResult.Succeeded, top: numberOfBuilds);

                var timelines = await Task.WhenAll(builds.Select(async build => (timeline: await buildClient.GetBuildTimelineAsync(build.Project.Name, build.Id), project: build.Project.Name, id: build.Id)));

                var signingTimes = await Task.WhenAll(timelines.Select(timeline => CalculateSigningTime(buildClient, timeline.timeline, timeline.project, timeline.id)));

                var validationDurations = timelines.Select(t => t.timeline.Records.First(r => r.Name == "Validate").GetDuration());

                var futurePromotionTimes = signingTimes.Zip(validationDurations, (sign, validate) => sign + validate);

                var buildWithoutSignOrValidate = builds.Select(build => build.FinishTime - build.StartTime).Zip(futurePromotionTimes, (buildTime, promotionTime) => buildTime.Value - promotionTime).ToArray();


                var publishingDurations = timelines.Select(t => t.timeline.Records.First(r => r.Name == ".NET Core 5 Dev Publishing" || r.Name == ".NET 5 Dev Publishing").GetDuration());

                Array.Sort(signingTimes);

                TimeSpan averageSigning = TimeSpan.FromMilliseconds(signingTimes.Average(time => time.TotalMilliseconds));
                TimeSpan averageBuildNoSignValidate = TimeSpan.FromMilliseconds(buildWithoutSignOrValidate.Average(time => time.TotalMilliseconds));
                TimeSpan averagePublishTime = TimeSpan.FromMilliseconds(publishingDurations.Average(time => time.TotalMilliseconds));

                TimeSpan median = signingTimes[signingTimes.Length / 2];

                Console.WriteLine($"Time spent signing over the last {numberOfBuilds} official builds for dotnet/runtime");
                Console.WriteLine($"Average time spent signing in the build: {averageSigning}");
                Console.WriteLine($"Median time spent signing in the build: {median}");
                Console.WriteLine();
                Console.WriteLine($"Average build time without signing/validation: {averageBuildNoSignValidate}");
                Console.WriteLine($"Average time spent publishing : {averagePublishTime}. We have on average {TimeSpan.FromMinutes(45) - averagePublishTime} left for build.");
                Console.WriteLine($"On average, product build (no signing, validation, or publishing) takes about {averageBuildNoSignValidate - averagePublishTime}.");
            }
            else
            {
                Console.WriteLine("Usage: SigningTime {personalAccessToken} {numberOfBuilds}");
            }
        }

        private static async Task<TimeSpan> CalculateSigningTime(BuildHttpClient client, Timeline timeline, string projectName, int buildId)
        {
            var prepareSignedArtifactsJob = timeline.Records.First(r => r.Name == "Prepare Signed Artifacts" && r.RecordType == "Job");
            var signingTime = prepareSignedArtifactsJob.GetDuration();
            var installerJobs = timeline.Records.Where(r => r.Name.StartsWith("Installer Build and Test") && r.RecordType == "Job");

            var installerBuildStep = timeline.Records.Where(r => r.Name == "Build" && installerJobs.Any(job => job.Id == r.ParentId));

            TimeSpan? installerBuildSigningTime = null;

            foreach (var installerBuild in installerBuildStep)
            {
                var logLines = await client.GetBuildLogLinesAsync(projectName, buildId, installerBuild.Log.Id);
                var signingTimeForLeg = TimeSpan.FromTicks(
                    logLines
                        .Select(line => Regex.Match(line, "-> completed in ([0-9:.]+)"))
                        .Where(match => match.Success)
                        .Select(match => TimeSpan.Parse(match.Groups[1].Value))
                        .Sum(span => span.Ticks));

                if (installerBuildSigningTime == null || installerBuildSigningTime.Value < signingTimeForLeg)
                {
                    installerBuildSigningTime = signingTimeForLeg;
                }
            }

            var singingValidationJob = timeline.Records.First(r => r.Name == "Signing Validation" && r.RecordType == "Job");

            signingTime += singingValidationJob.GetDuration();

            return (signingTime + installerBuildSigningTime).Value;
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
