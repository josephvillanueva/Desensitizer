using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desensitizer;

static class TutorialTests
{
    public static void Run(CheckFn check, string work)
    {
        var folder = Path.Combine(work, "tutorial-settings");
        AppSettingsStore.UseFolder(folder);
        try
        {
            check("tutorial: first launch defaults to unseen", !AppSettingsStore.Load().TutorialSeen);
            AppSettingsStore.Save(new AppSettings { TutorialSeen = true, TextScalePercent = 150 });
            var saved = AppSettingsStore.Load();
            check("tutorial: seen flag survives restart", saved.TutorialSeen);
            check("tutorial: saving seen flag retains font size", saved.TextScalePercent == 150);
            check("tutorial: eight distinct spotlight targets", TutorialGuide.Steps.Count == 8 && TutorialGuide.Steps.Select(s => s.Target).Distinct().Count() == 8);
            check("tutorial: export instructions explain retries and incomplete checks", TutorialGuide.Steps[3].Description.Contains("Zero Review") && TutorialGuide.Steps[5].Description.Contains("cannot bypass"));
            check("tutorial: copy protects business rules and tables", TutorialGuide.Steps[1].Description.Contains("business rules and table context"));
            check("tutorial: no em dashes in new copy", TutorialGuide.Steps.All(s => !(s.Title + s.Description).Contains('\u2014')));
            var results = new List<(string, bool)>();
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Window? host = null; TutorialWindow? tour = null;
                try
                {
                    var root = new Grid();
                    var button = new Button { Name = "SettingsButton", Content = "Settings", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), Width = 90, Height = 40 };
                    root.Children.Add(button);
                    host = new Window { Title = "Fictional tutorial test", Content = root, Width = 650, Height = 550, ShowInTaskbar = false };
                    NameScope.SetNameScope(host, new NameScope());
                    host.RegisterName("SettingsButton", button); host.Show(); host.UpdateLayout();
                    tour = new TutorialWindow(host, 150); tour.Show(); tour.UpdateLayout();
                    results.Add(("tutorial: opens at first step", tour.StepIndex == 0));
                    results.Add(("tutorial: unavailable controls have no invented spotlight", tour.SpotlightBounds.IsEmpty));
                    tour.Move(-1); results.Add(("tutorial: Back cannot move below zero", tour.StepIndex == 0));
                    tour.Move(100); tour.UpdateLayout();
                    results.Add(("tutorial: navigation clamps at final step", tour.StepIndex == 7));
                    results.Add(("tutorial: visible target receives a spotlight", !tour.SpotlightBounds.IsEmpty));
                    results.Add(("tutorial: spotlight stays within window", new Rect(0, 0, tour.Width, tour.Height).Contains(tour.SpotlightBounds)));
                    results.Add(("tutorial: guide does not modify underlying button", Equals(button.Content, "Settings")));
                    var card = ((Canvas)((Grid)tour.Content).Children[1]).Children.OfType<Border>().Last();
                    results.Add(("tutorial: card fits at 150 percent on a small window", card.ActualWidth <= tour.Width && card.ActualHeight <= tour.Height));
                    var screenshot = Environment.GetEnvironmentVariable("DESENSITIZER_TUTORIAL_PREVIEW");
                    if (!string.IsNullOrEmpty(screenshot))
                    {
                        var bitmap = new RenderTargetBitmap((int)tour.Width, (int)tour.Height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render((Visual)tour.Content);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(screenshot); png.Save(output);
                    }
                    tour.Close();
                    results.Add(("tutorial: closing without Finish leaves completion false", !tour.Finished));
                    tour = null;
                    results.Add(("tutorial: closing detaches owned window", !host.OwnedWindows.OfType<TutorialWindow>().Any()));
                    host.Close(); host = null;
                }
                catch (Exception ex) { failure = ex; }
                finally { tour?.Close(); host?.Close(); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            foreach (var (name, passed) in results) check(name, passed);
            check("tutorial: window checks complete without exception", failure == null, failure?.ToString());
        }
        finally { AppSettingsStore.UseFolder(null); }
    }
}
