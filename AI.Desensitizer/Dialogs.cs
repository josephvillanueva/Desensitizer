using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;

namespace Desensitizer
{
 internal static class DialogLayout
 {
  // Secondary text colour with enough contrast on white (about 6 to 1).
  public static System.Windows.Media.Brush Muted=>Application.Current?.TryFindResource("MutedBrush") as System.Windows.Media.Brush??new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A,0x66,0x78));
  public static StackPanel Panel()=>new StackPanel{Margin=new Thickness(24)};
  public static TextBlock Label(string text,double top=0)=>new TextBlock{Text=text,FontSize=12,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,top,0,6)};
  public static T Field<T>(T control) where T:Control{control.MinHeight=36;return control;}
  public static Button Primary(string text)
  {
   var button=new Button{Content=text,MinWidth=96,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0),IsDefault=true};
   if(Application.Current.TryFindResource("PrimaryButton") is Style style)button.Style=style;
   return button;
  }
  // sizeToContent: the dialog grows to fit its fields (and the larger text sizes) instead of using a fixed height.
  public static void Configure(Window window,string title,double width,double height,bool sizeToContent=false)
  {
   window.Title=title;window.Icon=new BitmapImage(new System.Uri("pack://application:,,,/"+System.Reflection.Assembly.GetExecutingAssembly().GetName().Name+";component/Assets/Desensitizer.ico"));window.Width=width;window.Height=height;window.MinWidth=width;window.MinHeight=sizeToContent?0:height;window.ResizeMode=ResizeMode.NoResize;window.WindowStyle=WindowStyle.SingleBorderWindow;window.ShowInTaskbar=false;window.WindowStartupLocation=WindowStartupLocation.CenterOwner;
   if(sizeToContent){window.SizeToContent=SizeToContent.Height;window.MaxHeight=SystemParameters.WorkArea.Height*0.92;}
   window.Loaded+=(s,e)=>TextScaling.ApplyTree(window);
   // Esc closes every dialog without applying it, like the Cancel button.
   window.PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Escape&&!e.Handled&&!(System.Windows.Input.Keyboard.FocusedElement is ComboBox{IsDropDownOpen:true})){e.Handled=true;try{window.DialogResult=false;}catch(InvalidOperationException){window.Close();}}};
  }
  // Gives an input control a name that screen readers announce (the visible label is a separate element).
  public static T Named<T>(T control,string name) where T:DependencyObject{System.Windows.Automation.AutomationProperties.SetName(control,name);return control;}
  public static TextBlock Note(string text,double top=6)=>new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=Application.Current.TryFindResource("MutedBrush") as System.Windows.Media.Brush??DialogLayout.Muted,Margin=new Thickness(0,top,0,0)};
 }

 public sealed class PasswordDialog : Window
 {
  private readonly PasswordBox _box=DialogLayout.Field(new PasswordBox());
  public string? Result {get;private set;}
  private PasswordDialog(string title,string prompt)
  {
   DialogLayout.Configure(this,title,400,210,true);var panel=DialogLayout.Panel();var promptLabel=DialogLayout.Label(prompt);panel.Children.Add(promptLabel);DialogLayout.Named(_box,"Passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_box,promptLabel);panel.Children.Add(_box);var button=DialogLayout.Primary("Continue");button.Click+=(s,e)=>{Result=_box.Password;DialogResult=true;};panel.Children.Add(button);Content=panel;Loaded+=(s,e)=>_box.Focus();
  }
  public static string? Ask(string title,string prompt){var dialog=new PasswordDialog(title,prompt);if(Application.Current.MainWindow?.IsVisible==true)dialog.Owner=Application.Current.MainWindow;return dialog.ShowDialog()==true?dialog.Result:null;}
 }

 public sealed class ProjectDialog : Window
 {
  private readonly TextBox _name=DialogLayout.Field(new TextBox());private readonly PasswordBox _pass=DialogLayout.Field(new PasswordBox());private readonly PasswordBox _confirm=DialogLayout.Field(new PasswordBox());private readonly TextBox _label=DialogLayout.Field(new TextBox());
  public string ProjectName=>_name.Text.Trim();public string Passphrase=>_pass.Password;
  // Optional neutral label for the project list. Empty means the next free "Project N".
  public string ListLabel=>_label.Text.Trim();
  public ProjectDialog(string title,System.Collections.Generic.IEnumerable<string>? existingLabels=null)
  {
   var labels=(existingLabels??System.Linq.Enumerable.Empty<string>()).ToList();
   DialogLayout.Configure(this,title,460,420,true);var panel=DialogLayout.Panel();
   var nameLabel=DialogLayout.Label("Project name");panel.Children.Add(nameLabel);DialogLayout.Named(_name,"Project name");System.Windows.Automation.AutomationProperties.SetLabeledBy(_name,nameLabel);panel.Children.Add(_name);
   var passLabel=DialogLayout.Label("Passphrase",16);panel.Children.Add(passLabel);DialogLayout.Named(_pass,"Passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_pass,passLabel);panel.Children.Add(_pass);
   panel.Children.Add(DialogLayout.Note(UiMessages.PassphraseRuleSummary));
   var confirmLabel=DialogLayout.Label("Confirm passphrase",14);panel.Children.Add(confirmLabel);DialogLayout.Named(_confirm,"Confirm passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_confirm,confirmLabel);panel.Children.Add(_confirm);
   var listLabel=DialogLayout.Label("List label (optional)",16);panel.Children.Add(listLabel);DialogLayout.Named(_label,"List label, optional");System.Windows.Automation.AutomationProperties.SetLabeledBy(_label,listLabel);panel.Children.Add(_label);
   panel.Children.Add(DialogLayout.Note(UiMessages.NeutralLabelWarning));
   var button=DialogLayout.Primary("Create project");
   button.Click+=(s,e)=>
   {
    var problem=UiMessages.ValidateNewProject(ProjectName,Passphrase,_confirm.Password);
    if(problem==null&&ListLabel.Length>0)problem=UiProjectLabel.Validate(ListLabel,labels);
    if(problem!=null){MessageBox.Show(this,problem,"Project details");return;}
    DialogResult=true;
   };
   panel.Children.Add(button);Content=panel;Loaded+=(s,e)=>_name.Focus();
  }
 }

 public sealed class MappingDialog : Window
 {
  private readonly TextBox _value=DialogLayout.Field(new TextBox());private readonly ComboBox _category=DialogLayout.Field(new ComboBox{ItemsSource=new[]{"PERSON","EMAIL","PHONE","ADDRESS","IDENTIFIER","CUSTOM"},SelectedIndex=0});
  public string Value=>_value.Text.Trim();public string Category=>(string)_category.SelectedItem;
  public MappingDialog()
  {
   DialogLayout.Configure(this,"Add mapping",420,290,true);var panel=DialogLayout.Panel();var valueLabel=DialogLayout.Label("Sensitive value or phrase");panel.Children.Add(valueLabel);DialogLayout.Named(_value,"Sensitive value or phrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_value,valueLabel);panel.Children.Add(_value);var categoryLabel=DialogLayout.Label("Category",16);panel.Children.Add(categoryLabel);DialogLayout.Named(_category,"Category");System.Windows.Automation.AutomationProperties.SetLabeledBy(_category,categoryLabel);panel.Children.Add(_category);var button=DialogLayout.Primary("Add mapping");button.Click+=(s,e)=>{if(Value.Length==0)return;DialogResult=true;};panel.Children.Add(button);Content=panel;Loaded+=(s,e)=>_value.Focus();
  }
 }

 public sealed class TextPromptDialog : Window
 {
  private readonly TextBox _value=DialogLayout.Field(new TextBox());public string Value=>_value.Text.Trim();
  // note: extra explanation under the field. validate: returns a sentence when the text is not acceptable, otherwise null.
  public TextPromptDialog(string title,string prompt,string initial,string? note=null,Func<string,string?>? validate=null)
  {
   DialogLayout.Configure(this,title,420,220,true);var panel=DialogLayout.Panel();var promptLabel=DialogLayout.Label(prompt);panel.Children.Add(promptLabel);DialogLayout.Named(_value,prompt);System.Windows.Automation.AutomationProperties.SetLabeledBy(_value,promptLabel);_value.Text=initial;_value.SelectAll();panel.Children.Add(_value);
   if(!string.IsNullOrEmpty(note))panel.Children.Add(DialogLayout.Note(note));
   var button=DialogLayout.Primary("Save changes");button.Click+=(s,e)=>{if(Value.Length==0)return;var problem=validate?.Invoke(Value);if(problem!=null){MessageBox.Show(this,problem,title);return;}DialogResult=true;};panel.Children.Add(button);Content=panel;Loaded+=(s,e)=>_value.Focus();
  }
 }

 public sealed class SettingsDialog : Window
 {
  private readonly CheckBox _autoScan=new CheckBox{Content="Scan files automatically when added",Margin=new Thickness(0,8,0,0)};
  private readonly CheckBox _compact=new CheckBox{Content="Compact mapping table",Margin=new Thickness(0,8,0,0)};
  private readonly CheckBox _review=new CheckBox{Content="Open mapping review on unresolved findings",Margin=new Thickness(0,8,0,0)};
  private readonly CheckBox _autoReplaceExport=new CheckBox{Content="Automatically replace new findings during export",Margin=new Thickness(0,8,0,0)};
  private readonly TextBox _imageText=DialogLayout.Field(new TextBox{MaxLength=DocumentProcessingOptions.MaxPlaceholderLength});
  private readonly CheckBox _imageSize=new CheckBox{Content="Include the picture's size",Margin=new Thickness(0,8,0,0)};
  private readonly CheckBox _imageAlt=new CheckBox{Content="Include the picture's description",Margin=new Thickness(0,8,0,0)};
  private readonly Slider _textScale=new Slider{Minimum=100,Maximum=150,TickFrequency=10,IsSnapToTickEnabled=true,MinWidth=250,VerticalAlignment=VerticalAlignment.Center};
  public AppSettings Result {get;}
  public bool TutorialRequested {get;private set;}
  public bool? AutoScanOnAddResult {get;private set;}
  public SettingsDialog(AppSettings current,Action<string,string>? changeProjectPassphrase=null,bool? autoScanOnAdd=null,string? projectName=null,int reviewerModelCount=1)
  {
   Result=new AppSettings{AutomaticDeepReview=current.AutomaticDeepReview,CompactTable=current.CompactTable,OpenOnReview=current.OpenOnReview,TextScalePercent=TextScaling.Normalize(current.TextScalePercent),AutoReplaceDuringExport=current.AutoReplaceDuringExport,ImagePlaceholder=current.ImagePlaceholder??"",ImagePlaceholderIncludeSize=current.ImagePlaceholderIncludeSize,ImagePlaceholderIncludeAltText=current.ImagePlaceholderIncludeAltText};
   UiPerfSettings.CopyPerformance(current,Result);
   Result.TutorialSeen=current.TutorialSeen;
   AutoScanOnAddResult=autoScanOnAdd;
   DialogLayout.Configure(this,"Settings",490,530);
   var panel=DialogLayout.Panel();panel.Margin=new Thickness(24,22,24,24);
   panel.Children.Add(new TextBlock{Text="Getting started",FontSize=17,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(DialogLayout.Note("A guided spotlight tour explains scanning, review decisions and how to export. Replaying closes Settings without saving any pending changes.",6));
   var tutorial=new Button{Content="Run tutorial again",Margin=new Thickness(0,10,0,0),HorizontalAlignment=HorizontalAlignment.Left};
   DialogLayout.Named(tutorial,"Run tutorial again");
   tutorial.Click+=(_,_)=>{TutorialRequested=true;DialogResult=false;};panel.Children.Add(tutorial);
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Accessibility",FontSize=17,FontWeight=FontWeights.SemiBold});
   var preview=new TextBlock{Text="Text size preview",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,10,0,12)};
   panel.Children.Add(preview);
   var textSizeRow=new Grid();textSizeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});textSizeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});textSizeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var smallA=new TextBlock{Text="A",FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};textSizeRow.Children.Add(smallA);
   _textScale.Value=Result.TextScalePercent;DialogLayout.Named(_textScale,"Text size percent");Grid.SetColumn(_textScale,1);textSizeRow.Children.Add(_textScale);
   var largeA=new TextBlock{Text="A",FontSize=22,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0)};Grid.SetColumn(largeA,2);textSizeRow.Children.Add(largeA);panel.Children.Add(textSizeRow);
   var sizeLabel=new TextBlock{Text=$"Text size: {Result.TextScalePercent:0}%",Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(0,7,0,0)};panel.Children.Add(sizeLabel);
   _textScale.ValueChanged+=(s,e)=>{var value=TextScaling.Normalize(_textScale.Value);sizeLabel.Text=$"Text size: {value:0}%";preview.FontSize=16*value/100d;};
   panel.Children.Add(new TextBlock{Text="Changes apply throughout Desensitizer after you save.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=DialogLayout.Muted,Margin=new Thickness(0,4,0,0)});
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Project security",FontSize=17,FontWeight=FontWeights.SemiBold});
   if(changeProjectPassphrase!=null)
   {
    panel.Children.Add(new TextBlock{Text="Change the passphrase used to unlock this project's encrypted mappings.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=DialogLayout.Muted,Margin=new Thickness(0,6,0,8)});
    var change=new Button{Content="Change project passphrase",HorizontalAlignment=HorizontalAlignment.Left,MinHeight=36};
    change.Click+=(s,e)=>
    {
     var dialog=new ChangePassphraseDialog(projectName){Owner=this};if(dialog.ShowDialog()!=true)return;
     try{changeProjectPassphrase(dialog.CurrentPassphrase,dialog.NewPassphrase);MessageBox.Show(this,"Project passphrase changed. Use the new passphrase the next time you open this project.","Passphrase changed",MessageBoxButton.OK,MessageBoxImage.Information);}
     catch(Exception ex){MessageBox.Show(this,"Could not change the passphrase. "+ex.Message,"Passphrase unchanged",MessageBoxButton.OK,MessageBoxImage.Warning);}
    };
    panel.Children.Add(change);
   }
   else panel.Children.Add(new TextBlock{Text="Open a project to change its passphrase.",FontSize=12,Foreground=DialogLayout.Muted,Margin=new Thickness(0,6,0,0)});
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Analysis",FontSize=17,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(new TextBlock{Text="All analysis stays on this computer.",Foreground=DialogLayout.Muted,Margin=new Thickness(0,4,0,8)});
   if(autoScanOnAdd.HasValue){_autoScan.IsChecked=autoScanOnAdd.Value;panel.Children.Add(_autoScan);panel.Children.Add(new TextBlock{Text="New Word and text files are scanned one at a time. Turn this off to add files without starting a scan.",TextWrapping=TextWrapping.Wrap,Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(24,4,0,4)});}
   panel.Children.Add(new TextBlock{Text="Use Automatic deep check in the top bar to continue from local detectors into three distinct local AI reviewers. Two-reviewer consensus can approve a replacement automatically. One-reviewer findings remain available for your decision.",TextWrapping=TextWrapping.Wrap,Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(0,6,0,0)});
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Review experience",FontSize=17,FontWeight=FontWeights.SemiBold});
   _compact.IsChecked=Result.CompactTable;_review.IsChecked=Result.OpenOnReview;_autoReplaceExport.IsChecked=Result.AutoReplaceDuringExport;panel.Children.Add(_compact);panel.Children.Add(_review);panel.Children.Add(_autoReplaceExport);
   panel.Children.Add(new TextBlock{Text="When enabled, new residual findings are set to Replace and the protected copy is checked again. The process stops after five attempts if a finding cannot be removed. Keep as-is decisions are never changed.",TextWrapping=TextWrapping.Wrap,Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(24,4,0,0)});
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Pictures in exported Word files",FontSize=17,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(DialogLayout.Note("Pictures and embedded objects are always removed from the protected copy. This text marks where each one was. Leave it empty to remove them without a marker. It cannot look like a replacement token such as [PERSON_001].",6));
   var imageLabel=DialogLayout.Label("Placeholder text",10);panel.Children.Add(imageLabel);
   _imageText.Text=Result.ImagePlaceholder;DialogLayout.Named(_imageText,"Placeholder text for removed pictures");System.Windows.Automation.AutomationProperties.SetLabeledBy(_imageText,imageLabel);panel.Children.Add(_imageText);
   _imageSize.IsChecked=Result.ImagePlaceholderIncludeSize;_imageAlt.IsChecked=Result.ImagePlaceholderIncludeAltText;panel.Children.Add(_imageSize);panel.Children.Add(_imageAlt);
   panel.Children.Add(DialogLayout.Note("A picture's description can contain names that were never checked. When it is included, it is passed through the same replacements as the document and the export check still scans it.",4));
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   var perf=new PerformanceSettingsPanel(current,reviewerModelCount);panel.Children.Add(perf);
   panel.Children.Add(new Separator{Margin=new Thickness(0,20,0,16)});
   panel.Children.Add(new TextBlock{Text="Privacy and safety",FontSize=17,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(new TextBlock{Text="Offline mode is always on. Export validation cannot be disabled. Project mappings are encrypted; source documents are not copied into the project. Removing the last file clears its mappings.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=DialogLayout.Muted,Margin=new Thickness(0,6,0,0)});
   var save=DialogLayout.Primary("Save settings");save.Margin=new Thickness(0);save.Click+=(s,e)=>{var perfProblem=perf.Apply(Result);if(perfProblem!=null){MessageBox.Show(this,perfProblem,"Performance settings",MessageBoxButton.OK,MessageBoxImage.Warning);return;}var imageProblem=UiImageSettings.Problem(_imageText.Text,_imageSize.IsChecked==true,_imageAlt.IsChecked==true);if(imageProblem!=null){MessageBox.Show(this,imageProblem,"Picture placeholder",MessageBoxButton.OK,MessageBoxImage.Warning);_imageText.Focus();return;}Result.ImagePlaceholder=_imageText.Text.Trim();Result.ImagePlaceholderIncludeSize=_imageSize.IsChecked==true;Result.ImagePlaceholderIncludeAltText=_imageAlt.IsChecked==true;Result.CompactTable=_compact.IsChecked==true;Result.OpenOnReview=_review.IsChecked==true;Result.TextScalePercent=TextScaling.Normalize(_textScale.Value);Result.AutoReplaceDuringExport=_autoReplaceExport.IsChecked==true;if(autoScanOnAdd.HasValue)AutoScanOnAddResult=_autoScan.IsChecked==true;DialogResult=true;};
   var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,0);layout.Children.Add(scroll);
   var footer=new Border{BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(227,232,240)),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(24,12,24,12)};footer.Child=save;Grid.SetRow(footer,1);layout.Children.Add(footer);Content=layout;
  }
 }

 public sealed class AboutDialog : Window
 {
  public AboutDialog(Func<string>? supportInfo=null)
  {
   DialogLayout.Configure(this,"About Desensitizer",600,620);
   var panel=DialogLayout.Panel();panel.Margin=new Thickness(24,22,24,24);
   panel.Children.Add(new TextBlock{Text=ProductVersion.Label,FontSize=20,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(new TextBlock{Text="How Desensitizer works",FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,9,0,0)});
   panel.Children.Add(new TextBlock{Text="A local privacy gateway for preparing Word and text files for external analysis.",TextWrapping=TextWrapping.Wrap,Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(0,6,0,14)});
   AddSection(panel,"1  Read the document","Supports .docx, .xlsx, .pptx, .csv, .md and .txt. Text is extracted locally, including hidden sheets, speaker notes and comments. Macros and active controls are blocked. Comments and tracked edits are omitted from the protected copy, and embedded files are removed. In Excel and PowerPoint files, pictures, charts, embedded objects and external links are removed too. Source documents are not copied into the project.");
   AddSection(panel,"2  Find sensitive details","Built-in rules, Microsoft Presidio, OpenAI Privacy Filter, and GLiNER propose identity and business-sensitive findings. Related acronyms, whole URLs, and date ranges are prepared for review.");
   AddSection(panel,"3  Three-reviewer consensus","Qwen, NuExtract, and Gemma review the document sequentially on this computer. Findings supported by at least two reviewers are set to Replace. One-reviewer findings stay in Review, and Keep as-is decisions remain unchanged.");
   AddSection(panel,"4  Protect the copy","Approved values become tokens using encrypted local project mappings. Word export removes metadata, external links, comments, review history, embedded files, and images, then checks the package and scans the protected copy again. Incomplete required checks block release.");
   AddSection(panel,"5  Restore when needed","A returned file can be rehydrated locally with recognized project tokens. Keep the encrypted project and its passphrase secure.");
   try
   {
    // No manifest ships beside the app yet, so the local AI programs and models are not checked against known hashes.
    var integrity=RuntimeIntegrity.Default.IntegrityWarning;
    if(integrity!=null)AddSection(panel,"Runtime check",integrity+" This is a notice only, nothing is blocked. Create the manifest when packaging to turn the check on.");
   }
   catch(Exception){}
   panel.Children.Add(new Separator{Margin=new Thickness(0,16,0,12)});
   panel.Children.Add(new TextBlock{Text="Current limits",FontSize=15,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(new TextBlock{Text="Passing the release checks is not a guarantee of anonymity. Supported files include Word, Excel, PowerPoint, CSV, Markdown and text. Images are removed or replaced, not read with OCR; PDF is not supported. This app reviews with Qwen, NuExtract and Gemma. Model agreement is not a calibrated probability. Inspect each exported copy and follow your organization's data-handling rules.",TextWrapping=TextWrapping.Wrap,Foreground=DialogLayout.Muted,FontSize=12,Margin=new Thickness(0,6,0,0)});
   var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,0);layout.Children.Add(scroll);
   var close=DialogLayout.Primary("Close");close.Margin=new Thickness(0);close.IsCancel=true;close.Click+=(s,e)=>Close();
   var footerActions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
   if(supportInfo!=null)
   {
    var copySupport=new Button{Content="Copy support info",MinHeight=36,Padding=new Thickness(12,0,12,0),Margin=new Thickness(0,0,8,0),ToolTip="Copies versions, counts and timings for support. It never includes document text, names, file names or paths."};
    copySupport.Click+=(s,e)=>{string text;try{text=supportInfo();}catch(Exception){text="Support info could not be collected.";}new SupportInfoDialog(text){Owner=this}.ShowDialog();};
    footerActions.Children.Add(copySupport);
   }
   footerActions.Children.Add(close);
   var footer=new Border{BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(227,232,240)),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(24,12,24,12),Child=footerActions};Grid.SetRow(footer,1);layout.Children.Add(footer);Content=layout;
  }
  private static void AddSection(StackPanel panel,string title,string description)
  {
   panel.Children.Add(new TextBlock{Text=title,FontSize=14,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,0)});
   panel.Children.Add(new TextBlock{Text=description,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=DialogLayout.Muted,Margin=new Thickness(0,4,0,0)});
  }
 }

 public sealed class ChangePassphraseDialog : Window
 {
  private readonly PasswordBox _current=DialogLayout.Field(new PasswordBox());
  private readonly PasswordBox _next=DialogLayout.Field(new PasswordBox());
  private readonly PasswordBox _confirm=DialogLayout.Field(new PasswordBox());
  public string CurrentPassphrase=>_current.Password;
  public string NewPassphrase=>_next.Password;
  public ChangePassphraseDialog(string? projectName=null)
  {
   DialogLayout.Configure(this,"Change project passphrase",460,340,true);
   var panel=DialogLayout.Panel();
   var currentLabel=DialogLayout.Label("Current passphrase");panel.Children.Add(currentLabel);DialogLayout.Named(_current,"Current passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_current,currentLabel);panel.Children.Add(_current);
   var nextLabel=DialogLayout.Label("New passphrase",14);panel.Children.Add(nextLabel);DialogLayout.Named(_next,"New passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_next,nextLabel);panel.Children.Add(_next);
   panel.Children.Add(DialogLayout.Note(UiMessages.PassphraseRuleSummary));
   var confirmLabel=DialogLayout.Label("Confirm new passphrase",14);panel.Children.Add(confirmLabel);DialogLayout.Named(_confirm,"Confirm new passphrase");System.Windows.Automation.AutomationProperties.SetLabeledBy(_confirm,confirmLabel);panel.Children.Add(_confirm);
   var save=DialogLayout.Primary("Change passphrase");save.Click+=(s,e)=>
   {
    var problem=UiMessages.ValidatePassphraseChange(CurrentPassphrase,NewPassphrase,_confirm.Password,projectName??"");
    if(problem!=null){MessageBox.Show(this,problem,"Check passphrase");return;}
    DialogResult=true;
   };panel.Children.Add(save);Content=panel;Loaded+=(s,e)=>_current.Focus();
  }
 }

 public sealed class DeepCheckChoiceDialog : Window
 {
  public DeepCheckChoiceDialog(string? estimate=null)
  {
   DialogLayout.Configure(this,"Optional deep check",460,290,true);
   var panel=DialogLayout.Panel();
   panel.Children.Add(new TextBlock{Text="Local scan complete",FontSize=18,FontWeight=FontWeights.SemiBold});
   panel.Children.Add(new TextBlock{Text="Run an additional offline check now? Local detectors plus Qwen, NuExtract, and Gemma will review the document sequentially. Two-reviewer consensus is set to Replace. One-reviewer findings stay in Review.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,16),Foreground=DialogLayout.Muted});
   if(!string.IsNullOrWhiteSpace(estimate))panel.Children.Add(DialogLayout.Note(estimate+".",0));
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
   var skip=new Button{Content="No, skip for now",IsCancel=true,MinHeight=36,Margin=new Thickness(0,0,8,0)};
   skip.Click+=(s,e)=>DialogResult=false;
   var run=DialogLayout.Primary("Yes, run deep check");run.Margin=new Thickness(0);run.Click+=(s,e)=>DialogResult=true;
   actions.Children.Add(skip);actions.Children.Add(run);panel.Children.Add(actions);Content=panel;
  }
 }

 public enum ExportOpenAction { Done, OpenFile, OpenFolder }

 public sealed class ExportCompleteDialog : Window
 {
  public ExportOpenAction Action {get;private set;}=ExportOpenAction.Done;
  public ExportCompleteDialog(string filePath,string description)
  {
   DialogLayout.Configure(this,"Export complete",510,270,true);
   var panel=DialogLayout.Panel();
   panel.Children.Add(new TextBlock{Text="✓  File ready",FontSize=19,FontWeight=FontWeights.SemiBold,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24,121,78))});
   panel.Children.Add(new TextBlock{Text=description,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12),Foreground=DialogLayout.Muted});
   panel.Children.Add(new TextBlock{Text=Path.GetFileName(filePath),FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=filePath});
   panel.Children.Add(new TextBlock{Text=Path.GetDirectoryName(filePath),FontSize=12,Foreground=DialogLayout.Muted,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,3,0,0),ToolTip=filePath});
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};
   var done=new Button{Content="Done",MinWidth=76,IsCancel=true,Margin=new Thickness(0,0,8,0)};done.Click+=(s,e)=>{Action=ExportOpenAction.Done;DialogResult=true;};
   var folder=new Button{Content="Open folder",MinWidth=100,Margin=new Thickness(0,0,8,0)};folder.Click+=(s,e)=>{Action=ExportOpenAction.OpenFolder;DialogResult=true;};
   var open=DialogLayout.Primary("Open file");open.Margin=new Thickness(0);open.Click+=(s,e)=>{Action=ExportOpenAction.OpenFile;DialogResult=true;};
   actions.Children.Add(done);actions.Children.Add(folder);actions.Children.Add(open);panel.Children.Add(actions);Content=panel;
  }
 }
}
