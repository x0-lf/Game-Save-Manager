using GameSaves.Core.Sync;

namespace GameSaves.App.Models
{
    // One read-only row of the Settings provider status list: the catalog's
    // display name plus its availability state, exactly as the provider
    // catalog defines it. Mirrors the shape of the option models the other
    // Settings lists already use.
    //
    // Kind is carried so the row's Configure action can tell the Sync tab which
    // provider to select. Without it the action could navigate but not
    // preselect, which would leave the user to find the provider again.
    //
    // IsConfigurable gates the Configure action from the catalog's own
    // implemented flag rather than from the status wording, so a provider this
    // build cannot actually use can never be offered a setup route it would
    // fail to honour.
    public sealed record ProviderStatusOption(
        string Name,
        string Status,
        SyncProviderKind Kind,
        bool IsConfigurable);
}
