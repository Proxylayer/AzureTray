using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Http;

// The one shape Graph and ARM do not agree on. Both return a page as an object
// carrying the items plus a link to the next page, but Graph names the link
// "@odata.nextLink" and ARM names it "nextLink" — so each service keeps its own
// record (and, for Graph, its own JsonPropertyName mapping) and only the two
// members the paging loop actually reads are unified here.
internal interface IPageEnvelope<T>
{
    List<T>? Value { get; }

    string? NextLink { get; }
}
