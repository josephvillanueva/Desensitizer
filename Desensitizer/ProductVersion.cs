namespace Desensitizer
{
 public static class ProductVersion
 {
  public static string Label
  {
   get
   {
    var version=typeof(App).Assembly.GetName().Version;
    return version==null?"Desensitizer":"Desensitizer v"+version.Major+"."+version.Minor;
   }
  }
 }
}
