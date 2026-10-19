using Terminal.Gui;
using Attribute = Terminal.Gui.Attribute;

namespace SmartInspectTui.UI;

/// <summary>
/// Configures a somber, low-contrast dark theme with black backgrounds across all Terminal.Gui views.
/// </summary>
public static class DarkTheme
{
    public static ColorScheme BaseScheme { get; private set; } = default!;
    public static ColorScheme InputScheme { get; private set; } = default!;
    public static ColorScheme ButtonScheme { get; private set; } = default!;
    public static ColorScheme MenuScheme { get; private set; } = default!;

    public static void Apply()
    {
        var driver = Application.Driver;
        if (driver == null)
        {
            return;
        }

        Attribute Make(Color fg, Color bg) => driver.MakeAttribute(fg, bg);

        BaseScheme = new ColorScheme
        {
            Normal = Make(Color.Gray, Color.Black),
            Focus = Make(Color.White, Color.DarkGray),
            HotNormal = Make(Color.Cyan, Color.Black),
            HotFocus = Make(Color.BrightCyan, Color.DarkGray),
            Disabled = Make(Color.DarkGray, Color.Black),
        };

        InputScheme = new ColorScheme
        {
            Normal = Make(Color.White, Color.Black),
            Focus = Make(Color.White, Color.DarkGray),
            HotNormal = Make(Color.Cyan, Color.Black),
            HotFocus = Make(Color.BrightCyan, Color.DarkGray),
            Disabled = Make(Color.DarkGray, Color.Black),
        };

        ButtonScheme = new ColorScheme
        {
            Normal = Make(Color.Gray, Color.Black),
            Focus = Make(Color.White, Color.DarkGray),
            HotNormal = Make(Color.Cyan, Color.Black),
            HotFocus = Make(Color.BrightCyan, Color.DarkGray),
            Disabled = Make(Color.DarkGray, Color.Black),
        };

        MenuScheme = new ColorScheme
        {
            Normal = Make(Color.Gray, Color.Black),
            Focus = Make(Color.White, Color.DarkGray),
            HotNormal = Make(Color.Cyan, Color.Black),
            HotFocus = Make(Color.BrightCyan, Color.DarkGray),
            Disabled = Make(Color.DarkGray, Color.Black),
        };

        var errorScheme = new ColorScheme
        {
            Normal = Make(Color.Red, Color.Black),
            Focus = Make(Color.BrightRed, Color.DarkGray),
            HotNormal = Make(Color.BrightRed, Color.Black),
            HotFocus = Make(Color.White, Color.Red),
            Disabled = Make(Color.DarkGray, Color.Black),
        };

        Colors.TopLevel = BaseScheme;
        Colors.Base = BaseScheme;
        Colors.Dialog = BaseScheme;
        Colors.Menu = MenuScheme;
        Colors.Error = errorScheme;
    }
}
