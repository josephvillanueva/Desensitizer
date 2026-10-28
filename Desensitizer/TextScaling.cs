using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Desensitizer
{
 public static class TextScaling
 {
  private static readonly DependencyProperty BaseFontSizeProperty=DependencyProperty.RegisterAttached("BaseFontSize",typeof(double),typeof(TextScaling),new PropertyMetadata(double.NaN));
  public static double Percent {get;private set;}=100;

  public static double Normalize(double percent)=>Math.Clamp(Math.Round(percent/10)*10,100,150);

  public static void SetPercent(double percent)
  {
   Percent=Normalize(percent);
   if(Application.Current==null)return;
   foreach(Window window in Application.Current.Windows)ApplyTree(window);
  }

  public static void ApplyTree(DependencyObject root)
  {
   var visited=new HashSet<DependencyObject>();
   ApplyTree(root,visited);
  }

  public static void ApplyElement(DependencyObject? element)
  {
   if(element==null)return;
   var property=FontSizeProperty(element);
   if(property==null)return;
   var baseline=(double)element.GetValue(BaseFontSizeProperty);
   if(double.IsNaN(baseline))
   {
    var local=element.ReadLocalValue(property);
    if(local is double size)baseline=size;
    else if(element is Window)baseline=12;
    else
    {
     var source=DependencyPropertyHelper.GetValueSource(element,property);
     if(source.BaseValueSource==BaseValueSource.Inherited)return;
     baseline=(double)element.GetValue(property);
    }
    element.SetValue(BaseFontSizeProperty,baseline);
   }
   element.SetValue(property,baseline*Percent/100d);
  }

  private static void ApplyTree(DependencyObject element,HashSet<DependencyObject> visited)
  {
   if(!visited.Add(element))return;
   ApplyElement(element);
   if(element is Visual||element is Visual3D)
   {
    var count=VisualTreeHelper.GetChildrenCount(element);
    for(var index=0;index<count;index++)ApplyTree(VisualTreeHelper.GetChild(element,index),visited);
   }
   foreach(var child in LogicalTreeHelper.GetChildren(element))if(child is DependencyObject dependency)ApplyTree(dependency,visited);
  }

  private static DependencyProperty? FontSizeProperty(DependencyObject element)
  {
   if(element is TextBlock)return TextBlock.FontSizeProperty;
   if(element is Control)return Control.FontSizeProperty;
   return null;
  }
 }
}
