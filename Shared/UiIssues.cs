using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Desensitizer;

public enum IssueKind { NoDocument, ScanRequired, NoReplacements, DeepRequired, ScanIncomplete, DeepIncomplete, ExportIncomplete, ResidualFindings, SourceChanged, MissingFile, ModelUnavailable, ContextMismatch, InvalidPlaceholder, SaveFailed, ExportFailed, RehydrateFailed, ProjectOpenFailed, ProjectCreateFailed, UnsupportedFile, ReadyToExport, ProjectAlreadyOpen, ProjectAccessFailed, HiddenContent }
public enum IssueAction { SelectDocument, Scan, DeepCheck, Export, Review, ReviewFindings, Settings, AddFiles, AppFolder, SaveProject, Rehydrate, OpenProject, CreateProject, Support }
public sealed record UserIssue(IssueKind Kind, string Title, string Explanation, string NextStep, string Button, IssueAction Action);

// Fixed, privacy-safe explanations. Never display an arbitrary exception or model response here.
public static class UiIssues
{
    public static UserIssue For(IssueKind kind) => kind switch
    {
        IssueKind.HiddenContent => New(kind,"Hidden identifying content blocked export","An identifying value selected for replacement remains in hidden or encoded document content. No protected copy was released.","Do not repeat the same export or disable checks. Ask support to help remove the hidden content from a separate source copy, then add and scan that copy. Copy support info to begin; it contains no document text.","Copy support info",IssueAction.Support),
        IssueKind.ProjectAlreadyOpen => New(kind,"Project already open elsewhere","Another window is using this encrypted project. Opening a second writable copy could overwrite its decisions.","Close this project in the other Desensitizer or AI Desensitizer window first, then retry. Do not delete its lock file or create a second copy to bypass this protection.","Try opening again",IssueAction.OpenProject),
        IssueKind.ProjectAccessFailed => New(kind,"Project storage is unavailable","The app could not obtain writable access to the project location. This is not proof that another window has the project open.","Check that the drive is connected and the project folder is writable, with free space available. Ask your administrator about folder access if needed, then retry opening the original project.","Try opening again",IssueAction.OpenProject),
        IssueKind.ReadyToExport => New(kind,"Ready for the final safety check","The initial requirements are met. The export still needs its final safety checks.","Export the protected copy, choose a separate output path and let the final checks finish. Inspect the result before sharing.","Export protected copy",IssueAction.Export),
        IssueKind.NoDocument => New(kind,"Select a document first","There is no selected document to export.","Open a project, add your file if needed, then select it in Project files.","Select document",IssueAction.SelectDocument),
        IssueKind.ScanRequired => New(kind,"Scan needed before export","This document has not completed its initial scan.","Scan the selected document, then review the identifying values.","Scan document",IssueAction.Scan),
        IssueKind.NoReplacements => New(kind,"Choose what to replace","No usable identifying values are currently set to Replace.","Review the mappings and set real identifying information to Replace. Keep business rules and generic terms unchanged. Do not replace everything just to enable export.","Review mappings",IssueAction.Review),
        IssueKind.DeepRequired => New(kind,"Finish the deep check","Automatic deep check is on, but this document's check is incomplete. Zero Review findings does not mean it finished.","Resume the saved check or retry the failed sections. If a warning returns, follow its recommended action.","Resume or run deep check",IssueAction.DeepCheck),
        IssueKind.ScanIncomplete => New(kind,"The local scan did not finish","One or more local detector checks did not complete. Findings already found are retained, but the scan is not verified.","Close other heavy applications and retry the scan. If it fails again, copy support info and check the installed detector files.","Retry scan",IssueAction.Scan),
        IssueKind.DeepIncomplete => New(kind,"The deep check did not finish","Some local AI sections could not be reviewed. Existing decisions and completed sections are retained when available.","Resume or retry the failed sections. Keep the app open while it checks. A repeated failure needs investigation, not repeated blind retries.","Retry failed sections",IssueAction.DeepCheck),
        IssueKind.ExportIncomplete => New(kind,"The final safety check did not finish","No protected copy was released because a safety check was incomplete. This can happen even with zero Review findings.","Retry export to reuse saved successful sections where available. If it fails again, copy support info instead of repeating the same loop.","Retry export",IssueAction.Export),
        IssueKind.ResidualFindings => New(kind,"New findings need a decision","The protected copy still contained identifying-looking content. No file was exported. The remaining findings were placed in Review.","Inspect each finding in context and choose Replace or Keep as-is. Then export again. If automatic replacement stops at its limit, resolve those findings rather than restarting it unchanged.","Review findings",IssueAction.ReviewFindings),
        IssueKind.SourceChanged => New(kind,"The source document changed","The document no longer matches the version that was checked. No new export is approved.","Scan the current document again. Existing decisions are retained for matching values, but new or changed content needs review.","Rescan document",IssueAction.Scan),
        IssueKind.MissingFile => New(kind,"The source file cannot be read","The file may have been moved, removed, locked or made unavailable.","Restore it to its original location, or add the accessible copy and scan it. Adding a new file does not remove the old reference.","Add accessible file",IssueAction.AddFiles),
        IssueKind.ModelUnavailable => New(kind,"A local engine is unavailable","A required model or detector could not start, or its installation is missing or invalid.","Check that the portable folder was fully extracted, including analyzer and model-packs. Do not use only the executable. Reinstall the matching package if files are missing. Never disable safety checks just to bypass this error.","Open app folder",IssueAction.AppFolder),
        IssueKind.ContextMismatch => New(kind,"The review context is inconsistent","The local reviewer could not match the section to its table context. This is an application issue, not a mapping decision you need to guess.","Copy privacy-safe support info and provide the app version to support. Use the corrected release before retrying. Do not keep changing findings to bypass this failure.","Copy support info",IssueAction.Support),
        IssueKind.InvalidPlaceholder => New(kind,"Picture placeholder needs correction","The placeholder is too long, token-shaped or otherwise invalid.","Open Settings and use short neutral text for removed pictures, or leave the placeholder empty.","Open settings",IssueAction.Settings),
        IssueKind.SaveFailed => New(kind,"Your changes could not be saved","The encrypted project could not be written. Keep this window open so unsaved decisions are not lost.","Check storage space and write permission, reconnect the project drive and close anything locking the project. Then retry saving.","Retry saving",IssueAction.SaveProject),
        IssueKind.ExportFailed => New(kind,"Export could not complete","A file, validation or write error prevented completion. Do not share any partially written output.","Check free space, close the destination in Word or another application, and choose a writable output location when retrying. Repeated validation errors need support investigation.","Retry export",IssueAction.Export),
        IssueKind.RehydrateFailed => New(kind,"The returned file could not be restored","Token restoration did not complete.","Use the encrypted project that created the tokens. Select a supported, accessible returned file and a different writable output path.","Retry rehydration",IssueAction.Rehydrate),
        IssueKind.ProjectOpenFailed => New(kind,"The project could not be opened","The passphrase, project file or storage access could not be verified.","Try opening the correct encrypted project with its original passphrase. Check that its drive is available. Do not delete or overwrite it; preserve a backup if it may be damaged.","Open project again",IssueAction.OpenProject),
        IssueKind.ProjectCreateFailed => New(kind,"The project could not be created","The new encrypted project could not be written.","Check free space and access to the local project folder, then create the project again with a valid passphrase.","Create project again",IssueAction.CreateProject),
        _ => New(kind,"This file type is not supported","The file was not added for analysis.","Save legacy Word files as .docx first. Supported types are .docx, .txt, .md, .csv, .xlsx and .pptx. PDF and image analysis are not supported here.","Add supported file",IssueAction.AddFiles)
    };
    private static UserIssue New(IssueKind k,string title,string explanation,string step,string button,IssueAction action) => new(k,title,explanation,step,button,action);
    public static UserIssue Failure(IssueKind fallback, Exception? exception=null, string? warning=null)
    {
        var text=(warning??exception?.Message??"").ToLowerInvariant();
        if(fallback==IssueKind.ExportFailed && exception is InvalidDataException &&
            (text.StartsWith("export blocked: an identifying value remains",StringComparison.Ordinal)||
             text.StartsWith("export blocked: a formula can reconstruct an identifying value.",StringComparison.Ordinal)))return For(IssueKind.HiddenContent);
        if(text.Contains("does not match")&&text.Contains("table"))return For(IssueKind.ContextMismatch);
        // Storage errors on output are not mislabeled as missing source files.
        if(fallback is IssueKind.ScanIncomplete or IssueKind.DeepIncomplete && exception is FileNotFoundException or DirectoryNotFoundException)return For(IssueKind.MissingFile);
        if(text.Contains("model")&&(text.Contains("missing")||text.Contains("not found")||text.Contains("could not start")||text.Contains("integrity")))return For(IssueKind.ModelUnavailable);
        if(text.Contains("changed since")||text.Contains("no longer matches"))return For(IssueKind.SourceChanged);
        return For(fallback);
    }
}

