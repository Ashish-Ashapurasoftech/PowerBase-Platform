namespace PowerBase.Domain.ValueObjects;

/// <summary>
/// App-level structural layout preferences (App Settings → Styles/Layout). No user-level
/// tier exists for these — unlike appearance, they are not personalizable per account.
/// </summary>
public class AppLayoutSettings
{
    /// <summary>expanded | collapsed | mini | floating</summary>
    public string SidebarStyle { get; set; } = "collapsed";

    /// <summary>left | top</summary>
    public string NavPosition { get; set; } = "left";

    /// <summary>full | boxed</summary>
    public string ContentWidth { get; set; } = "full";

    /// <summary>rounded | sharp | bordered | shadowed</summary>
    public string PanelStyle { get; set; } = "rounded";

    /// <summary>fixed | static</summary>
    public string HeaderBehavior { get; set; } = "fixed";
}
