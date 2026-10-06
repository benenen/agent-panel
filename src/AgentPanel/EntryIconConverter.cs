using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AgentPanel;

public sealed class EntryIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        StreamGeometry.Parse(value is true ? "M3,7 V20 H21 V7 H12 L10,4 H3 Z" : "M6,3 H15 L20,8 V21 H6 Z M15,3 V8 H20");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
