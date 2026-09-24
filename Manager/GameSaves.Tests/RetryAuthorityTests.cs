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
    public void ServerSuppliedRetryInstruction_IsHonouredOnlyWhereItIsCarried()
    {
        // Google Drive does not capture Retry-After: the header never reaches
        // the failure mapper (an AsyncLocal attempt at it never delivered and
        // was removed). The provider-neutral decorator honours a delay only
        // when the failing exception itself carries one, which OneDrive's
        // client does. When Drive gains a real capture path, add it here.
        static bool Mentions(string text) =>
            text.Contains("RetryAfter", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Retry-After", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(DriveSources(), source => Mentions(source.Text));
        Assert.Equal(
            new[] { "RetryingRemoteFileSystem.cs" },
            SyncSources().Where(source => Mentions(source.Text)).Select(source => source.Name));

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
