using System.Collections.ObjectModel;

namespace Ferry.App.ViewModels;

/// <summary>Making a list shown on screen what it should be without filling it again, so its items keep their place and selection.</summary>
public static class CollectionMatching
{
    /// <summary>The list as <paramref name="wanted"/> says, reached with as few changes as can be.</summary>
    public static void MatchTo<T>(this ObservableCollection<T> list, IReadOnlyList<T> wanted) where T : class
    {
        // what is going goes first, or everything after it would be moved up one by one, which loses what is selected
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(list[i]))
                list.RemoveAt(i);
        }
        var present = new HashSet<T>(list, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], wanted[i]))
                continue;
            if (present.Contains(wanted[i]))
                list.RemoveAt(list.IndexOf(wanted[i]));
            list.Insert(i, wanted[i]);
        }
    }
}
