using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Desensitizer;

// The default result is cancellation. No original values or returned document
// excerpts are placed in visual controls or accessibility properties.
public sealed class RehydratePreviewDialog : Window
{
    public const string RestoreButton = "Restore copy";
    public const string CancelButton = "Cancel";
    public bool RestoreRequested { get; private set; }

    public RehydratePreviewDialog(RehydratePreview preview)
    {
        DialogLayout.Configure(this, "Review token restoration", 560, 340, true);
        var panel = DialogLayout.Panel();
        panel.Children.Add(new TextBlock { Text = "Check before restoring identifying information", FontSize = 17,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var summary = new TextBlock { Text = preview.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(summary, "Token restoration counts");
        panel.Children.Add(summary);
        if (preview.Warning.Length > 0) panel.Children.Add(DialogLayout.Note(preview.Warning, 12));
        panel.Children.Add(DialogLayout.Note("The returned file stays unchanged. The restored copy contains identifying information and must remain private.", 12));

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = CancelButton, IsCancel = true, MinHeight = 36, MinWidth = 90,
            Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(cancel, CancelButton);
        cancel.Click += (_, _) => DialogResult = false;
        var restore = DialogLayout.Primary(RestoreButton);
        restore.Margin = new Thickness(0);
        restore.Padding = new Thickness(12, 0, 12, 0);
        restore.IsEnabled = preview.CanRestore;
        restore.IsDefault = preview.CanRestore;
        AutomationProperties.SetName(restore, RestoreButton);
        restore.Click += (_, _) => { if (!preview.CanRestore) return; RestoreRequested = true; DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(restore); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { if (preview.CanRestore) restore.Focus(); else cancel.Focus(); };
    }
}