public sealed class IssueDialog : Window
{
    public bool ActionRequested { get; private set; }
    public IssueDialog(UserIssue issue, bool repeated=false)
    {
        DialogLayout.Configure(this,issue.Title,530,410,true);
        var body=DialogLayout.Panel();
        body.Children.Add(new TextBlock { Text=issue.Title, FontSize=19, FontWeight=FontWeights.SemiBold, TextWrapping=TextWrapping.Wrap });
        body.Children.Add(DialogLayout.Note(issue.Explanation,12));
        body.Children.Add(DialogLayout.Label("What to do next",18));
        body.Children.Add(DialogLayout.Note(issue.NextStep,0));
        if(repeated)body.Children.Add(DialogLayout.Note("This issue has repeated. Copy support info for investigation rather than retrying indefinitely.",12));
        var actions=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(0,18,0,0) };
        var cancel=new Button { Content="Cancel",IsCancel=true,MinWidth=90,Margin=new Thickness(0,0,10,0) };
        DialogLayout.Named(cancel,"Cancel and return to the app");cancel.Click+=(_,_)=>DialogResult=false;
        var fix=DialogLayout.Primary(repeated?"Copy support info":issue.Button);fix.Margin=new Thickness(0);fix.IsDefault=false;
        DialogLayout.Named(fix,repeated?"Copy support info":issue.Button);fix.Click+=(_,_)=>{ActionRequested=true;DialogResult=true;};
        actions.Children.Add(cancel);actions.Children.Add(fix);
        var scroll=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        layout.Children.Add(scroll);var footer=new Border { Padding=new Thickness(24,12,24,16),Child=actions };Grid.SetRow(footer,1);layout.Children.Add(footer);Content=layout;
        SizeToContent=SizeToContent.Manual;Height=Math.Min(460,SystemParameters.WorkArea.Height*0.9);MinHeight=0;
    }
}

