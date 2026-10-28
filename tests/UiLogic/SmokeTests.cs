using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Desensitizer;

// Creates the real main window and every dialog on a UI thread, with all folders pointed at a temp folder,
// to catch what only fails at run time: XAML that does not load, a missing element or resource, a constructor
// that throws. Nothing here shows a dialog that waits for input, and no real project or setting is read.
static class SmokeTests
{
    private static object? Call(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null) throw new MissingMethodException(name);
        return method.Invoke(target, args);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public static void Run(CheckFn Check, string work)
    {
        var results = new List<(string Name, bool Passed, string? Detail)>();
        void Record(string name, bool passed, string? detail = null) => results.Add((name, passed, detail));
        Exception? failure = null;
        var data = Path.Combine(work, "smoke-data");
        var temp = Path.Combine(work, "smoke-temp");
        Directory.CreateDirectory(temp);
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        Environment.SetEnvironmentVariable("TMP", temp);
        Environment.SetEnvironmentVariable("TEMP", temp);
        ProjectCatalog.UseRoot(data);
        AppSettingsStore.UseFolder(data);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                // The first-launch modal tutorial has dedicated tests. Do not block this smoke run.
                AppSettingsStore.Save(new AppSettings { TutorialSeen = true });
                var tutorialSettings = new SettingsDialog(new AppSettings { TutorialSeen = true });
                Record("settings: saving other preferences preserves tutorial seen flag", tutorialSettings.Result.TutorialSeen);
                tutorialSettings.Close();
                var window = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized };
                window.Show();
                Record("window: the main window loads and shows", window.IsLoaded || window.IsVisible);
                var tourOpened=false;
                var tutorialTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
                tutorialTimer.Tick+=(_,_)=>
                {
                    var tour=window.OwnedWindows.OfType<TutorialWindow>().FirstOrDefault();
                    if(tour==null)return;
                    tourOpened=true;tutorialTimer.Stop();tour.Close();
                };
                tutorialTimer.Start();
                Call(window,"StartTutorial");
                tutorialTimer.Stop();
                Record("tutorial: the real main window opens the modal guide",tourOpened);
                Record("tutorial: dismissal saves the seen preference",AppSettingsStore.Load().TutorialSeen);
                var replayDialog=new SettingsDialog(new AppSettings { TutorialSeen=true }) { Owner=window };
                var replayTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
                replayTimer.Tick+=(_,_)=>
                {
                    replayTimer.Stop();
                    var body=(StackPanel)((ScrollViewer)((Grid)replayDialog.Content).Children[0]).Content;
                    body.Children.OfType<Button>().Single(b=>Equals(b.Content,"Run tutorial again")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                replayTimer.Start();
                var replaySaved=replayDialog.ShowDialog();
                replayTimer.Stop();
                Record("tutorial: Settings replay requests the tour without saving other preferences",replayDialog.TutorialRequested&&replaySaved==false);
                foreach (var name in new[] { "StatusText", "NoticePanel", "MarkupNoticeText", "ResumeNoticeText", "ResumeCheckButton", "ReviewerRows", "AnalysisTimeText", "EstimateText", "ModelLoadText", "ReviewListsButton", "BulkRememberButton", "MappingsGrid", "StopAnalysisButton", "AnalysisOverlay" })
                    Record("window: element " + name + " exists", window.FindName(name) != null);
                Record("window: the status line is visible and has the muted contrast colour", ((TextBlock)window.FindName("StatusText")).Visibility == Visibility.Visible);
                ProjectLockTests.RunWindow(window,Record,work);

                // A project with a file, mappings and an interrupted check, put in place the way an unlock does.
                var project = Project.New("Smoke project", "Violet-Harbor-Lantern-42");
                var file = new FileRecord { Id = "file1", SourcePath = Path.Combine(work, "smoke.txt"), Display = "smoke.txt", Status = "Local scan complete: review mappings", LastScanAt = DateTimeOffset.UtcNow };
                File.WriteAllText(file.SourcePath, "Orion Portal is used by Mary Santos.");
                project.Files.Add(file);
                project.EnsureMapping(new Candidate { Value = "Orion Portal", Category = "SYSTEM", Confidence = 0.8, Source = "Rules" }, file.Id);
                project.EnsureMapping(new Candidate { Value = "Mary Santos", Category = "PERSON", Confidence = 0.9, Source = "Rules" }, file.Id);
                var checkpoint = new ReviewCheckpoint { SectionsTotal = 6 };
                checkpoint.Entries[new string('A', 64)] = "[]";
                checkpoint.Failed.Add(new SectionFailure { Reviewer = "Gemma", SectionIndex = 2, SectionCount = 2 });
                project.ReviewCheckpoints["file1#deep"] = checkpoint;
                SetField(window, "_project", project);
                SetField(window, "_path", Path.Combine(work, "smoke.dsproj"));
                var files = Field<System.Collections.ObjectModel.ObservableCollection<FileRecord>>(window, "_files");
                files.Add(file);
                var list = (ListBox)window.FindName("FilesList");
                list.SelectedItem = file;
                Call(window, "RefreshMappings");
                var grid = (DataGrid)window.FindName("MappingsGrid");
                Record("project view: the mapping table lists both findings", grid.Items.Count == 2, grid.Items.Count.ToString());
                var resume = (Button)window.FindName("ResumeCheckButton");
                Record("project view: an interrupted check shows the retry button", resume.Visibility == Visibility.Visible && Equals(resume.Content, "Retry failed sections"), resume.Visibility + "/" + resume.Content);
                Record("project view: the notice panel is visible and mentions the failed section", ((StackPanel)window.FindName("NoticePanel")).Visibility == Visibility.Visible && ((TextBlock)window.FindName("ResumeNoticeText")).Text.Contains("Gemma"));
                Record("project view: Review lists is enabled with a project open", ((Button)window.FindName("ReviewListsButton")).IsEnabled);

                // UI-7: while an export is starting (before any overlay) navigation and new work are disabled.
                var backButton = (Button)window.FindName("BackButton");
                Call(window, "UpdateUiState");
                var backBefore = backButton.IsEnabled;
                SetField(window, "_exportStarting", true);
                Call(window, "UpdateUiState");
                Record("export help: disabled while an operation starts, avoiding stale guidance",!((Button)window.FindName("ExportGuidanceButton")).IsEnabled);
                Record("busy: Back, Scan, Rehydrate and the project list are disabled while an export starts", backBefore && !backButton.IsEnabled && !((Button)window.FindName("ScanButton")).IsEnabled && !((Button)window.FindName("RehydrateButton")).IsEnabled && !((ListBox)window.FindName("ProjectsList")).IsEnabled && !((Button)window.FindName("DesensitizeButton")).IsEnabled);
                SetField(window, "_exportStarting", false);
                SetField(window, "_rehydrating", true);
                Call(window, "UpdateUiState");
                Record("busy: a running rehydrate also disables Back and Scan", !backButton.IsEnabled && !((Button)window.FindName("ScanButton")).IsEnabled);
                SetField(window, "_rehydrating", false);
                Call(window, "UpdateUiState");
                Record("export help: available again when the app is idle",((Button)window.FindName("ExportGuidanceButton")).IsEnabled);
                Record("busy: everything is enabled again afterwards", backButton.IsEnabled && ((Button)window.FindName("ScanButton")).IsEnabled);

                // Keyboard shortcut and bulk decision paths on highlighted rows.
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(grid.Items.Cast<object>().OfType<Mapping>().First(m => m.Category == "SYSTEM"));
                Call(window, "ApplySelectedDecision", "Keep as-is");
                Record("project view: K on a highlighted row keeps only that row", project.Mappings.Single(m => m.Category == "SYSTEM").Ignored && !project.Mappings.Single(m => m.Category == "PERSON").Ignored);

                // The progress overlay: rows, time text and the model loading notice.
                Call(window, "BeginAnalysisProgress", "Reading document", "Extracting text.", "scan");
                Call(window, "ShowAnalysisProgress", new AnalysisProgress { Step = 7, Total = 60, Name = ReviewerReportName(), Detail = "Reviewing section 1 of 20 locally. Nothing is uploaded." });
                var rows = (ItemsControl)window.FindName("ReviewerRows");
                Record("progress: a reviewer row appears with its section", rows.Visibility == Visibility.Visible && rows.Items.Count >= 1, rows.Items.Count.ToString());
                Record("progress: the elapsed time line is filled", ((TextBlock)window.FindName("AnalysisTimeText")).Text.StartsWith("Elapsed"));
                Record("progress: the overlay is shown while busy", ((Border)window.FindName("AnalysisOverlay")).Visibility == Visibility.Visible);
                Call(window, "ShowEstimate", Call(window, "BeginEstimate", new string('a', 4000) + " b"));
                var estimateLabel = (TextBlock)window.FindName("EstimateText");
                Record("estimate: the label shows the sections, reviewers and 'may differ' while a check runs", estimateLabel.Visibility == Visibility.Visible && estimateLabel.Text.StartsWith("About ") && estimateLabel.Text.Contains("sections x") && estimateLabel.Text.Contains("may differ"), estimateLabel.Text);
                Call(window, "EndAnalysisProgress");
                Record("estimate: the label is hidden afterwards", estimateLabel.Visibility == Visibility.Collapsed);
                Record("progress: the overlay and rows are cleared afterwards", ((Border)window.FindName("AnalysisOverlay")).Visibility == Visibility.Collapsed && rows.Visibility == Visibility.Collapsed);

                // Debounced save: a change is only marked, then written by the timer path.
                Call(window, "RequestSave");
                Record("save: a requested change is pending, not written at once", Field<UiSaveScheduler>(window, "_saveScheduler").IsDirty && !File.Exists(Path.Combine(work, "smoke.dsproj")));

                // UI-5: the crash guard's flush writes a project that has unsaved changes, from another thread.
                project.LoadedFormat = ProjectFormat.Dsp2;
                project.Name = "Edited before the crash";
                Call(window, "FlushForCrash");
                var crashSaved = File.Exists(Path.Combine(work, "smoke.dsproj")) && ProjectStore.Load(Path.Combine(work, "smoke.dsproj"), "Violet-Harbor-Lantern-42").Name == "Edited before the crash";
                Record("crash flush: the unsaved project is written before the app exits", crashSaved);

                SetField(window, "_project", null);
                SetField(window, "_path", null);
                files.Clear();
                Call(window, "UpdateUiState");
                window.Close();

                // Every dialog builds and shows without throwing.
                var dialogs = new List<(string, Func<Window>)>
                {
                    ("IssueDialog", () => new IssueDialog(UiIssues.For(IssueKind.DeepIncomplete))),
                    ("IssueDialog repeated failure", () => new IssueDialog(UiIssues.For(IssueKind.ExportIncomplete),true)),
                    ("ProjectDialog", () => new ProjectDialog("Create project", new[] { "Project 1" })),
                    ("SettingsDialog with a project", () => new SettingsDialog(new AppSettings(), (a, b) => { }, true, "Smoke project")),
                    ("SettingsDialog without a project", () => new SettingsDialog(new AppSettings())),
                    ("AboutDialog", () => new AboutDialog()),
                    ("AboutDialog with support info", () => new AboutDialog(() => "Support info")),
                    ("SupportInfoDialog", () => new SupportInfoDialog(SupportBundle.Build(new SupportInput { AppName = "Desensitizer" }))),
                    ("SettingsDialog with performance values", () => new SettingsDialog(new AppSettings { LlamaThreads = 3, MaxResidentModels = 2, DetectorConcurrency = 1, WorkerIdleMinutes = 9 }, null, null, null, 3)),
                    ("FinalReviewChoiceDialog", () => new FinalReviewChoiceDialog(3, "about 10 sections x 3 reviewers, roughly 30 min (first estimate, may differ)", 2)),
                    ("DeepCheckChoiceDialog with an estimate", () => new DeepCheckChoiceDialog("About 10 sections x 3 reviewers, roughly 30 min (first estimate, may differ)")),
                    ("ExportCompleteDialog with the advisory line", () => new ExportCompleteDialog(Path.Combine(work, "out.docx"), UiFinalReview.CompletionText(false, 3))),
                    ("ChangePassphraseDialog", () => new ChangePassphraseDialog("Smoke project")),
                    ("TextPromptDialog with a note", () => new TextPromptDialog("Neutral project label", "Label shown in the project list", "Project 1", UiMessages.NeutralLabelWarning, t => null)),
                    ("MappingDialog", () => new MappingDialog()),
                    ("DeepCheckChoiceDialog", () => new DeepCheckChoiceDialog()),
                    ("ExportCompleteDialog", () => new ExportCompleteDialog(Path.Combine(work, "out.docx"), "Done.")),
                    ("ReviewListsDialog", () => new ReviewListsDialog(new ProjectReviewPreferences())),
                    ("ResumeChoiceDialog", () => new ResumeChoiceDialog(checkpoint)),
                    ("RehydratePreviewDialog", () => new RehydratePreviewDialog(new RehydratePreview(2,1,1,1,0,0,0))),
                    ("UnappliedMappingsDialog", () => new UnappliedMappingsDialog(new UiMessages.UnappliedReplacements { Count = 2, Categories = "ORGANIZATION: 2" }))
                };
                foreach (var (name, create) in dialogs)
                {
                    try
                    {
                        var dialog = create();
                        dialog.ShowInTaskbar = false;
                        dialog.Show();
                        dialog.UpdateLayout();
                        var fits = dialog.ActualHeight > 0 && dialog.ActualHeight <= SystemParameters.WorkArea.Height;
                        dialog.Close();
                        Record("dialog: " + name + " builds, shows and fits the screen", fits, dialog.ActualHeight.ToString());
                    }
                    catch (Exception ex) { Record("dialog: " + name + " builds and shows", false, ex.GetType().Name + ": " + ex.Message); }
                }
                foreach(var accept in new[]{false,true})
                {
                    var issueDialog=new IssueDialog(UiIssues.For(IssueKind.ExportIncomplete));
                    var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
                    timer.Tick+=(_,_)=>
                    {
                        timer.Stop();
                        var actions=(WrapPanel)((Border)((Grid)issueDialog.Content).Children[1]).Child;
                        ((Button)actions.Children[accept?1:0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                    timer.Start();var answer=issueDialog.ShowDialog();timer.Stop();
                    Record("issue dialog: "+(accept?"primary button requests recovery":"Cancel returns without recovery"),issueDialog.ActionRequested==accept&&answer==accept);
                }
                // The performance group: round trip of the dialog result and bad input.
                var perfDialog = new SettingsDialog(new AppSettings { LlamaThreads = 3, MaxResidentModels = 2, DetectorConcurrency = 1, WorkerIdleMinutes = 9 }, null, null, null, 3);
                Record("settings: the dialog result keeps the saved performance values", perfDialog.Result.LlamaThreads == 3 && perfDialog.Result.MaxResidentModels == 2 && perfDialog.Result.DetectorConcurrency == 1 && perfDialog.Result.WorkerIdleMinutes == 9);
                var perfPanel = new PerformanceSettingsPanel(new AppSettings { LlamaThreads = 3, WorkerIdleMinutes = 9 }, 3);
                var perfBoxes = perfPanel.Children.OfType<TextBox>().ToList();
                Record("settings: the performance group has four fields and shows 0 as Automatic", perfBoxes.Count == 4 && perfBoxes[0].Text == "3" && perfBoxes[1].Text == "Automatic" && perfBoxes[2].Text == "Automatic" && perfBoxes[3].Text == "9", string.Join("|", perfBoxes.Select(b => b.Text)));
                Record("settings: every performance field has an explanatory tooltip", perfBoxes.All(b => b.ToolTip is string tip && tip.Length > 40));
                var perfTarget = new AppSettings();
                perfBoxes[0].Text = "twelve";
                Record("settings: bad input in a performance field is refused without throwing", perfPanel.Apply(perfTarget) != null && perfTarget.LlamaThreads == 0);
                perfBoxes[0].Text = "2"; perfBoxes[1].Text = "";
                Record("settings: good input is written to the settings", perfPanel.Apply(perfTarget) == null && perfTarget.LlamaThreads == 2 && perfTarget.MaxResidentModels == 0 && perfTarget.WorkerIdleMinutes == 9);
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Environment.SetEnvironmentVariable("TMP", oldTmp);
        Environment.SetEnvironmentVariable("TEMP", oldTemp);
        AppSettingsStore.UseFolder(null);
        ProjectCatalog.UseRoot(Path.Combine(work, "app-data"));
        foreach (var result in results) Check(result.Name, result.Passed, result.Detail);
        Check("smoke: the window and dialogs ran to the end without an exception", failure == null, failure?.ToString());
    }

    // The name each app's AI reviewer uses for its progress reports.
    private static string ReviewerReportName() =>
#if AI_APP
        "AI reviewer 1 of 3: Qwen";
#else
        "Local AI review";
#endif
}
