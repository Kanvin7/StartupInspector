using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StartupInspector.App;

/// <summary>
/// 窗口的 DWM 外观:系统材质、圆角、边框色、标题栏明暗。直接调原生属性,无依赖。
///
/// 已知边界:材质只作用在非客户区(标题栏)。WPF 的窗口表面是不透明的,客户区的透明
/// 像素会被画成黑色,所以这里不让出窗口背景 —— 客户区仍由应用自己绘制,否则会变成黑洞。
/// 老系统上这些属性不存在,调用失败即静默跳过,窗口保持默认外观。
/// </summary>
internal static class WindowMaterial
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int BorderColor = 34;
    private const int SystemBackdropType = 38;

    private const int BackdropMica = 2;         // DWMSBT_MAINWINDOW
    private const int CornerRound = 2;          // DWMWCP_ROUND
    private const int Dark = 1;                 // 与深色界面一致
    private const int DarkBorder = 0x003A3A3A; // COLORREF 0x00BBGGRR = #3A3A3A

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        Set(handle, SystemBackdropType, BackdropMica);
        Set(handle, UseImmersiveDarkMode, Dark);
        Set(handle, WindowCornerPreference, CornerRound);
        Set(handle, BorderColor, DarkBorder);
    }

    private static void Set(IntPtr handle, int attribute, int value)
        => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
}
