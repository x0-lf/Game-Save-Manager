using Avalonia;
using GameSaves.Infrastructure.DependencyInjection;
using GameSaves.Infrastructure.Transfers;
using Microsoft.Extensions.DependencyInjection;
using System;

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

        /// <summary>
        /// <c>GameSaves.App.exe --run-job &lt;id&gt;</c> runs one scheduled backup
        /// without a window and returns its <see cref="ScheduledBackupOutcome"/>
        /// as the exit code; 1 is a malformed command line. Any other command
        /// line returns null and the window opens as usual.
        /// </summary>
        internal static int? RunScheduledJob(
            string[] args,
            Action<IServiceCollection>? overrides = null)
        {
            if (args.Length == 0 ||
                !string.Equals(args[0], RunJobSwitch, StringComparison.OrdinalIgnoreCase))
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

                return (int)services.GetRequiredService<ScheduledBackupRunner>()
                    .RunAsync(jobId)
                    .GetAwaiter()
                    .GetResult();
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
