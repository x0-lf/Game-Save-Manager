namespace GameSaves.App.Models
{
    // One read-only row of the Settings provider status list: the catalog's
    // display name plus its availability state, exactly as the provider
    // catalog defines it. Mirrors the shape of the option models the other
    // Settings lists already use.
    public sealed record ProviderStatusOption(string Name, string Status);
}
