namespace Desensitizer
{
 public static class ProductVersion
 {
  public static string Label
  {
   get
   {
    var version=typeof(App).Assembly.GetName().Version;
    return version==null?"AI Desensitizer":"AI Desensitizer v"+version.Major+"."+version.Minor+"."+version.Build;
   }
  }
 }
}
