using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GameSaves.App.Views.Workspace;

namespace GameSaves.Tests;

/// <summary>
/// The app froze on start with a Steam library of five games. To find the
/// page's minimum height, the page host measured the Installed games table a
/// second time, squeezed to its 200px minimum. Five rows needed a scrollbar at
/// that height and none at full height, so every pass flipped the scrollbar,
/// the flip invalidated the table, and layout never finished (reproduced on
/// the headless platform at 1280x560: 120 flips in five frames before the
/// fix, none after). A filling panel now answers that probe without measuring
/// its content, so the content is only ever measured at its real size.
/// </summary>
[Collection(nameof(AvaloniaDispatcherCollection))]
public sealed class WorkspaceLayoutConvergenceTests
{
    [Fact]
    public void AProbedPanel_ReportsItsMinimum_WithoutMeasuringItsContent()
    {
        var content = new CountingContent(height: 300);
        WorkspacePanel panel = Panel(content, minHeight: 200);

        panel.Measure(new Size(400, 500));
        Assert.Equal(1, content.Measures);
        Assert.Equal(300, panel.DesiredSize.Height);

        panel.IsProbingMinimumHeight = true;
        panel.Measure(new Size(400, double.PositiveInfinity));
        panel.IsProbingMinimumHeight = false;

        Assert.Equal(1, content.Measures);
        Assert.Equal(200, panel.DesiredSize.Height);

        // Back at its real size the content is not measured again: its
        // constraint never changed, so nothing about it can flip.
        panel.Measure(new Size(400, 500));
        Assert.Equal(1, content.Measures);
        Assert.Equal(300, panel.DesiredSize.Height);
    }

    [Fact]
    public void AProbedPanel_WhoseContentNeedsLessThanItsMinimum_ReportsWhatTheContentNeeds()
    {
        var content = new CountingContent(height: 62);
        WorkspacePanel panel = Panel(content, minHeight: 200);

        panel.Measure(new Size(400, 500));
        panel.IsProbingMinimumHeight = true;
        panel.Measure(new Size(400, double.PositiveInfinity));

        Assert.Equal(62, panel.DesiredSize.Height);
        Assert.Equal(1, content.Measures);
    }

    // The content stands in as the whole template, so it is the panel's one
    // visual child and is measured exactly when the panel measures its body.
    private static WorkspacePanel Panel(Control content, double minHeight) =>
        new()
        {
            MinPanelHeight = minHeight,
            SizeMode = WorkspacePanelSizeMode.Fill,
            Template = new FuncControlTemplate<WorkspacePanel>((_, _) => content)
        };

    private sealed class CountingContent(double height) : Control
    {
        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return new Size(100, height);
        }
    }
}
