using GameSaves.Infrastructure.Sync;

namespace GameSaves.Tests;

/// <summary>
/// Milestone X Task 4. Retry has to happen in exactly one place before a
/// server-supplied retry instruction means anything: an instructed delay
/// honoured by one layer while another layer retries underneath it is not an
/// instructed delay, it is two.
/// </summary>
public sealed class RetryAuthorityTests
{
    [Fact]
    public void EveryDriveServiceInitializer_DisablesTheLibraryBackoff()
    {
        (string Name, string Text)[] initializers = DriveSources()
            .Where(source => source.Text.Contains(
                "new BaseClientService.Initializer", StringComparison.Ordinal))
            .ToArray();

        // Non-vacuity: the scan found the real construction sites. Nine, counted
        // from the repository rather than estimated; the first estimate was five.
        Assert.Equal(9, initializers.Length);

        foreach ((string name, string text) in initializers)
        {
            int constructions = Occurrences(text, "new BaseClientService.Initializer");
            int pins = Occurrences(
                text, "DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None");

            Assert.True(
                constructions == pins,
                $"{name} builds {constructions} Drive service initializer(s) " +
                $"but pins the backoff policy {pins} time(s)");
        }
    }

    [Fact]
    public void TheRetryBound_IsTheOnlyBoundThatApplies()
    {
        // With the library backoff disabled, everything a failing remote call
        // may spend waiting is what the decorator asked for, so the published
        // ceiling is the real one rather than the decorator's share of it.
        Assert.Equal(TimeSpan.FromSeconds(30), RetryingRemoteFileSystem.MaximumTotalDelay);
        Assert.Equal(4, RetryingRemoteFileSystem.DefaultMaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), RetryingRemoteFileSystem.DefaultBaseDelay);
    }

    [Fact]
    public void NoServerSuppliedRetryInstruction_IsCapturedAnywhere()
    {
        // This is a finding pinned as a test rather than a defect fixed here.
        //
        // Honouring a server-supplied retry instruction needs the Retry-After
        // response header, and nothing in this repository reads it. The failure
        // mapper is handed a GoogleApiException and takes only the status code
        // and a safe reason string from it; the header never reaches that far,
        // and the failure record has nowhere to carry a delay even if it did.
        //
        // Building it means observing the HTTP response across all nine Drive
        // service constructions and carrying the observed delay to the point
        // where the decorator decides how long to wait. That is its own task,
        // and Milestone X did not do it.
        //
        // When it is built, rewrite this test to pin where the instruction is
        // captured and how it reaches the decorator. Do not delete it.
        string[] mentions = DriveSources()
            .Concat(SyncSources())
            .Where(source =>
                source.Text.Contains("RetryAfter", StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains("Retry-After", StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Name)
            .ToArray();

        Assert.Empty(mentions);

        // Non-vacuity: the same scan does find the retry that exists, so an
        // empty result above is an absence rather than a scan that reads
        // nothing.
        Assert.Contains(
            SyncSources(),
            source => source.Text.Contains(
                "RetryingRemoteFileSystem", StringComparison.Ordinal));
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyList<(string Name, string Text)> DriveSources() =>
        Sources("GameSaves.Infrastructure", "GoogleDrive");

    private static IReadOnlyList<(string Name, string Text)> SyncSources() =>
        Sources("GameSaves.Infrastructure", "Sync");

    private static IReadOnlyList<(string Name, string Text)> Sources(
        string projectName,
        string folderName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Manager.sln")))
            {
                string folder = Path.Combine(
                    directory.FullName, projectName, folderName);

                return Directory
                    .EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
                    .ToArray();
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Manager.sln was not found.");
    }
}
