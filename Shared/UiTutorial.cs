using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Desensitizer;

public sealed record TutorialStep(string Target, string Title, string Description);

public static class TutorialGuide
{
    public static IReadOnlyList<TutorialStep> Steps { get; } = Array.AsReadOnly(new[]
    {
        new TutorialStep("ProjectsList", "Start with an encrypted project", "Create or open a project. It keeps your replacement tokens and decisions together. Use a neutral project label and keep the passphrase safe. Only one window can use the same project at a time. Close it in the other app before opening it here. This tour does not change your documents."),
        new TutorialStep("AddFilesButton", "Add your documents", "Add supported files to the project, then select a document in Project files. The original stays unchanged. Names, companies, contact details and identifying values should be tokenized, while business rules and table context should remain readable."),
        new TutorialStep("ScanButton", "Scan locally", "Scan selected document finds potential identifying information on this computer. Findings appear in Mapping review. A finding is a suggestion, not proof that the value is sensitive."),
        new TutorialStep("DeepCheckButton", "Finish the deep check", "Run deep check now reviews context with local AI. If a check stops, use Resume previous check or Retry failed sections. Read the warning if it cannot finish. Zero Review findings does not mean the deep check completed. Older saved progress may be discarded to limit project size; those sections will be checked again."),
        new TutorialStep("MappingsGrid", "Choose what to protect", "Use Replace for identifying information and Keep as-is for ordinary terms, business rules, versions and section numbers. Review means a decision is still needed. Check aliases before assuming that similar names refer to the same person. Verify scores are not proven accuracy."),
        new TutorialStep("DesensitizeButton", "Export the protected copy", "Select a document and set identifying findings to Replace. Export desensitized copy checks the protected file again. If export is blocked, read the warning, finish or retry the check, and inspect any new findings. Hidden or encoded identifying content may require editing the source file or getting support, not repeating the same export. Automatic replacement in Settings can retry new findings, up to five passes, but cannot bypass failed safety checks."),
        new TutorialStep("RehydrateButton", "Keep the project private", "Send only the protected copy to cloud analysis. Do not send the encrypted project, passphrase or original document. Rehydrate first previews recognized and unknown token counts. Review warnings, then choose Restore copy or Cancel. The restored copy contains identifying information and must stay private. Removed pictures cannot be restored by rehydration."),
        new TutorialStep("SettingsButton", "Make the app work for you", "Settings includes text size, performance and export preferences. You can run this tutorial again here at any time. Back and Next move between steps. Skip or Escape closes the tutorial.")
    });
}

// An owned modal window blocks clicks and shortcuts into the real workspace. The spotlight
// is only a visual guide: no sample project, mapping, scan or export is created by the tour.
public sealed class TutorialWindow : Window
{
    private readonly Window host;
    private readonly Grid layout = new();
    private readonly TutorialShade shade = new();
    private readonly Canvas canvas = new();
    private readonly Border card;
    private readonly Border ring = new() { BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(7), IsHitTestVisible = false };
    private readonly TextBlock badge, title, description, location;
    private readonly Button back, next;
    public int StepIndex { get; private set; }
    public Rect SpotlightBounds { get; private set; }
    public bool Finished { get; private set; }

