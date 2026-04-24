using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Memory;

public interface IMemoryManager
{
    string LongTermMemory { get; }

    void EnqueueLongTermMemoryUpdateTask(ChatContext chatContext, Assistant assistant, CancellationToken cancellationToken);
}