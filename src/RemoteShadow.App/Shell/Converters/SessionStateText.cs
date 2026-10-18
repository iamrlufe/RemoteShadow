using RemoteShadow.Core.Models;

namespace RemoteShadow.App.Shell.Converters;

/// <summary>Состояние сессии по-русски.</summary>
public static class SessionStateText
{
    public static string Get(SessionState state) => state switch
    {
        SessionState.Active => "активна",
        SessionState.Connected => "подключена",
        SessionState.ConnectQuery => "подключается",
        SessionState.Shadow => "наблюдение",
        SessionState.Disconnected => "отключена",
        SessionState.Idle => "простаивает",
        SessionState.Listen => "ожидание",
        SessionState.Reset => "сброс",
        SessionState.Down => "выключена",
        SessionState.Init => "инициализация",
        _ => "неизвестно",
    };
}
