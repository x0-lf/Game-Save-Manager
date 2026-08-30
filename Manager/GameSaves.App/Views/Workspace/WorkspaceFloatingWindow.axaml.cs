using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace GameSaves.App.Views.Workspace
{
    /// <summary>
    /// A floated panel's own window. Deliberately the same shape and contract
    /// as <see cref="Views.DetachedWindow"/>, which a detached rail tab
    /// already gets: same page background, Escape closes, and closing puts the
    /// content back where it came from. A user who has floated a tab and a user
    /// who has floated a section learn one behaviour, not two.
    /// </summary>
    public partial class WorkspaceFloatingWindow : Window
    {
        public WorkspaceFloatingWindow()
        {
            InitializeComponent();

            foreach (KeyBinding binding in KeyBindings)
                binding.Command = new RelayCommand(() => Close());

            // Created by the view layer without DI access, so it registers with
            // the ambient material service itself — same as DetachedWindow.
            App.CurrentWindowMaterial?.Attach(this);
        }

        /// <summary>Raised when the user closes the window; the surface docks the panel back.</summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Placement in DIPs on the virtual desktop. The platform position is
        /// in device pixels, so both directions convert through the current
        /// render scaling — the same conversion the detached tab windows use.
        /// </summary>
        public Rect PlacementBounds
        {
            get
            {
                double scale = RenderScaling;
                PixelPoint position = Position;

                return new Rect(position.X / scale, position.Y / scale, Width, Height);
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
            // Only a close the user performed docks the panel back. An
            // application, OS or owner shutdown must leave the layout saying
            // "floating", or every floated section would silently re-dock on
            // the way out and the next launch would lose the arrangement.
            if (e.CloseReason == WindowCloseReason.WindowClosing)
                CloseRequested?.Invoke(this, EventArgs.Empty);

            base.OnClosing(e);
        }
    }
}
