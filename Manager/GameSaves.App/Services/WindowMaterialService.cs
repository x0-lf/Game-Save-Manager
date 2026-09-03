using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GameSaves.App.Services
{
    /// <summary>
    /// Applies the requested window material (acrylic or mica) to every
    /// window that registers with it. The material is only ever allowed to
    /// replace the page background once the platform confirms it is actually
    /// compositing (<see cref="TopLevel.ActualTransparencyLevel"/>): until
    /// then, and again the moment a confirmation is withdrawn (older
    /// Windows, RDP, battery saver), <see cref="ThemeService"/> keeps the
    /// fully opaque page background, so a denied material can never leave a
    /// transparent window with nothing behind it. The window template's own
    /// transparency fallback border is additionally pinned to the always
    /// opaque page colour while a material is requested.
    /// </summary>
    public sealed class WindowMaterialService
    {
        private readonly ThemeService _themeService;
        private readonly List<TrackedWindow> _windows = new();
        private AppUiSettings? _settings;
        private bool _materialConfirmed;

        public WindowMaterialService(ThemeService themeService)
        {
            _themeService = themeService ??
                throw new ArgumentNullException(nameof(themeService));
        }

        /// <summary>
        /// The material that should run for these settings. High contrast
        /// always forces "none" (accessibility beats aesthetics), and so do
        /// unknown or malformed values.
        /// </summary>
        internal static string EffectiveMaterial(AppUiSettings settings)
        {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));

            return settings.Accessibility.HighContrast ||
                    !AppUiSettings.IsWindowMaterial(settings.WindowMaterial)
                ? AppUiSettings.MaterialNone
                : settings.WindowMaterial;
        }

        /// <summary>
        /// The Avalonia transparency level a material maps to. "none" (and
        /// anything unknown) requests no platform transparency.
        /// </summary>
        internal static WindowTransparencyLevel TransparencyLevelForMaterial(
            string material) => material switch
        {
            AppUiSettings.MaterialAcrylic => WindowTransparencyLevel.AcrylicBlur,
            AppUiSettings.MaterialMica => WindowTransparencyLevel.Mica,
            _ => WindowTransparencyLevel.None,
        };

        /// <summary>
        /// Stores the current settings and re-syncs every tracked window's
        /// hint and fallback, then re-evaluates whether the material is
        /// actually compositing. Called at startup and on every settings
        /// change.
        /// </summary>
        public void Apply(AppUiSettings settings)
        {
            _settings = settings ??
                throw new ArgumentNullException(nameof(settings));

            WindowTransparencyLevel requested =
                TransparencyLevelForMaterial(EffectiveMaterial(settings));

            foreach (TrackedWindow tracked in _windows.ToArray())
                ApplyToWindow(tracked.Window, requested);

            Evaluate();
        }

        /// <summary>
        /// Registers a window (the main window at startup, each detached
        /// window at creation) and gives it the current material hint. The
        /// hint set before the window is shown still reaches the platform:
        /// the property forwards to the platform implementation as soon as
        /// it is assigned.
        /// </summary>
        public void Attach(Window window)
        {
            if (window is null)
                throw new ArgumentNullException(nameof(window));

            if (_windows.Exists(tracked => tracked.Window == window))
                return;

            _windows.Add(new TrackedWindow(this, window));

            if (_settings is { } settings)
            {
                ApplyToWindow(
                    window,
                    TransparencyLevelForMaterial(EffectiveMaterial(settings)));
            }

            Evaluate();
        }

        private void ApplyToWindow(Window window, WindowTransparencyLevel requested)
        {
            // The hint is a fallback list; a single entry lets the platform
            // take it or fall back to None entirely on its own.
            window.TransparencyLevelHint =
                requested == WindowTransparencyLevel.None
                    ? Array.Empty<WindowTransparencyLevel>()
                    : new[] { requested };

            if (requested == WindowTransparencyLevel.None)
            {
                // Restore the shipped default fallback so the pre-material
                // translucency behaviour is bit-for-bit what it was.
                window.ClearValue(TopLevel.TransparencyBackgroundFallbackProperty);
            }
            else if (window.TryFindResource(
                    ThemeService.PageBackgroundOpaqueBrushKey,
                    window.ActualThemeVariant,
                    out object? fallback) &&
                fallback is ISolidColorBrush fallbackBrush)
            {
                window.TransparencyBackgroundFallback = fallbackBrush;
            }
        }

        internal static bool IsRequestedLevelActive(
            WindowTransparencyLevel requested,
            WindowTransparencyLevel actual,
            bool hasPlatformHandle) =>
            hasPlatformHandle &&
            requested != WindowTransparencyLevel.None &&
            actual == requested;

        // The material is active only when every shown window reports the
        // exact level requested. A denied or substituted level keeps every
        // window on the same safe opaque fallback instead of making attached
        // and detached windows disagree.
        private void Evaluate()
        {
            if (_settings is not { } settings)
                return;

            WindowTransparencyLevel requested =
                TransparencyLevelForMaterial(EffectiveMaterial(settings));

            TrackedWindow[] shown = _windows
                .Where(tracked => tracked.Window.TryGetPlatformHandle() is not null)
                .ToArray();

            bool confirmed =
                shown.Length > 0 &&
                shown.All(tracked => IsRequestedLevelActive(
                    requested,
                    tracked.Window.ActualTransparencyLevel,
                    hasPlatformHandle: true));

            if (confirmed == _materialConfirmed)
                return;

            _materialConfirmed = confirmed;
            _themeService.SetWindowMaterialActive(confirmed);
            _themeService.Apply(settings);
        }

        private void Detach(TrackedWindow tracked)
        {
            _windows.Remove(tracked);
            Evaluate();
        }

        private sealed class TrackedWindow
        {
            private readonly WindowMaterialService _owner;

            public TrackedWindow(WindowMaterialService owner, Window window)
            {
                _owner = owner;
                Window = window;

                // ActualTransparencyLevel is raised by the platform callback
                // whenever the achieved level changes (grant, deny, revoke),
                // which is exactly when the surface decision must be redone.
                Window.PropertyChanged += OnWindowPropertyChanged;
                Window.Opened += OnWindowOpened;
                Window.Closed += OnWindowClosed;
            }

            public Window Window { get; }

            private void OnWindowPropertyChanged(
                object? sender,
                AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == TopLevel.ActualTransparencyLevelProperty)
                    _owner.Evaluate();
            }

            private void OnWindowOpened(object? sender, EventArgs e) =>
                _owner.Evaluate();

            private void OnWindowClosed(object? sender, EventArgs e)
            {
                Window.PropertyChanged -= OnWindowPropertyChanged;
                Window.Opened -= OnWindowOpened;
                Window.Closed -= OnWindowClosed;
                _owner.Detach(this);
            }
        }
    }
}
