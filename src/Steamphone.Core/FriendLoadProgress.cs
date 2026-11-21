using SteamKit2;

namespace Steamphone.Core;

/// <summary>Tracks partial Steam packets without confusing the friend ID list with loaded personas.</summary>
public sealed class FriendLoadProgress
{
    public const EClientPersonaStateFlag RequiredFields = EClientPersonaStateFlag.PlayerName |
        EClientPersonaStateFlag.Presence | EClientPersonaStateFlag.GameExtraInfo | EClientPersonaStateFlag.LastSeen;
    public static readonly TimeSpan LoadBudget = TimeSpan.FromSeconds(25);
    private readonly Dictionary<ulong, EClientPersonaStateFlag> received = [];

    public void Receive(ulong id, EClientPersonaStateFlag fields) => received[id] = received.GetValueOrDefault(id) | fields;
    public bool IsLoaded(ulong id) => (received.GetValueOrDefault(id) & RequiredFields) == RequiredFields;
    public void Retain(ISet<ulong> ids)
    {
        foreach (var id in received.Keys.Where(x => !ids.Contains(x)).ToArray()) received.Remove(id);
    }
    public void Clear() => received.Clear();
}
