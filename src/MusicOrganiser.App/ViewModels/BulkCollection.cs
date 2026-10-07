using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MusicOrganiser.App.ViewModels;

/// <summary>
/// An ObservableCollection whose whole contents can be swapped with one change notification.
/// Adding thousands of projects one at a time would re-sort and re-filter the list for each one,
/// and can't be done inside a CollectionView's DeferRefresh at all.
/// </summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
