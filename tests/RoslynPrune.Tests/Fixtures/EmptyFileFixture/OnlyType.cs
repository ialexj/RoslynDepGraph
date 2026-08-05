namespace EmptyFileFixture;

// This is the only type in this file. When it is pruned, the file should be
// removed entirely rather than left with just the file-scoped namespace.
public class OnlyTypeInFile
{
    public void DoWork() { }
}
