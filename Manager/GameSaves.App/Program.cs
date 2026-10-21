using Avalonia;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.Sync;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace GameSaves.App
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static int Main(string[] args) =>
            RunScheduledJob(args) ?? BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        internal const string RunJobSwitch = "--run-job";
        internal const string RunSyncSwitch = "--run-sync";

        /// <summary>The arguments a scheduled backup's Windows task passes to this program.</summary>
        internal static string RunJobArguments(Guid jobId) => $"{RunJobSwitch} {jobId:D}";

        /// <summary>The arguments a scheduled sync's Windows task passes to this program.</summary>
        internal static string RunSyncArguments(Guid jobId) => $"{RunSyncSwitch} {jobId:D}";

        /// <summary>
        /// <c>GameSaves.App.exe --run-job &lt;id&gt;</c> runs one scheduled backup
        /// and <c>--run-sync &lt;id&gt;</c> one scheduled sync, without a window,
        /// and returns the <see cref="ScheduledBackupOutcome"/> as the exit
        /// code; 1 is a malformed command line. Any other command line returns
        /// null and the window opens as usual.
        /// </summary>
        internal static int? RunScheduledJob(
            string[] args,
            Action<IServiceCollection>? overrides = null)
        {
            if (args.Length == 0)
                return null;

            bool backup = string.Equals(args[0], RunJobSwitch, StringComparison.OrdinalIgnoreCase);

            if (!backup && !string.Equals(args[0], RunSyncSwitch, StringComparison.OrdinalIgnoreCase))
                return null;

            if (args.Length != 2 || !Guid.TryParse(args[1], out Guid jobId))
                return 1;

            try
            {
                using ServiceProvider services = AppServices.Build(collection =>
                {
                    collection.AddUnattendedSignInGuards();
                    overrides?.Invoke(collection);
                });

                Task<ScheduledBackupOutcome> run = backup
                    ? services.GetRequiredService<ScheduledBackupRunner>().RunAsync(jobId)
                    : services.GetRequiredService<ScheduledSyncRunner>().RunAsync(jobId);

                return (int)run.GetAwaiter().GetResult();
            }
            catch
            {
                // Composition itself failed (for example an unreadable
                // database); there is nothing to record into, so the exit
                // code is the only report.
                return (int)ScheduledBackupOutcome.Failed;
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
