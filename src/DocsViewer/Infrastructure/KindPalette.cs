using System.Windows.Media;
using DocsViewer.Core.Model;

namespace DocsViewer.Infrastructure;

/// <summary>Badge colour and short label for every <see cref="NodeKind"/>.</summary>
public static class KindPalette
{
    private static readonly Dictionary<NodeKind, (string Hex, string Label)> Map = new()
    {
        [NodeKind.Document] = ("#111827", "Document"),
        [NodeKind.Section] = ("#6B7280", "Section"),
        [NodeKind.Browser] = ("#475569", "Browser"),
        [NodeKind.Route] = ("#2563EB", "Route"),
        [NodeKind.Endpoint] = ("#4F46E5", "Endpoint"),
        [NodeKind.VueComponent] = ("#16A34A", "Vue"),
        [NodeKind.TypeScript] = ("#D97706", "TS"),
        [NodeKind.Html] = ("#C2410C", "HTML"),
        [NodeKind.Controller] = ("#7C3AED", "Controller"),
        [NodeKind.Middleware] = ("#6D28D9", "Middleware"),
        [NodeKind.UseCase] = ("#E11D48", "Use case"),
        [NodeKind.Service] = ("#0D9488", "Service"),
        [NodeKind.Repository] = ("#EA580C", "Repository"),
        [NodeKind.Client] = ("#0891B2", "Client"),
        [NodeKind.Store] = ("#0E7490", "Store"),
        [NodeKind.Mapper] = ("#6B7280", "Mapper"),
        [NodeKind.Interface] = ("#64748B", "Interface"),
        [NodeKind.Method] = ("#374151", "Method"),
        [NodeKind.Destination] = ("#65A30D", "Destination"),
        [NodeKind.Outcome] = ("#DC2626", "Outcome"),
        [NodeKind.Branch] = ("#9CA3AF", "Branch"),
        [NodeKind.Setting] = ("#CA8A04", "Setting"),
        [NodeKind.File] = ("#78716C", "File"),
        [NodeKind.Concept] = ("#6B7280", "Concept"),
    };

    private static readonly Dictionary<NodeKind, SolidColorBrush> Brushes = new();

    public static string Label(NodeKind kind) => Map.TryGetValue(kind, out var v) ? v.Label : kind.ToString();

    public static SolidColorBrush Brush(NodeKind kind)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue(kind, out var b)) return b;
            var hex = Map.TryGetValue(kind, out var v) ? v.Hex : "#6B7280";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Brushes[kind] = brush;
            return brush;
        }
    }

    /// <summary>Group header used in the Components view, e.g. "Controllers", "Vue components".</summary>
    public static string GroupLabel(NodeKind kind) => kind switch
    {
        NodeKind.VueComponent => "Vue components",
        NodeKind.TypeScript => "TypeScript modules",
        NodeKind.Html => "HTML pages",
        NodeKind.Controller => "Controllers",
        NodeKind.Middleware => "Middleware",
        NodeKind.UseCase => "Use cases",
        NodeKind.Service => "Services",
        NodeKind.Repository => "Repositories",
        NodeKind.Client => "Clients",
        NodeKind.Store => "Stores",
        NodeKind.Mapper => "Mappers and resolvers",
        NodeKind.Interface => "Interfaces",
        NodeKind.Method => "Methods",
        NodeKind.Destination => "Destinations",
        NodeKind.Outcome => "Outcomes",
        NodeKind.Route => "Routes",
        NodeKind.Endpoint => "Endpoints",
        NodeKind.Setting => "Settings",
        NodeKind.File => "Files",
        _ => "Other",
    };
}