public partial class MainWindow
{
    private bool issueQueued,saveIssueShown;
    private readonly Dictionary<string,int> issueAttempts=new();
    private void QueueIssue(UserIssue issue, FileRecord? file=null)
    {
        if(issueQueued)return;
        issueQueued=true;
        var project=_project;
        // Run after the calling operation's finally block removes the progress overlay and busy state.
        void ShowIssue()
        {
            if(!IsVisible||!ReferenceEquals(project,_project)||(file!=null&&_project?.Files.Contains(file)!=true)){issueQueued=false;return;}
            if(Busy||tutorialOpen)
            {
                var wait=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(500) };
                wait.Tick+=(_,_)=>{if(!IsVisible||(!Busy&&!tutorialOpen)){wait.Stop();ShowIssue();}};
                wait.Start();return;
            }
            issueQueued=false;
            var key=issue.Kind+":"+(file?.Id??"project");
            var repeated=issue.Kind is IssueKind.ScanIncomplete or IssueKind.DeepIncomplete or IssueKind.ExportIncomplete or IssueKind.ContextMismatch or IssueKind.ExportFailed
                && issueAttempts.TryGetValue(key,out var count)&&count>0;
            issueAttempts[key]=issueAttempts.GetValueOrDefault(key)+1;
            var dialog=new IssueDialog(issue,repeated) { Owner=this };
            if(dialog.ShowDialog()!=true||!dialog.ActionRequested)return;
            if(file!=null&&_project?.Files.Contains(file)==true)FilesList.SelectedItem=file;
            try { RunIssueAction(repeated?IssueAction.Support:issue.Action); }
            catch(Exception) { new SupportInfoDialog(SupportCollector.CollectText(_project,Title,ReviewerCount)) { Owner=this }.ShowDialog(); }
        }
        Dispatcher.BeginInvoke(new Action(ShowIssue),DispatcherPriority.ContextIdle);
    }
    private void RunIssueAction(IssueAction action)
    {
        if(Busy)return;
        switch(action)
        {
            case IssueAction.Scan: Scan_Click(this,new RoutedEventArgs());break;
            case IssueAction.DeepCheck: if(Selected!=null)ResumeCheck_Click(this,new RoutedEventArgs());break;
            case IssueAction.Export: Desensitize_Click(this,new RoutedEventArgs());break;
            case IssueAction.Review: _mappingFilter="All";RefreshMappings();MappingsGrid.Focus();break;
            case IssueAction.ReviewFindings: _mappingFilter="Review";RefreshMappings();MappingsGrid.Focus();break;
            case IssueAction.Settings: Settings_Click(this,new RoutedEventArgs());break;
            case IssueAction.AddFiles: AddFiles_Click(this,new RoutedEventArgs());break;
            case IssueAction.SelectDocument: if(_project==null)OpenProject_Click(this,new RoutedEventArgs());else FilesList.Focus();break;
            case IssueAction.SaveProject: SaveProject_Click(this,new RoutedEventArgs());break;
            case IssueAction.Rehydrate: Rehydrate_Click(this,new RoutedEventArgs());break;
            case IssueAction.OpenProject: OpenProject_Click(this,new RoutedEventArgs());break;
            case IssueAction.CreateProject: NewProject_Click(this,new RoutedEventArgs());break;
            case IssueAction.AppFolder: Process.Start(new ProcessStartInfo { FileName=AppContext.BaseDirectory,UseShellExecute=true });break;
            case IssueAction.Support:
                var info=SupportCollector.CollectText(_project,Title,ReviewerCount);
                try { Clipboard.SetText(info); } catch(Exception) { }
                new SupportInfoDialog(info) { Owner=this }.ShowDialog();break;
        }
    }
    private void ExportHelp_Click(object sender,RoutedEventArgs e)
    {
        var file=Selected;
        var kind=file==null?IssueKind.NoDocument:!HasScannedFile(file)?IssueKind.ScanRequired:
            _settings.AutomaticDeepReview&&!file.DeepReviewComplete?IssueKind.DeepRequired:
            UiExportScope.Approved(_project!.Mappings).Count==0?IssueKind.NoReplacements:IssueKind.ReadyToExport;
        QueueIssue(UiIssues.For(kind),file);
    }
}
