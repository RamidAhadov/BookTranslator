namespace BookTranslator.Models;

public enum BlockKind { H1, H2, P, Code }

public sealed record StructuredBlock(BlockKind Kind, string Text);
