using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace GameSaves.Tests
{
    [CollectionDefinition(nameof(ProcessExitCodeCollection), DisableParallelization = true)]
    public sealed class ProcessExitCodeCollection
    {
    }

    /// <summary>
    /// Catalog/harvest CLI commands must reject bad arguments with exit code 2 (audit §3.20) before
    /// touching the database or the network. Environment.ExitCode is process-wide, so these tests
    /// run alone and always restore it.
    /// </summary>
    [Collection(nameof(ProcessExitCodeCollection))]
    public sealed class CatalogCliArgumentTests
    {
        [Theory]
        [InlineData("tracklist", "--ouput", "missing.csv")]
        [InlineData("tracklist", "missing.csv")]
        [InlineData("tracklist", "-o")]
        [InlineData("tracklist", "-n", "abc")]
        [InlineData("tracklist", "--limit", "-5")]
        [InlineData("tracklist", "--format", "xml")]
        [InlineData("tracklist", "--status", "Bogus")]
        [InlineData("tracklist", "--status", "7")]
        [InlineData("tracklist", "--min-priority", "Urgent")]
        [InlineData("pcgw-harvest-tracklist", "missing.json", "out", "Agent/1.0", "1O")]
        [InlineData("pcgw-harvest-installed", "out", "Agent/1.0", "ten")]
        public async Task InvalidArguments_ExitWithUsageError(params string[] args)
        {
            Assert.Equal(2, await RunAsync(args));
        }

        [Theory]
        [InlineData("abc", "--save-db")]
        [InlineData("--output", "savepaths.json")]
        [InlineData("--save-db")]
        [InlineData("123", "Name", "Extra")]
        [InlineData("123", "--bogus")]
        [InlineData("123", "--output")]
        public async Task AiDetect_InvalidArguments_ExitWithUsageError(params string[] tail)
        {
            string gameDir = Path.Combine(Path.GetTempPath(), "gsm_cli_ai_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(gameDir);
            try
            {
                string[] args = new string[tail.Length + 2];
                args[0] = "ai-detect";
                args[1] = gameDir;
                tail.CopyTo(args, 2);

                Assert.Equal(2, await RunAsync(args));
            }
            finally
            {
                Directory.Delete(gameDir, recursive: true);
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            Environment.ExitCode = 0;
            try
            {
                await Program.Main(args);
                return Environment.ExitCode;
            }
            finally
            {
                Environment.ExitCode = 0;
            }
        }
    }
}
