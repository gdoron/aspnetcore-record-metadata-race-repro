namespace AspNetCoreRecordMetadataRaceRepro;

// A plain nest of positional records, the shape of an ordinary [FromBody] request contract.
// Nothing here is special: any record whose primary constructor parameters map to properties works.

public sealed record ToolDefinition(string Name, string Description);

public sealed record ToolSet(ToolDefinition[] Definitions);

public sealed record Message(string Role, string Content);

public sealed record Conversation(Message[] Messages, string[] System, ToolSet Tools);

public sealed record RequestMetadata(string RequestId, DateTimeOffset SentAt);

public sealed record ProcessConversationRequest(Conversation Conversation, RequestMetadata Metadata);
