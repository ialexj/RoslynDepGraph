namespace CascadeFixture;

// A is referenced only by B. B is itself unused, so after B is pruned, A
// becomes unused and must be pruned in a later pass (fixpoint).
public class A
{
    public void DoWork() { }
}

public class B
{
    public void UseA()
    {
        var a = new A();
        a.DoWork();
    }
}
