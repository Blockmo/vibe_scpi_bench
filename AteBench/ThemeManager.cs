using System;
using System.Windows;
using Microsoft.Win32;

namespace AteBench
{
    public enum AppTheme { Dark, Light }

    /// <summary>
    /// 切換深色 / 淺色主題: 替換應用程式資源中的調色盤 (Palette.*.xaml)，
    /// 所有以 DynamicResource 參照顏色的元件會立即更新。選擇會記在登錄檔，下次啟動沿用。
    /// </summary>
    public static class ThemeManager
    {
        const string SettingsKey = @"Software\ATE Bench";
        const string ThemeValue = "Theme";

        static ResourceDictionary _palette;

        public static AppTheme Current { get; private set; }

        /// <summary>使用者選擇的主題 (未選過則依 Windows 設定)。命令列模式沒有初始化視窗時也可用。</summary>
        public static AppTheme Preferred
        {
            get { return LoadSaved() ?? SystemTheme(); }
        }
        public static event EventHandler Changed;

        /// <summary>載入樣式與上次選擇的主題 (第一次使用時依 Windows 的應用程式色彩設定)。</summary>
        public static void Initialize(Application app)
        {
            Current = LoadSaved() ?? SystemTheme();
            _palette = Palette(Current);
            app.Resources.MergedDictionaries.Add(_palette);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AteBench;component/Theme.xaml", UriKind.Relative) });
        }

        public static void Apply(AppTheme theme)
        {
            if (theme == Current && _palette != null) return;
            var dicts = Application.Current.Resources.MergedDictionaries;
            var next = Palette(theme);
            int at = _palette != null ? dicts.IndexOf(_palette) : -1;
            if (at >= 0) dicts[at] = next;
            else dicts.Insert(0, next);
            _palette = next;
            Current = theme;
            Save(theme);
            var h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        static ResourceDictionary Palette(AppTheme theme)
        {
            string file = theme == AppTheme.Light ? "Palette.Light.xaml" : "Palette.Dark.xaml";
            return new ResourceDictionary { Source = new Uri("/AteBench;component/" + file, UriKind.Relative) };
        }

        static AppTheme? LoadSaved()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    AppTheme t;
                    string v = k != null ? k.GetValue(ThemeValue) as string : null;
                    if (v != null && Enum.TryParse(v, out t)) return t;
                }
            }
            catch (Exception) { /* 讀不到設定就用預設值 */ }
            return null;
        }

        static void Save(AppTheme theme)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue(ThemeValue, theme.ToString());
            }
            catch (Exception) { /* 設定存不下來不影響使用 */ }
        }

        /// <summary>Windows「選擇您的預設應用程式模式」。</summary>
        static AppTheme SystemTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    if (v is int && (int)v == 1) return AppTheme.Light;
                }
            }
            catch (Exception) { }
            return AppTheme.Dark;
        }
    }
}
