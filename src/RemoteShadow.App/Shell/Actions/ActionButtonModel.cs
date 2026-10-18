using System.Windows.Input;
using RemoteShadow.Core.Actions;

namespace RemoteShadow.App.Shell.Actions;

/// <summary>
/// Одна кнопка действия (и пункт контекстного меню). Страницы не создают их вручную: список собирает
/// <see cref="ActionPresenter{TTarget}.BuildButtons"/>, а выводят <see cref="ActionBar"/> и <see cref="ActionMenuBuilder"/>.
/// </summary>
/// <param name="Id"><see cref="IAction{TTarget}.Id"/>.</param>
/// <param name="Title">Подпись; для нескольких целей — с числом: «Перезагрузить (3)».</param>
/// <param name="Group">Заголовок группы кнопок («Подключение», «Сессия»).</param>
/// <param name="Icon">Имя значка (Tabler) для <see cref="IconMap"/>; null — без значка.</param>
/// <param name="Description">Подсказка к доступной кнопке.</param>
/// <param name="IsEnabled">false — кнопка видна, но нажать нельзя; причина — в <paramref name="DisabledReason"/>.</param>
/// <param name="DisabledReason">Почему действие недоступно (подсказка вместо описания); null, если причина не нужна.</param>
/// <param name="Command">Запускает действие. Недоступна, пока идёт другое действие того же <see cref="ActionPresenter{TTarget}"/>.</param>
/// <param name="Danger">Опасность действия — страница может подсветить кнопку.</param>
public sealed record ActionButtonModel(
    string Id,
    string Title,
    string Group,
    string? Icon,
    string? Description,
    bool IsEnabled,
    string? DisabledReason,
    ICommand Command,
    ActionDanger Danger = ActionDanger.None)
{
    /// <summary>Группа по умолчанию, если действие не назвало свою.</summary>
    public const string DefaultGroup = "Действия";

    /// <summary>Группа для показа: пустое имя заменяется на <see cref="DefaultGroup"/>.</summary>
    public string Group { get; init; } = string.IsNullOrWhiteSpace(Group) ? DefaultGroup : Group;

    public bool HasIcon => !string.IsNullOrWhiteSpace(Icon);

    public bool IsDangerous => Danger != ActionDanger.None;

    /// <summary>Подсказка: у недоступной кнопки — причина (если есть), иначе описание.</summary>
    public string? ToolTipText
    {
        get
        {
            var text = IsEnabled ? Description : DisabledReason ?? Description;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }
}
