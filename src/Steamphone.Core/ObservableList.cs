using System.Collections.ObjectModel;

namespace Steamphone.Core;

public static class ObservableList
{
    /// <summary>Keep row instances and scroll position when a live list changes.</summary>
    public static void Synchronize<T>(this ObservableCollection<T> items, IReadOnlyList<T> desired) where T : class
    {
        var retained = desired.ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
            if (!retained.Contains(items[i])) items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], desired[i])) continue;
            var existing = items.IndexOf(desired[i]);
            if (existing < 0) items.Insert(i, desired[i]);
            else items.Move(existing, i);
        }
    }
}
