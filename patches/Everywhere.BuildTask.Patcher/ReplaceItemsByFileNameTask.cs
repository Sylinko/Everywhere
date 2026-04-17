using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Task = Microsoft.Build.Utilities.Task;

namespace Everywhere.BuildTask.Patcher;

public class ReplaceItemsByFileNameTask : Task
{
    [Required]
    public ITaskItem[] OriginalItems { get; set; } = [];

    [Required]
    public ITaskItem[] ReplacementItems { get; set; } = [];

    [Output]
    public ITaskItem[] ItemsToRemove { get; set; } = [];

    [Output]
    public ITaskItem[] ItemsToAdd { get; set; } = [];

    public override bool Execute()
    {
        var replacements = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ReplacementItems)
            replacements[Path.GetFileName(r.ItemSpec)] = r;

        var toRemove = new List<ITaskItem>();
        var toAdd = new List<ITaskItem>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in OriginalItems)
        {
            var fileName = Path.GetFileName(item.ItemSpec);
            if (!replacements.TryGetValue(fileName, out var replacement))
                continue;

            toRemove.Add(item);
            if (added.Add(fileName))
            {
                var newItem = new TaskItem(replacement.ItemSpec);
                item.CopyMetadataTo(newItem);
                toAdd.Add(newItem);
                Log.LogMessage(MessageImportance.High,
                    "[Patcher] Publish list: {0} \u2192 {1}", item.ItemSpec, replacement.ItemSpec);
            }
            else
            {
                Log.LogMessage(MessageImportance.Normal,
                    "[Patcher] Publish list: removed duplicate {0}", item.ItemSpec);
            }
        }

        ItemsToRemove = toRemove.ToArray();
        ItemsToAdd = toAdd.ToArray();
        return true;
    }
}
