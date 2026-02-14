namespace BookTranslator.Models;

public enum BlockKind { H1, H2, P }

public sealed record StructuredBlock(BlockKind Kind, string Text);