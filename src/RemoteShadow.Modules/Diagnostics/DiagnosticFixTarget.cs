using RemoteShadow.Core.Actions;
using RemoteShadow.Core.Models;

namespace RemoteShadow.Modules;

/// <summary>Цель кнопки «Исправить»: проверка диагностики конкретного компьютера.</summary>
public sealed record DiagnosticFixTarget(ComputerInfo Computer, DiagnosticItem Item) : IActionTarget
{
    public string Kind => "computer";
    public string Label => $"{Computer.Name}: {Item.Title}";
}
