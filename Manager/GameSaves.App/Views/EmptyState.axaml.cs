using Avalonia;
using Avalonia.Controls;

namespace GameSaves.App.Views
{
    public partial class EmptyState : UserControl
    {
        public static readonly StyledProperty<string> GlyphProperty =
            AvaloniaProperty.Register<EmptyState, string>(nameof(Glyph), "");

        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<EmptyState, string>(nameof(Title), string.Empty);

        public static readonly StyledProperty<string> MessageProperty =
            AvaloniaProperty.Register<EmptyState, string>(nameof(Message), string.Empty);

        public static readonly StyledProperty<object?> ActionContentProperty =
            AvaloniaProperty.Register<EmptyState, object?>(nameof(ActionContent));

        public EmptyState()
        {
            InitializeComponent();

            GlyphText.Text = Glyph;
            TitleText.Text = Title;
            MessageText.Text = Message;
        }

        public string Glyph
        {
            get => GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Message
        {
            get => GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public object? ActionContent
        {
            get => GetValue(ActionContentProperty);
            set => SetValue(ActionContentProperty, value);
        }

        protected override void OnPropertyChanged(
            AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == GlyphProperty)
                GlyphText.Text = Glyph;
            else if (change.Property == TitleProperty)
                TitleText.Text = Title;
            else if (change.Property == MessageProperty)
                MessageText.Text = Message;
            else if (change.Property == ActionContentProperty)
                ActionPresenter.Content = ActionContent;
        }
    }
}
