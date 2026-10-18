using System.Windows;
using System.Windows.Controls;

namespace RemoteShadow.App.Shell.Search;

/// <summary>Подбирает шаблон строки панели поиска: заголовок секции, пояснение или результат.</summary>
public sealed class SearchItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? MessageTemplate { get; set; }
    public DataTemplate? ResultTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        SearchItem { Kind: SearchItemKind.Header } => HeaderTemplate,
        SearchItem { Kind: SearchItemKind.Message } => MessageTemplate,
        _ => ResultTemplate,
    };
}
