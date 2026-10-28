using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Desensitizer
{
 // "Copy support info" (item D2): copies the privacy-safe report to the clipboard and shows exactly what was copied.
 public sealed class SupportInfoDialog : Window
 {
  private readonly string _text;
  private readonly TextBlock _status=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,8,0,0)};

  public SupportInfoDialog(string text)
  {
   _text=text??"";
   DialogLayout.Configure(this,"Support info",620,520);
   var panel=new Grid{Margin=new Thickness(24)};
   panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   panel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
   panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var intro=new TextBlock{Text="This is exactly what was copied. It holds versions, counts and timings only: no document text, names, file names, paths or passphrases. Read it before you send it to anyone.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=DialogLayout.Muted};
   panel.Children.Add(intro);
   var box=new TextBox{Text=_text,IsReadOnly=true,FontFamily=new System.Windows.Media.FontFamily("Consolas"),FontSize=12,TextWrapping=TextWrapping.NoWrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,10,0,0)};
   DialogLayout.Named(box,"Support info text");
   Grid.SetRow(box,1);panel.Children.Add(box);
   Grid.SetRow(_status,2);panel.Children.Add(_status);
   var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
   var copy=new Button{Content="Copy again",MinWidth=96,MinHeight=36,Margin=new Thickness(0,0,8,0)};copy.Click+=(s,e)=>Copy();
   var close=DialogLayout.Primary("Close");close.Margin=new Thickness(0);close.IsCancel=true;close.Click+=(s,e)=>Close();
   actions.Children.Add(copy);actions.Children.Add(close);
   Grid.SetRow(actions,3);panel.Children.Add(actions);
   Content=panel;
   Loaded+=(s,e)=>Copy();
  }

  private void Copy()
  {
   _status.Text=TrySetClipboard(_text)?"Copied to the clipboard.":"The clipboard is busy or unavailable. Select the text above and copy it by hand (Ctrl+C), or press Copy again.";
  }

  // The clipboard can be locked by another program for a moment, so a few quick retries are made. Never throws.
  public static bool TrySetClipboard(string text)
  {
   for(var attempt=0;attempt<4;attempt++)
   {
    try{Clipboard.SetDataObject(text,true);return true;}
    catch(System.Runtime.InteropServices.ExternalException){Thread.Sleep(40);}
    catch(Exception){return false;}
   }
   return false;
  }
 }
}
