using System;
using System.Threading;
using System.Linq;
using System.Windows;

namespace SpacePeek;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--bench")
        {
            PreviewRenderer.Bench(args.Skip(1).ToArray());
            return;
        }

        if (args.Length >= 2 && args[0] == "--dump") { PreviewRenderer.Dump(args[1]); return; }

        if (args.Length >= 2 && args[0] == "--bench-office") { PreviewRenderer.BenchOffice(args.Skip(1).ToArray()); return; }

        if (args.Length >= 2 && args[0] == "--probe") { PreviewRenderer.Probe(args.Skip(1).ToArray()); return; }

        if (args.Length >= 3 && args[0] == "--thumb")
        {
            PreviewRenderer.RenderThumbnail(args[1], args[2]);
            return;
        }

        if (args.Length >= 3 && args[0] == "--render")
        {
            double width = args.Length >= 4 && double.TryParse(args[3], out double w) ? w : 660;
            PreviewRenderer.Render(args[1], args[2], width);
            return;
        }

        using var mutex = new Mutex(true, "SpacePeek.SingleInstance", out bool created);
        if (!created) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AppController();
        app.Startup += (_, _) => controller.Start();
        app.Exit += (_, _) => controller.Dispose();
        app.Run();
    }
}