    public TutorialWindow(Window owner, double textScale = 100)
    {
        host = owner; Owner = owner;
        Title = "Getting started tutorial";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var scale = TextScaling.Normalize(textScale) / 100d;
        badge = Text("", 12 * scale, Brushes.RoyalBlue);
        title = Text("", 20 * scale, Brushes.Black); title.FontWeight = FontWeights.SemiBold;
        description = Text("", 14 * scale, Brushes.Black);
        location = Text("", 12 * scale, Brushes.DimGray);
        var body = new StackPanel(); body.Children.Add(badge); body.Children.Add(title); body.Children.Add(description); body.Children.Add(location);
        back = Action("Back", (_, _) => Move(-1));
        next = Action("Next", (_, _) => { if (StepIndex == TutorialGuide.Steps.Count - 1) { Finished = true; Close(); } else Move(1); });
        var skip = Action("Skip tutorial", (_, _) => Close());
        back.FontSize = next.FontSize = skip.FontSize = 13 * scale;
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(skip); buttons.Children.Add(back); buttons.Children.Add(next); body.Children.Add(buttons);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(body, KeyboardNavigationMode.Cycle);
        card = new Border { Background = Brushes.White, BorderBrush = Brushes.RoyalBlue, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(20), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        AutomationProperties.SetName(card, "Guided tutorial");
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Polite);
        layout.Children.Add(shade); layout.Children.Add(canvas); canvas.Children.Add(ring); canvas.Children.Add(card); Content = layout;
        Loaded += (_, _) => { Position(); RenderStep(); next.Focus(); };
        host.LocationChanged += HostMoved; host.SizeChanged += HostSized;
        Closed += (_, _) => { host.LocationChanged -= HostMoved; host.SizeChanged -= HostSized; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private static TextBlock Text(string text, double size, Brush color) => new() { Text = text, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private static Button Action(string label, RoutedEventHandler click)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 6), MinHeight = 36 };
        AutomationProperties.SetName(button, label); button.Click += click; return button;
    }
    private void HostMoved(object? sender, EventArgs e) => Position();
    private void HostSized(object sender, SizeChangedEventArgs e) => Position();
    private void Position()
    {
        if (host.Content is not FrameworkElement root || !root.IsLoaded) return;
        var origin = root.PointToScreen(new Point());
        var fromDevice = PresentationSource.FromVisual(host)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        origin = fromDevice.Transform(origin);
        Left = origin.X; Top = origin.Y; Width = Math.Max(1, root.ActualWidth); Height = Math.Max(1, root.ActualHeight);
        PlaceCard();
    }
    public void Move(int delta)
    {
        StepIndex = Math.Clamp(StepIndex + delta, 0, TutorialGuide.Steps.Count - 1);
        RenderStep(); next.Focus();
    }
    private void RenderStep()
    {
        var step = TutorialGuide.Steps[StepIndex];
        badge.Text = $"GETTING STARTED: {StepIndex + 1} OF {TutorialGuide.Steps.Count}";
        title.Text = step.Title; description.Text = step.Description;
        back.IsEnabled = StepIndex > 0; next.Content = StepIndex == TutorialGuide.Steps.Count - 1 ? "Finish tutorial" : "Next";
        AutomationProperties.SetName(next, next.Content.ToString()); PlaceCard();
    }
    private void PlaceCard()
    {
        SpotlightBounds = Rect.Empty;
        var target = host.FindName(TutorialGuide.Steps[StepIndex].Target) as FrameworkElement;
        if (target?.IsVisible == true && target.ActualWidth > 0 && host.Content is Visual root)
        {
            SpotlightBounds = target.TransformToAncestor(root).TransformBounds(new Rect(target.RenderSize));
            SpotlightBounds.Inflate(5, 5);
            SpotlightBounds = Rect.Intersect(SpotlightBounds, new Rect(0, 0, Width, Height));
        }
        location.Text = SpotlightBounds.IsEmpty ? "This control becomes available after opening a project and selecting a document. You can replay the tour then for a spotlight on each control." : "The highlighted area is where you will use this step after closing the tour.";
        ring.Visibility = SpotlightBounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!SpotlightBounds.IsEmpty)
        {
            Canvas.SetLeft(ring, SpotlightBounds.Left); Canvas.SetTop(ring, SpotlightBounds.Top);
            ring.Width = SpotlightBounds.Width; ring.Height = SpotlightBounds.Height;
        }
        shade.Hole = SpotlightBounds; shade.InvalidateVisual();
        card.Width = Math.Max(1, Math.Min(470, Width - 32)); card.MaxHeight = Math.Max(1, Height - 32);
        card.Measure(new Size(card.Width, card.MaxHeight));
        var h = Math.Min(card.MaxHeight, card.DesiredSize.Height);
        var x = Math.Max(16, (Width - card.Width) / 2); var y = Math.Max(16, (Height - h) / 2);
        if (!SpotlightBounds.IsEmpty)
        {
            if (SpotlightBounds.Right + card.Width + 32 <= Width) x = SpotlightBounds.Right + 16;
            else if (SpotlightBounds.Left >= card.Width + 32) x = SpotlightBounds.Left - card.Width - 16;
            else if (SpotlightBounds.Bottom + h + 32 <= Height) y = SpotlightBounds.Bottom + 16;
            else if (SpotlightBounds.Top >= h + 32) y = SpotlightBounds.Top - h - 16;
        }
        Canvas.SetLeft(card, x); Canvas.SetTop(card, y);
    }
    private sealed class TutorialShade : FrameworkElement
    {
        public Rect Hole { get; set; } = Rect.Empty;
        protected override void OnRender(DrawingContext dc)
        {
            Geometry area = new RectangleGeometry(new Rect(RenderSize));
            if (!Hole.IsEmpty) area = new CombinedGeometry(GeometryCombineMode.Exclude, area, new RectangleGeometry(Hole, 7, 7));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(165, 15, 23, 42)), null, area);
        }
    }
}

public partial class MainWindow
{
    private bool tutorialOpen;
    private void InitializeTutorial()
    {
        ContentRendered += (_, _) => { if (!_settings.TutorialSeen) StartTutorial(); };
    }
    private void StartTutorial()
    {
        if (Busy || tutorialOpen) return;
        tutorialOpen = true;
        var focus = Keyboard.FocusedElement;
        try
        {
            new TutorialWindow(this, _settings.TextScalePercent).ShowDialog();
            _settings.TutorialSeen = true;
            try { AppSettingsStore.Save(_settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { StatusText.Text = "Tutorial closed. Its preference could not be saved, so it may appear again next time."; }
        }
        finally { tutorialOpen = false; if (focus is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus(); }
    }
}
