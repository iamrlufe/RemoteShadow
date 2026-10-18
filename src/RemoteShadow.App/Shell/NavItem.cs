namespace RemoteShadow.App.Shell;

/// <summary>Пункт левой навигации: страница с названием и значком.</summary>
public sealed class NavItem
{
    public NavItem(PageRegistration registration)
    {
        Id = registration.Page.Id;
        Title = registration.Page.Title;
        Glyph = IconMap.Get(registration.Page.Icon);
    }

    public string Id { get; }
    public string Title { get; }

    /// <summary>Глиф Segoe Fluent Icons.</summary>
    public string Glyph { get; }
}
