using System;
using System.Collections.Generic;
using System.Windows;

namespace ImageShareSetup
{
    /// <summary>
    /// 主题切换器。
    ///
    /// App.xaml 的 MergedDictionaries 约定：
    ///   [0] = 色板（Dark.xaml / Light.xaml，可整体替换）
    ///   [1] = 控件样式（Controls.xaml，全程不动）
    /// 所以切主题只替换 [0]，所有 DynamicResource 引用会自动刷新。
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; } = true;

        public static void Apply(bool dark, bool persist = true)
        {
            IsDark = dark;

            ResourceDictionary dict = new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dict);
                if (!ContainsControls(merged)) merged.Add(LoadControls());
            }
            else
            {
                merged[0] = dict;
            }

            UiSettings.DarkTheme = dark;
            if (persist) UiSettings.Save();
        }

        static bool ContainsControls(IEnumerable<ResourceDictionary> merged)
        {
            foreach (ResourceDictionary d in merged)
            {
                if (d.Source != null && d.Source.OriginalString.IndexOf("Controls.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        static ResourceDictionary LoadControls()
        {
            return new ResourceDictionary { Source = new Uri("Themes/Controls.xaml", UriKind.Relative) };
        }
    }
}
