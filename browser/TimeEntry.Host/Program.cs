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

    /// <summary>
    /// Forwards a command to the F# kernel. Same contract: string in, string
    /// out, no decisions here.
    /// </summary>
    [JSExport]
    internal static string Dispatch(string requestJson) => TimeEntry.Kernel.dispatch(requestJson);

    /// <summary>
    /// Forwards a request for the six-minute duration options.
    /// </summary>
    [JSExport]
    internal static string DurationGrid(string requestJson) =>
        TimeEntry.Kernel.durationGrid(requestJson);

    /// <summary>
    /// Forwards a split preview request.
    /// </summary>
    [JSExport]
    internal static string SplitPreview(string requestJson) =>
        TimeEntry.Kernel.splitPreview(requestJson);

    /// <summary>
    /// Forwards a request for the catalogue's selectable choices.
    /// </summary>
    [JSExport]
    internal static string CatalogueChoices(string requestJson) =>
        TimeEntry.Kernel.catalogueChoices(requestJson);
}

public static class Program
{
    public static void Main() { }
}
