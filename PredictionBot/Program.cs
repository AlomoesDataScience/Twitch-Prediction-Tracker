using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PredictionBot;

class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var db = new Db(Program.DbPath);
            var server = new IngestServer(db);
            desktop.MainWindow = new MainWindow(db, server);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

static class Program
{
    // Override with PREDICTIONBOT_DB to point at a different database file.
    public static string DbPath =>
        Environment.GetEnvironmentVariable("PREDICTIONBOT_DB")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PredictionBot", "predictions.db");

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--headless"))
        {
            RunHeadless();
            return;
        }
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    // No window: just collect, and print the stats table whenever something changes.
    static void RunHeadless()
    {
        var db = new Db(DbPath);
        var server = new IngestServer(db);
        server.Changed += () =>
        {
            var (byOdds, byChoice) = Stats.Compute(db.Load());
            Console.WriteLine($"{"group",-28}{"w/n",8}{"win",6}{"95% CI",10}{"crowd",7}{"roi",7}{"P(+EV)",8}");
            foreach (var g in byOdds.Concat(byChoice))
                Console.WriteLine($"{g.Name,-28}{g.Wins + "/" + g.N,8}{g.Mean * 100,5:0}%{$"{g.Lo * 100:0}-{g.Hi * 100:0}",10}" +
                                  $"{g.Crowd * 100,6:0}%{g.Roi * 100,6:+0;-0}%{g.PEdge * 100,7:0}%");
            Console.WriteLine();
        };
        var error = server.Start();
        Console.WriteLine(error ?? $"Listening on 127.0.0.1:{IngestServer.Port}, database {DbPath}");
        if (error is null) Thread.Sleep(Timeout.Infinite);
    }
}
