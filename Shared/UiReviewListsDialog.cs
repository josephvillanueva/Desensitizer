using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Desensitizer
{
 // Shows and edits the open project's review lists (items P3 and P4):
 //  1. Labels kept as-is (added with "Keep as-is and remember"): view and remove.
 //  2. Generic terms of this project: view, add and remove.
 //  3. Organizations and vendors the lists hid during the last check: for the user to confirm that none is real.
 // Every edit goes straight into the project preferences, so it applies at once. Changed tells the caller to save.
 public sealed class ReviewListsDialog : Window
 {
  private const string AnyCategory="Any allowed category";
  private readonly ProjectReviewPreferences _preferences;
  private readonly ListBox _keep=new ListBox{MinHeight=64,MaxHeight=140};
  private readonly ListBox _terms=new ListBox{MinHeight=64,MaxHeight=140};
  private readonly ListBox _hidden=new ListBox{MinHeight=64,MaxHeight=160};
  private readonly TextBox _contextLabel=DialogLayout.Field(new TextBox());
  private readonly ComboBox _contextCategory=DialogLayout.Field(new ComboBox{ItemsSource=ProjectReviewPreferences.KeepAsIsCategories,SelectedIndex=0});
  private readonly TextBox _contextHint=DialogLayout.Field(new TextBox());
  private readonly TextBlock _contextProblem=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};
  private readonly TextBox _newTerm=DialogLayout.Field(new TextBox());
  private readonly ComboBox _termCategory=DialogLayout.Field(new ComboBox{ItemsSource=new[]{AnyCategory,"ORGANIZATION","VENDOR","PROJECT","SYSTEM","PRODUCT","DEPARTMENT"},SelectedIndex=0});
  private readonly Button _removeKeep=new Button{Content="Remove selected label",MinHeight=34,Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=false};
  private readonly Button _removeTerm=new Button{Content="Remove selected term",MinHeight=34,Margin=new Thickness(8,0,0,0),IsEnabled=false};
  private readonly Button _showAgain=new Button{Content="Show this organization again",MinHeight=34,Margin=new Thickness(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=false};

  public bool Changed {get;private set;}

  public ReviewListsDialog(ProjectReviewPreferences preferences)
  {
   _preferences=preferences;
   DialogLayout.Configure(this,"Review lists",600,700);
   var panel=DialogLayout.Panel();panel.Margin=new Thickness(24,22,24,24);
   panel.Children.Add(Heading("Labels kept as-is"));
   panel.Children.Add(Note("Choose Keep as-is and remember on a finding to add its label here. These labels are saved inside this project only. People, companies, contacts, places and identifiers are never remembered by spelling."));
   Describe(_keep,"Labels kept as-is");panel.Children.Add(_keep);panel.Children.Add(_removeKeep);
   _keep.SelectionChanged+=(s,e)=>_removeKeep.IsEnabled=_keep.SelectedItem!=null;
   _removeKeep.Click+=(s,e)=>{if(_keep.SelectedItem is ListBoxItem item&&item.Tag is KeepAsIsEntry entry&&_preferences.RemoveKeepAsIs(entry.Value,entry.ContextHint)){Changed=true;Reload();}};

   panel.Children.Add(Heading("Optional context-limited label"));
   panel.Children.Add(Note("Use this only for an ordinary project, system or product label suggested by AI. It applies only when every mention in the reviewed section has your phrase in its own sentence or line. It does not change direct Keep decisions, skip detectors or train model weights. Remove the entry above to undo it."));
   panel.Children.Add(DialogLayout.Label("Business label",8));Describe(_contextLabel,"Business label for context-limited feedback");panel.Children.Add(_contextLabel);
   panel.Children.Add(DialogLayout.Label("Suggestion category",8));Describe(_contextCategory,"Category for context-limited feedback");panel.Children.Add(_contextCategory);
   panel.Children.Add(DialogLayout.Label("Required nearby phrase",8));Describe(_contextHint,"Required phrase near each label mention");panel.Children.Add(_contextHint);
   var addContext=new Button{Content="Remember in this context",MinHeight=36,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,0)};
   Describe(addContext,"Remember label only in the supplied context");addContext.Click+=(s,e)=>AddContextLabel();panel.Children.Add(addContext);
   Describe(_contextProblem,"Context-limited label validation");AutomationProperties.SetLiveSetting(_contextProblem,AutomationLiveSetting.Polite);panel.Children.Add(_contextProblem);

   panel.Children.Add(new Separator{Margin=new Thickness(0,18,0,14)});
   panel.Children.Add(Heading("Generic terms of this project"));
   panel.Children.Add(Note("Ordinary words of this project (a department, a product family) that detectors should not report as organizations. A term can hide a real company, so every hidden name is listed below after a check."));
   Describe(_terms,"Generic terms");panel.Children.Add(_terms);
   _terms.SelectionChanged+=(s,e)=>_removeTerm.IsEnabled=_terms.SelectedItem!=null;
   _removeTerm.Click+=(s,e)=>{if(_terms.SelectedItem is ListBoxItem item&&item.Tag is ProjectGenericTerm term&&_preferences.RemoveGenericTerm(term.Value)){Changed=true;Reload();}};
   var addRow=new Grid{Margin=new Thickness(0,10,0,0)};
   addRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});addRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});addRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   Describe(_newTerm,"New generic term");Describe(_termCategory,"Category for the new generic term");
   _termCategory.Margin=new Thickness(8,0,0,0);_termCategory.MinWidth=190;Grid.SetColumn(_termCategory,1);
   var add=new Button{Content="Add term",MinHeight=36,Margin=new Thickness(8,0,0,0),MinWidth=84};Grid.SetColumn(add,2);
   add.Click+=(s,e)=>AddTerm();
   addRow.Children.Add(_newTerm);addRow.Children.Add(_termCategory);addRow.Children.Add(add);
   panel.Children.Add(addRow);
   var removeRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
   _removeTerm.Margin=new Thickness(0);removeRow.Children.Add(_removeTerm);panel.Children.Add(removeRow);

   panel.Children.Add(new Separator{Margin=new Thickness(0,18,0,14)});
   panel.Children.Add(Heading("Hidden organizations and vendors"));
   panel.Children.Add(Note(UiMessages.SuppressedOrganizationsIntro));
   Describe(_hidden,"Hidden organizations and vendors");panel.Children.Add(_hidden);panel.Children.Add(_showAgain);
   _hidden.SelectionChanged+=(s,e)=>_showAgain.IsEnabled=_hidden.SelectedItem!=null;
   _showAgain.Click+=(s,e)=>ShowAgain();

   var close=DialogLayout.Primary("Done");close.Margin=new Thickness(0);close.IsCancel=true;close.Click+=(s,e)=>DialogResult=true;
   var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};layout.Children.Add(scroll);
   var footer=new Border{BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(227,232,240)),BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(24,12,24,12),Child=close};Grid.SetRow(footer,1);layout.Children.Add(footer);
   Content=layout;
   Reload();
  }

  private static TextBlock Heading(string text)=>new TextBlock{Text=text,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,4)};
  private static TextBlock Note(string text)=>new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#5A6678")!,Margin=new Thickness(0,0,0,8)};
  private static void Describe(DependencyObject element,string name)=>AutomationProperties.SetName(element,name);

  private void Reload()
  {
   Fill(_keep,_preferences.KeepAsIsSnapshot().OrderBy(e=>e.Value,StringComparer.OrdinalIgnoreCase).Select(e=>(object)e),e=>{var k=(KeepAsIsEntry)e;return k.Value+" ("+string.Join(", ",k.Categories)+")"+(k.ContextHint.Length>0?", only near: "+k.ContextHint:"");},"No labels are kept as-is in this project yet.");
   Fill(_terms,_preferences.GenericTermsSnapshot().OrderBy(t=>t.Value,StringComparer.OrdinalIgnoreCase).Select(t=>(object)t),t=>{var g=(ProjectGenericTerm)t;return g.Value+" ("+(g.Categories.Count==0?"any allowed category":string.Join(", ",g.Categories))+")";},"No generic terms in this project yet.");
   Fill(_hidden,DocumentPrivacyPolicy.SuppressedOrganizations.Select(h=>(object)h),h=>{var c=(DocumentPrivacyPolicy.SuppressedCandidate)h;return c.Value+" ("+c.Category+", seen "+c.Count+(c.Count==1?" time":" times")+"): "+c.Reason;},"Nothing was hidden during the last check.");
  }

  private static void Fill(ListBox box,IEnumerable<object> items,Func<object,string> text,string emptyText)
  {
   box.Items.Clear();
   var any=false;
   foreach(var item in items){any=true;box.Items.Add(new ListBoxItem{Content=text(item),Tag=item});}
   if(!any)box.Items.Add(new ListBoxItem{Content=emptyText,IsEnabled=false,Foreground=System.Windows.Media.Brushes.Gray});
  }

  private void AddTerm()
  {
   var category=_termCategory.SelectedItem as string;
   var ok=_preferences.AddGenericTerm(_newTerm.Text,string.IsNullOrEmpty(category)||category==AnyCategory?null:new[]{category});
   if(!ok){MessageBox.Show(this,"This term cannot be added. Use 2 to 120 characters with letters and no digits, addresses or replacement tokens. People, contacts, places, identifiers, accounts and dates are never treated as generic.","Generic term not added",MessageBoxButton.OK,MessageBoxImage.Information);return;}
   _newTerm.Clear();Changed=true;Reload();
  }

  private void AddContextLabel()
  {
   if(!_preferences.AddContextKeepAsIs(_contextLabel.Text,_contextCategory.SelectedItem as string,_contextHint.Text))
   {
    _contextProblem.Text="Use a project, system or product label with 2 to 120 characters and no digits, addresses or tokens. Add a nearby phrase of 4 to 200 characters that is not just the label itself.";
    return;
   }
   _contextLabel.Clear();_contextHint.Clear();_contextProblem.Text="Context-limited label saved inside this project. It affects future AI suggestions only.";Changed=true;Reload();
  }

  private void ShowAgain()
  {
   if(!(_hidden.SelectedItem is ListBoxItem item&&item.Tag is DocumentPrivacyPolicy.SuppressedCandidate candidate))return;
   if(candidate.Reason=="Project generic term"&&_preferences.RemoveGenericTerm(candidate.Value))
   {
    Changed=true;Reload();
    MessageBox.Show(this,"The generic term was removed. Scan the document again to see this name in the findings.","Term removed",MessageBoxButton.OK,MessageBoxImage.Information);
    return;
   }
   MessageBox.Show(this,"This name was hidden by a built-in list or pattern, which cannot be edited here. If it is a real organization, add it with Add mapping so it is replaced.","Built-in rule",MessageBoxButton.OK,MessageBoxImage.Information);
  }
 }
}
