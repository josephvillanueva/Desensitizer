using System;
using System.Windows;
using System.Windows.Controls;

namespace Desensitizer
{
 public enum ResumeChoice { Resume, StartOver, Cancel }

 // Asked when an earlier check of the same document was interrupted and its progress is still saved in the project.
 public sealed class ResumeChoiceDialog : Window
 {
  public ResumeChoice Choice {get;private set;}=ResumeChoice.Cancel;
  public ResumeChoiceDialog(ReviewCheckpoint checkpoint)
  {
   DialogLayout.Configure(this,"Resume previous check",500,260,true);
   var panel=DialogLayout.Panel();
   panel.Children.Add(new TextBlock{Text=UiResume.OfferText(checkpoint),FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
   panel.Children.Add(DialogLayout.Note(UiResume.OfferDetail(checkpoint),10));
   if(checkpoint.Failed.Count>0)panel.Children.Add(DialogLayout.Note(UiResume.FailedSummary(checkpoint.Failed),8));
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
   var cancel=new Button{Content="Cancel",MinWidth=80,MinHeight=36,IsCancel=true,Margin=new Thickness(0,0,8,0)};cancel.Click+=(s,e)=>{Choice=ResumeChoice.Cancel;DialogResult=false;};
   var startOver=new Button{Content="Start over",MinWidth=96,MinHeight=36,Margin=new Thickness(0,0,8,0),ToolTip="Discard the saved progress and check every section again"};startOver.Click+=(s,e)=>{Choice=ResumeChoice.StartOver;DialogResult=true;};
   var resume=DialogLayout.Primary(checkpoint.Failed.Count>0?"Retry failed sections":"Resume");resume.Margin=new Thickness(0);resume.Click+=(s,e)=>{Choice=ResumeChoice.Resume;DialogResult=true;};
   actions.Children.Add(cancel);actions.Children.Add(startOver);actions.Children.Add(resume);panel.Children.Add(actions);Content=panel;
  }
 }

 // Shown when an export was stopped because approved replacements would not be applied (item G3).
 public sealed class UnappliedMappingsDialog : Window
 {
  public bool ExportAnyway {get;private set;}
  public UnappliedMappingsDialog(UiMessages.UnappliedReplacements found)
  {
   DialogLayout.Configure(this,"Replacements not applied",540,300,true);
   var panel=DialogLayout.Panel();
   panel.Children.Add(new TextBlock{Text="Some approved replacements would not be applied",FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
   panel.Children.Add(new TextBlock{Text=UiMessages.UnappliedExplanation(found),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)});
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};
   var anyway=new Button{Content="Export anyway",MinWidth=110,MinHeight=36,Margin=new Thickness(0,0,8,0)};
   anyway.Click+=(s,e)=>
   {
    if(MessageBox.Show(this,"Export without applying "+(found.Count==1?"this replacement":"these replacements")+"? The value stays visible in the exported file.","Confirm export",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
    ExportAnyway=true;DialogResult=true;
   };
   var fix=DialogLayout.Primary("Fix the list");fix.Margin=new Thickness(0);fix.IsCancel=true;fix.Click+=(s,e)=>{ExportAnyway=false;DialogResult=false;};
   actions.Children.Add(anyway);actions.Children.Add(fix);panel.Children.Add(actions);Content=panel;
  }
 }
}
