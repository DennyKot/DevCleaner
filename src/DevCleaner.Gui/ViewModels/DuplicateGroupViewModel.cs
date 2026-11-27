using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevCleaner.Core;
using DevCleaner.Core.DiskUsage;
using DevCleaner.Core.Duplicates;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Search;
using DevCleaner.Infrastructure;
using DevCleaner.Infrastructure.Persistence;

namespace DevCleaner.Gui.ViewModels;

public sealed class DuplicateGroupViewModel(DuplicateGroup group)
{
    public string Header { get; } = $"{Bytes.Format(group.Size)} x {group.Files.Count}  (wasted {Bytes.Format(group.WastedBytes)})";

    public List<SelectableFinding> Files { get; } = group.Files
        .Select(f => new SelectableFinding(new Finding
        {
            ProviderId = "duplicates",
            Category = Category.Duplicates,
            Name = Path.GetFileName(f.Path),
            Path = f.Path,
            Kind = FindingKind.File,
            SizeBytes = f.Size,
            LastModifiedUtc = f.LastWriteUtc,
            Risk = RiskLevel.Review,
            Note = f == group.Keeper ? "keep (oldest)" : $"duplicate of {group.Keeper.Path}",
        }, f != group.Keeper))
        .ToList();
}
