using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace GameSaves.App.Views
{
    // Floating host for a detached navigation tab. The content control is
    // reparented (never recreated) so view state survives both directions.
    // Closing is surfaced through TabDetachCoordinator via CloseRequested;
    // the coordinator decides whether to reattach based on why the window
    // is closing (user close reattaches, application shutdown does not).
    internal partial class DetachedWindow : Window, IDetachedTabWindow
    {
        public DetachedWindow()
        {
            InitializeComponent();

            // The Escape gesture is declared in DetachedWindow.axaml; closing
            // through it runs the same path as the title-bar close button.
            foreach (KeyBinding binding in KeyBindings)
                binding.Command = new RelayCommand(() => Close());

            // Detached windows are created by the view layer without DI
            // access, so they register with the ambient material service
            // themselves; a hint set here still reaches the platform before
            // the window is shown.
            App.CurrentWindowMaterial?.Attach(this);
        }

        public event EventHandler? CloseRequested;

        void IDetachedTabWindow.Show(Window? owner) => Show(owner!);

        // Bounds are stored in DIPs; the platform position is in device
        // pixels, so both directions convert through the current render
        // scaling. An explicit placement (workspace layout apply) replaces
        // the CenterOwner startup location before the window is shown.
        Rect IDetachedTabWindow.Bounds
        {
            get
            {
                double scale = RenderScaling;
                PixelPoint position = Position;

                return new Rect(
                    position.X / scale,
                    position.Y / scale,
                    Width,
                    Height);
            }
            set
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Width = value.Width;
                Height = value.Height;

                double scale = RenderScaling;
                Position = new PixelPoint(
                    (int)Math.Round(value.X * scale),
                    (int)Math.Round(value.Y * scale));
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            base.OnClosing(e);
        }
    }
}
