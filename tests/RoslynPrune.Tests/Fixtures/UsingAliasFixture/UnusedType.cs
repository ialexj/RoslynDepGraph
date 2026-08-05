namespace UsingAliasFixture;

// UnusedType is referenced only by the using alias below, so it is pruned and
// the alias is removed.
public class UnusedType
{
    public void DoWork() { }
}
