using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PredictionBot;

class MainWindow : Window
{
    static readonly IBrush Bg = Brush.Parse("#18181B"), Card = Brush.Parse("#26262C"),
                           Purple = Brush.Parse("#9147FF"), Lilac = Brush.Parse("#BF94FF"),
                           White = Brush.Parse("#EFEFF1"), Muted = Brush.Parse("#ADADB8");

    const string AllChannels = "All channels";

    readonly Db _db;
    readonly IngestServer _server;
    readonly string? _serverError;
    readonly ComboBox _channels = new() { MinWidth = 150, FontSize = 12 };
    readonly TextBlock _status = Text("", 11, Muted);
    readonly StackPanel _body = new() { Spacing = 12 };
    bool _updatingChannels;

    public MainWindow(Db db, IngestServer server)
    {
        _db = db;
        _server = server;

        Title = "Prediction Bot";
        Width = 520;
        Height = 580;
        MinWidth = 440;
        Background = Bg;

        var header = new DockPanel();
        DockPanel.SetDock(_channels, Dock.Right);
        header.Children.Add(_channels);
        header.Children.Add(Text("PREDICTION BOT", 15, Purple, bold: true));

        _status.Margin = new Thickness(0, 6, 0, 12);
        _status.TextWrapping = TextWrapping.Wrap;

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = _body });
        Content = root;

        _channels.SelectionChanged += (_, _) => { if (!_updatingChannels) Refresh(); };
        _server.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _serverError = _server.Start();

        // Hook status goes stale on its own when a tab closes, so re-check periodically.
        new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Background, (_, _) => Refresh()).Start();
        Refresh();
    }

    void Refresh()
    {
        var all = _db.Load();

        var names = new List<string> { AllChannels };
        names.AddRange(all.Select(p => p.Channel).Where(c => c != "").Distinct().Order());
        var selected = _channels.SelectedItem as string ?? AllChannels;
        if (!names.Contains(selected)) selected = AllChannels;
        if (_channels.ItemsSource is not List<string> current || !current.SequenceEqual(names))
        {
            _updatingChannels = true;
            _channels.ItemsSource = names;
            _channels.SelectedItem = selected;
            _updatingChannels = false;
        }

        var predictions = selected == AllChannels ? all : all.Where(p => p.Channel == selected).ToList();
        var resolved = predictions.Count(p => p.Status == "RESOLVED");
        var hooked = _server.HookedChannels();
        _status.Text = _serverError
            ?? (hooked.Count > 0 ? $"● Hooked: {string.Join(", ", hooked)}" : "○ No Twitch tab hooked — is the extension loaded?")
               + $"   ·   {resolved} resolved prediction{(resolved == 1 ? "" : "s")}";
        _status.Foreground = hooked.Count > 0 && _serverError is null ? Lilac : Muted;

        var (byOdds, byChoice) = Stats.Compute(predictions);

        _body.Children.Clear();
        _body.Children.Add(BestCard(Stats.Best(byOdds.Concat(byChoice))));
        _body.Children.Add(Table("BY ODDS", byOdds));
        _body.Children.Add(Table("BY CHOICE", byChoice));
        if (predictions.LastOrDefault(p => p.Status is "ACTIVE" or "LOCKED") is { } live)
            _body.Children.Add(LiveCard(live));
    }

    static Control BestCard(GroupStat? best)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(Text("BEST HISTORICAL PICK", 10, Lilac, bold: true));
        if (best is null)
        {
            stack.Children.Add(Text("Not enough data yet", 16, White, bold: true));
            stack.Children.Add(Text("Needs at least 5 resolved bets in a group.", 11, Muted));
        }
        else
        {
            stack.Children.Add(Text(best.Name, 16, White, bold: true));
            stack.Children.Add(Text(
                $"Expected return {Signed(best.BayesRoi)} per point  ·  {Pct(best.PEdge)} chance it's profitable  ·  n={best.N}",
                11, Muted));
            if (best.BayesRoi <= 0)
                stack.Children.Add(Text("Nothing has a positive expected return so far — this is just the least bad.", 11, Muted));
        }
        return Boxed(stack, accent: true);
    }

    static Control Table(string heading, List<GroupStat> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto") };

        void Row(IBrush color, bool bold, params string[] cells)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var col = 0; col < cells.Length; col++)
            {
                // The return column is the one that matters, so it gets the accent when positive.
                var brush = col == 4 && !bold && cells[col].StartsWith('+') ? Lilac : color;
                var cell = Text(cells[col], bold ? 10 : 12, brush, bold);
                cell.Margin = new Thickness(col == 0 ? 0 : 12, 3, 0, 3);
                cell.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, col);
                grid.Children.Add(cell);
            }
        }

        Row(Muted, true, heading, "W / N", "WIN % (95%)", "CROWD", "RETURN", "P(PROFIT)");
        foreach (var g in rows)
        {
            if (g.N == 0) Row(Muted, false, g.Name, "0 / 0", "–", "–", "–", "–");
            else Row(White, false, g.Name, $"{g.Wins} / {g.N}", $"{Pct(g.Mean)} ({g.Lo * 100:0}–{g.Hi * 100:0})",
                     Pct(g.Crowd), Signed(g.Roi), Pct(g.PEdge));
        }
        return Boxed(grid);
    }

    static Control LiveCard(PredictionRow p)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(Text($"{p.Status} NOW · {p.Channel}", 10, Lilac, bold: true));
        var title = Text(p.Title, 13, White, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(title);

        double pool = p.Outcomes.Sum(o => o.Points);
        foreach (var o in p.Outcomes)
        {
            var odds = o.Points > 0 ? $"{Pct(o.Points / pool)} of pool · pays {pool / o.Points:0.00}x" : "no bets yet";
            stack.Children.Add(Text($"{o.Title} — {odds}", 12, Muted));
        }
        return Boxed(stack);
    }

    static Border Boxed(Control child, bool accent = false) => new()
    {
        Child = child,
        Background = Card,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 10),
        BorderBrush = Purple,
        BorderThickness = new Thickness(accent ? 3 : 0, 0, 0, 0),
    };

    static TextBlock Text(string text, double size, IBrush color, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static string Pct(double x) => $"{x * 100:0}%";
    static string Signed(double x) => $"{x * 100:+0;-0;0}%";
}
