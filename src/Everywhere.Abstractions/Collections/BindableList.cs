using System.Collections.ObjectModel;

namespace Everywhere.Collections;

/// <summary>
/// A simple implementation of IReadOnlyBindableList{T} that inherits from ObservableCollection{T}.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class BindableList<T> : ObservableCollection<T>, IReadOnlyBindableList<T>;
