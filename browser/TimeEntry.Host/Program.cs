using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;

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
    /// Forwards a request for one entry's history.
    /// </summary>
    [JSExport]
    internal static string EntryHistory(string requestJson) =>
        TimeEntry.Kernel.entryHistory(requestJson);

    /// <summary>
    /// Forwards a daily review request.
    /// </summary>
    [JSExport]
    internal static string ReviewDay(string requestJson) => TimeEntry.Kernel.reviewDay(requestJson);

    /// <summary>
    /// Forwards a month summary request.
    /// </summary>
    [JSExport]
    internal static string ViewMonth(string requestJson) => TimeEntry.Kernel.viewMonth(requestJson);

    /// <summary>
    /// Forwards a request for the six-minute duration options.
    /// </summary>
    [JSExport]
    internal static string DurationGrid(string requestJson) =>
        TimeEntry.Kernel.durationGrid(requestJson);

    /// <summary>
    /// Forwards a command that should also be persisted. Returns a Task, which
    /// reaches JavaScript as a Promise — the only export here that is not
    /// synchronous, because it is the only one that performs I/O.
    /// </summary>
    [JSExport]
    // The marshalling is stated rather than inferred: the interop source
    // generator supports a Task only when told what it resolves to, and
    // without this it silently generates a shim that cannot return the value.
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static Task<string> Persist(string requestJson) =>
        FSharpAsync.StartAsTask(
            TimeEntry.Kernel.persist(requestJson),
            FSharpOption<TaskCreationOptions>.None,
            FSharpOption<CancellationToken>.None);

    /// <summary>
    /// Forwards a request to read the ledger and catalogue from the
    /// repository. Asynchronous for the same reason as Persist.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static Task<string> LoadLedger(string requestJson) =>
        FSharpAsync.StartAsTask(
            TimeEntry.Kernel.loadLedger(requestJson),
            FSharpOption<TaskCreationOptions>.None,
            FSharpOption<CancellationToken>.None);

    /// <summary>
    /// Forwards a request to read one entry, for reviewing a stale write.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static Task<string> ReviewEntry(string requestJson) =>
        FSharpAsync.StartAsTask(
            TimeEntry.Kernel.reviewEntry(requestJson),
            FSharpOption<TaskCreationOptions>.None,
            FSharpOption<CancellationToken>.None);

    /// <summary>
    /// Forwards a request to set or clear the monthly tracking target.
    /// Asynchronous because it writes to the repository.
    /// </summary>
    [JSExport]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static Task<string> SetMonthlyTarget(string requestJson) =>
        FSharpAsync.StartAsTask(
            TimeEntry.Kernel.setMonthlyTarget(requestJson),
            FSharpOption<TaskCreationOptions>.None,
            FSharpOption<CancellationToken>.None);

    /// <summary>
    /// Forwards a request for what to say about the current sign-in.
    /// </summary>
    [JSExport]
    internal static string IdentityView(string requestJson) =>
        TimeEntry.Kernel.identityView(requestJson);

    /// <summary>
    /// Forwards a split preview request.
    /// </summary>
    [JSExport]
    internal static string SplitPreview(string requestJson) =>
        TimeEntry.Kernel.splitPreview(requestJson);

    /// <summary>
    /// Forwards a merge preview request.
    /// </summary>
    [JSExport]
    internal static string MergePreview(string requestJson) =>
        TimeEntry.Kernel.mergePreview(requestJson);

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
