using System.CommandLine;
using DevCleaner.Core;
using DevCleaner.Core.Rules;
using DevCleaner.Infrastructure;
using Spectre.Console;

namespace DevCleaner.Cli.Commands;

internal static class ConfigCommands
{
    public static Command Exclude(AppHost host)
    {
        var path = new Argument<string>("path") { Description = "Path or glob (e.g. C:\\work\\keep-me or *\\legacy\\*)" };
        var reason = new Option<string?>("--reason");
        var add = new Command("add", "Never clean this path again") { path, reason };
        add.SetAction(pr =>
        {
            var ok = host.Store.AddExclusion(pr.GetValue(path)!, pr.GetValue(reason));
            AnsiConsole.MarkupLine(ok ? "[green]Added.[/]" : "[grey]Already excluded.[/]");
        });

        var removePath = new Argument<string>("path");
        var remove = new Command("remove", "Remove an exclusion") { removePath };
        remove.SetAction(pr =>
        {
            var ok = host.Store.RemoveExclusion(pr.GetValue(removePath)!);
            AnsiConsole.MarkupLine(ok ? "[green]Removed.[/]" : "[grey]Not found.[/]");
            return ok ? Output.Ok : Output.Partial;
        });

        var list = new Command("list", "List exclusions");
        list.SetAction(_ =>
        {
            var table = new Table().AddColumn("Pattern").AddColumn("Reason").AddColumn("Added");
            foreach (var e in host.Store.LoadExclusions().Items)
                table.AddRow(Markup.Escape(e.Pattern), Markup.Escape(e.Reason ?? ""), Output.Date(e.AddedUtc));
            AnsiConsole.Write(table);
        });

        return new Command("exclude", "Manage excluded projects/folders") { add, remove, list };
    }

    public static Command Roots(AppHost host)
    {
        var addPath = new Argument<string>("path");
        var add = new Command("add", "Add a project root folder") { addPath };
        add.SetAction(pr =>
        {
            var s = host.Store.LoadSettings();
            var p = Path.GetFullPath(pr.GetValue(addPath)!);
            if (!Directory.Exists(p))
            {
                AnsiConsole.MarkupLine($"[red]Folder not found:[/] {Markup.Escape(p)}");
                return Output.Error;
            }

            if (!s.ProjectRoots.Contains(p, OsInfo.PathComparer))
                s.ProjectRoots.Add(p);
            host.Store.SaveSettings(s);
            return Output.Ok;
        });

        var removePath = new Argument<string>("path");
        var remove = new Command("remove", "Remove a project root folder") { removePath };
        remove.SetAction(pr =>
        {
            var s = host.Store.LoadSettings();
            var p = Path.GetFullPath(pr.GetValue(removePath)!);
            s.ProjectRoots.RemoveAll(r => r.Equals(p, OsInfo.PathComparison));
            host.Store.SaveSettings(s);
        });

        var list = new Command("list", "List project roots (effective)");
        list.SetAction(_ =>
        {
            foreach (var r in host.EffectiveProjectRoots())
                AnsiConsole.WriteLine(r);
        });

        var days = new Argument<int>("days");
        var stale = new Command("stale-days", "Set the number of days after which a project is considered stale") { days };
        stale.SetAction(pr =>
        {
            var s = host.Store.LoadSettings();
            s.StaleDays = Math.Max(1, pr.GetValue(days));
            host.Store.SaveSettings(s);
        });

        return new Command("roots", "Manage project root folders and stale threshold") { add, remove, list, stale };
    }

    public static Command Stats(AppHost host)
    {
        var cmd = new Command("stats", "Show lifetime cleanup statistics");
        cmd.SetAction(_ =>
        {
            var s = host.Store.LoadStats();
            AnsiConsole.MarkupLine($"[bold]Total freed:[/] {Bytes.Format(s.TotalBytesFreed)} in {s.Runs} runs (since {Output.Date(s.FirstCleanUtc)}, last {Output.Date(s.LastCleanUtc)})");
            if (s.ByCategory.Count == 0)
                return;
            var chart = new BreakdownChart().Width(60).HideTagValues();
            var colors = new[] { Color.Blue, Color.Green, Color.Yellow, Color.Red, Color.Aqua, Color.Fuchsia, Color.Orange1, Color.Purple, Color.Teal, Color.Olive, Color.Silver, Color.Maroon };
            var i = 0;
            foreach (var (cat, bytes) in s.ByCategory.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
                chart.AddItem($"{cat} ({Bytes.Format(bytes)})", bytes / 1024d / 1024d, colors[i++ % colors.Length]);
            AnsiConsole.Write(chart);
        });
        return cmd;
    }

    public static Command History(AppHost host)
    {
        var last = new Option<int>("--last") { DefaultValueFactory = _ => 10 };
        var details = new Option<bool>("--details") { Description = "Show every item of each run" };
        var cmd = new Command("history", "Show past cleanup runs") { last, details };
        cmd.SetAction(pr =>
        {
            foreach (var run in host.Store.LoadHistory(pr.GetValue(last)))
            {
                AnsiConsole.MarkupLine($"[bold]{run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}[/]  freed {Bytes.Format(run.TotalBytesFreed)}  ({run.Items.Count} items)");
                if (!pr.GetValue(details))
                    continue;
                foreach (var i in run.Items)
                    AnsiConsole.MarkupLine($"   {i.Status,-8} {Bytes.Format(i.BytesFreed),10}  {Markup.Escape(i.Path)}");
            }
        });
        return cmd;
    }

    public static Command Rules(AppHost host)
    {
        var list = new Command("list", "List loaded rules");
        list.SetAction(_ =>
        {
            var table = new Table().AddColumn("Id").AddColumn("Name").AddColumn("Category").AddColumn("Risk").AddColumn("Kind");
            foreach (var r in host.Rules)
                table.AddRow(r.Id, Markup.Escape(r.Name), r.Category.ToString(), Output.Risk(r.Risk), r.IsProjectRule ? "project" : "path");
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"Custom rules folder: {Markup.Escape(host.Store.RulesDirectory)}");
            Output.Warnings(host.Warnings);
        });

        var file = new Argument<string>("file");
        var validate = new Command("validate", "Validate a custom rule file") { file };
        validate.SetAction(pr =>
        {
            var errors = new List<string>();
            var rules = RuleLoader.LoadFile(pr.GetValue(file)!, errors);
            AnsiConsole.MarkupLine($"{rules.Count} valid rule(s)");
            Output.Warnings(errors);
            return errors.Count == 0 ? Output.Ok : Output.Error;
        });

        return new Command("rules", "Inspect cleanup rules") { list, validate };
    }
}
