using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RemoteShadow.App.Shell.Actions;

/// <summary>
/// Панель кнопок действий, сгруппированных по <see cref="ActionButtonModel.Group"/>, с индикатором выполнения.
/// <code>
/// &lt;actions:ActionBar Actions="{Binding Buttons}" IsBusy="{Binding Presenter.IsBusy}" BusyText="{Binding Presenter.BusyText}" /&gt;
/// </code>
/// Список читается при смене значения <see cref="Actions"/>; если это <see cref="INotifyCollectionChanged"/>, то и при
/// его изменении.
/// </summary>
public partial class ActionBar : UserControl
{
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(IEnumerable<ActionButtonModel>),
        typeof(ActionBar),
        new PropertyMetadata(null, OnActionsChanged));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(ActionBar), new PropertyMetadata(false));

    public static readonly DependencyProperty BusyTextProperty = DependencyProperty.Register(
        nameof(BusyText), typeof(string), typeof(ActionBar), new PropertyMetadata(null));

    public ActionBar()
    {
        InitializeComponent();
    }

    /// <summary>Кнопки для показа (обычно результат <c>ActionPresenter.BuildButtons</c>).</summary>
    public IEnumerable<ActionButtonModel>? Actions
    {
        get => (IEnumerable<ActionButtonModel>?)GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>Показывать индикатор выполнения над кнопками.</summary>
    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Подпись под индикатором: что выполняется и как идёт пачка.</summary>
    public string? BusyText
    {
        get => (string?)GetValue(BusyTextProperty);
        set => SetValue(BusyTextProperty, value);
    }

    private static void OnActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (ActionBar)d;
        if (e.OldValue is INotifyCollectionChanged oldNotify) oldNotify.CollectionChanged -= bar.OnActionsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNotify) newNotify.CollectionChanged += bar.OnActionsCollectionChanged;
        bar.Rebuild();
    }

    private void OnActionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var snapshot = Actions?.ToList() ?? new List<ActionButtonModel>();

        // Группы идут в порядке первого появления: IActionRunner уже отсортировал действия по группам.
        var view = new ListCollectionView(snapshot);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionButtonModel.Group)));

        GroupsHost.ItemsSource = view;
        EmptyText.Visibility = snapshot.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
