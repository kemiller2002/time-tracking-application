using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace TimeEntry.Host;

// The interop attributes are browser-only; the analyzer requires that to be
// declared rather than inferred from the runtime identifier.
[SupportedOSPlatform("browser")]
public static partial class Interop
{
    /// <summary>
    /// Forwards a request to the F# kernel. String in, string out: this shim
    /// holds no domain knowledge and makes no decisions.
    /// </summary>
    [JSExport]
    internal static string ViewDay(string requestJson) => TimeEntry.Kernel.viewDay(requestJson);
}

public static class Program
{
    public static void Main() { }
}
