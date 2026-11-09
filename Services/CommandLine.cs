using System.Runtime.InteropServices;
using IconSwapper.Models;

namespace IconSwapper.Services;

public static class CommandLine
{
    public static int Run(string[] args)
    {
        AttachConsole();
        var logPath = Path.Combine(Path.GetTempPath(), "iconcraft-command.txt");
        try
        {
            File.WriteAllText(logPath, "");
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Usage();
                return args.Length == 0 ? 1 : 0;
            }

            if (args[0] == "--preview")
            {
                var index = AssociationReader.Load();
                foreach (var option in index.BuildChoices(args.Skip(1)))
                {
                    var detail = option.Detail;
                    WriteLine(
                        detail.Extension
                        + "\teffective=" + detail.EffectiveProgId
                        + "\thandler=" + detail.HandlerExe
                        + "\tname=" + detail.HandlerName
                        + "\tshared=" + option.Shared
                        + "\tprogids=" + string.Join("|", detail.ProgIds));
                }

                return 0;
            }

            if (args[0] == "--restore-shortcuts")
            {
                if (args.Length < 2)
                {
                    Usage();
                    return 1;
                }

                foreach (var exe in args.Skip(1))
                    WriteLine(IconApplier.RestoreShortcuts(exe));
                ShellRefresh.Notify();
                return 0;
            }

            if (args[0] == "--restore")
            {
                if (args.Length < 2)
                {
                    Usage();
                    return 1;
                }

                foreach (var arg in args.Skip(1))
                {
                    var extension = IconPaths.NormalizeExtension(arg)
                        ?? throw new InvalidOperationException("Not an extension: " + arg);
                    WriteLine(IconApplier.RestoreExtension(extension));
                }

                ShellRefresh.Notify();
                return 0;
            }

            if (args[0] != "--apply" || args.Length < 3)
            {
                Usage();
                return 1;
            }

            var image = args[1];
            var extensions = new List<string>();
            var exes = new List<string>();
            var shortcuts = false;
            for (var i = 2; i < args.Length; i++)
            {
                if (args[i] == "--shortcuts")
                {
                    shortcuts = true;
                    continue;
                }

                if (args[i] == "--exe")
                {
                    if (i + 1 >= args.Length)
                        throw new InvalidOperationException("--exe needs a path.");
                    exes.Add(args[++i]);
                    continue;
                }

                extensions.Add(args[i]);
            }

            var reader = AssociationReader.Load();
            var options = reader.BuildChoices(extensions);
            var ico = IconConverter.Save(IconConverter.ToIco(image));
            if (shortcuts && exes.Count == 0)
            {
                exes = options
                    .Select(option => option.Detail.HandlerExe)
                    .Where(exe => !string.IsNullOrWhiteSpace(exe))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var report = IconApplier.Apply(new ApplyRequest
            {
                Extensions = options.Select(option => option.Detail).ToList(),
                ExtensionsUsing = reader.ExtensionsUsing,
                IcoPath = ico,
                UpdateShortcuts = shortcuts || exes.Count > 0,
                ShortcutExes = exes,
            });
            ShellRefresh.Notify();
            WriteLine(report.Message);
            WriteLine(ico);
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void WriteLine(string text)
    {
        Console.WriteLine(text);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "iconcraft-command.txt"), text + Environment.NewLine);
    }

    private static void Usage()
    {
        Console.WriteLine("IconSwapper --apply <image> <extension> [extension...] [--shortcuts] [--exe <path>]");
        Console.WriteLine("IconSwapper --restore <extension> [extension...]");
    }

    private static void AttachConsole()
    {
        if (!AttachConsole(AttachParentProcess))
            return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
