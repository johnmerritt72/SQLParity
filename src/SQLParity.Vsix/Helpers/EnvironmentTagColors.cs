using System.Windows.Media;
using SQLParity.Core.Model;

namespace SQLParity.Vsix.Helpers
{
    public static class EnvironmentTagColors
    {
        public static Color GetColor(EnvironmentTag tag)
        {
            switch (tag)
            {
                case EnvironmentTag.Prod: return Color.FromRgb(220, 53, 69);
                case EnvironmentTag.Staging: return Color.FromRgb(255, 152, 0);
                case EnvironmentTag.Dev: return Color.FromRgb(40, 167, 69);
                case EnvironmentTag.Sandbox: return Color.FromRgb(0, 123, 255);
                default: return Color.FromRgb(108, 117, 125);
            }
        }

        public static SolidColorBrush GetBrush(EnvironmentTag tag)
        {
            var brush = new SolidColorBrush(GetColor(tag));
            brush.Freeze();
            return brush;
        }
    }
}
