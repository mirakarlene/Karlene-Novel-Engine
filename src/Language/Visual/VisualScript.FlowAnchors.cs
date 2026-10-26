namespace KNE;

public sealed partial class VisualScript
{
    private static void ValidateAnchorScopes(Dictionary<string, ScriptNode> map, string start, HashSet<string> connectedTriggers)
    {
        var contexts = map.Keys.ToDictionary(id => id, _ => new HashSet<(string Owner, string? Stop)>(), StringComparer.Ordinal);
        var pending = new Queue<(string Node, string Owner, string? Stop)>();
        pending.Enqueue((start, "main", null));
        foreach (var trigger in map.Values.Where(node => node.IsTrigger && !connectedTriggers.Contains(node.Id)))
            pending.Enqueue((trigger.Id, "trigger:" + trigger.Id, null));
        Drain();
        bool changed;
        do
        {
            changed = false;
            foreach (var send in map.Values.Where(node => node.Kind == ScriptNodeKind.SendFlow))
            {
                if (!map.TryGetValue(send.Target, out var target) || target.Kind != ScriptNodeKind.Anchor)
                    Fail(send, "выберите существующий якорь этой сцены");
                foreach (var context in contexts[send.Id].ToArray())
                {
                    if (contexts[send.Target].Any(candidate => candidate.Owner == context.Owner)) continue;
                    if (contexts[send.Target].Count != 0) Fail(send, "якорь принадлежит другому потоку этой сцены");
                    pending.Enqueue((send.Target, context.Owner, context.Stop));
                    Drain(); changed = true;
                }
            }
        } while (changed);

        void Drain()
        {
            while (pending.TryDequeue(out var item))
            {
                if (item.Node == item.Stop || !contexts[item.Node].Add((item.Owner, item.Stop))) continue;
                var node = map[item.Node];
                if (node.Kind is ScriptNodeKind.Parallel or ScriptNodeKind.Async)
                {
                    for (int index = 0; index < node.Options.Count; index++)
                        pending.Enqueue((node.Options[index].Target!, "branch:" + node.Id + ":" + index, node.Next));
                    pending.Enqueue((node.Next!, item.Owner, item.Stop));
                }
                else
                    foreach (var (_, next) in node.Outputs())
                        if (next != null) pending.Enqueue((next, item.Owner, item.Stop));
            }
        }
    }
}
