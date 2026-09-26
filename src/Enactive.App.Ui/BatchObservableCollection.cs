namespace Enactive.App.Ui;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

internal sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var value in snapshot) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void AppendTail(IEnumerable<T> values, int limit)
    {
        var incoming = values.ToArray();
        if (Count + incoming.Length > limit)
            ReplaceWith(this.Concat(incoming).TakeLast(limit));
        else foreach (var value in incoming) Add(value);
    }
}
