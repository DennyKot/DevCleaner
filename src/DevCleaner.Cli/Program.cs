using System.CommandLine;
using DevCleaner.Cli.Commands;
using DevCleaner.Infrastructure;

var host = AppHost.Create();

var root = new RootCommand("DevCleaner - find and clean developer junk (build output, caches, temp files, duplicates)");
root.Subcommands.Add(ScanCommands.Scan(host));
root.Subcommands.Add(ScanCommands.Clean(host));
root.Subcommands.Add(ToolCommands.Search(host));
root.Subcommands.Add(ToolCommands.Dupes(host));
root.Subcommands.Add(ToolCommands.Usage());
root.Subcommands.Add(ConfigCommands.Exclude(host));
root.Subcommands.Add(ConfigCommands.Roots(host));
root.Subcommands.Add(ConfigCommands.Stats(host));
root.Subcommands.Add(ConfigCommands.History(host));
root.Subcommands.Add(ConfigCommands.Rules(host));

return await root.Parse(args).InvokeAsync();
