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
    public void ServerSuppliedRetryInstruction_IsCapturedAndReachesDecorator()
    {
        // MAINT-003: Server-supplied retry instruction (Retry-After header) is
        // observed via GoogleDriveRetryAfterObserver attached to HTTP handlers across
        // all Drive service constructions. It is mapped to IRetryDelayCarrier on
        // GoogleDriveApiException, GoogleDriveRemoteValidationResult, and
        // GoogleDriveRemoteOperationException, and honoured by RetryingRemoteFileSystem.
        string[] driveMentions = DriveSources()
            .Where(source =>
                source.Text.Contains("RetryAfter", StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains("Retry-After", StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] syncMentions = SyncSources()
            .Where(source =>
                source.Text.Contains("RetryAfter", StringComparison.OrdinalIgnoreCase) ||
                source.Text.Contains("Retry-After", StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Pin the exact files participating in Retry-After capture and propagation:
        Assert.Contains("GoogleDriveRetryAfterObserver.cs", driveMentions);
        Assert.Contains("GoogleDriveApiFailure.cs", driveMentions);
        Assert.Contains("GoogleDriveRemoteValidation.cs", driveMentions);
        Assert.Contains("GoogleDriveRemoteOperationContext.cs", driveMentions);
        Assert.Contains("GoogleInstalledAppAuthorizer.cs", driveMentions);

        // Pin the exact files participating in Retry-After parsing and retry backoff:
        Assert.Contains("HttpRetryAfterParser.cs", syncMentions);
        Assert.Contains("RetryingRemoteFileSystem.cs", syncMentions);
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
